﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace LinqToDB.Linq.Builder
{
	using Extensions;
	using LinqToDB.Common;
	using LinqToDB.Expressions;
	using Mapping;
	using Reflection;
	using SqlQuery;

	partial class TableBuilder
	{
		public class TableContext : IBuildContext
		{
			#region Properties

#if DEBUG
			public string _sqlQueryText => SelectQuery == null ? "" : SelectQuery.SqlText;
			public string Path => this.GetPath();
#endif

			public ExpressionBuilder   Builder     { get; }
			public Expression?         Expression  { get; }

			public SelectQuery         SelectQuery { get; set; }
			public SqlStatement?       Statement   { get; set; }

			public List<Tuple<MemberInfo, Expression?>[]>? LoadWith    { get; set; }

			public virtual IBuildContext? Parent   { get; set; }

			public Type             OriginalType = null!;
			public Type             ObjectType = null!;
			public EntityDescriptor EntityDescriptor = null!;
			public SqlTable         SqlTable = null!;

			internal bool           ForceLeftJoinAssociations { get; set; }

			private readonly bool   _associationsToSubQueries;
			#endregion

			#region Init

			public TableContext(ExpressionBuilder builder, BuildInfo buildInfo, Type originalType)
			{
				Builder          = builder;
				Parent           = buildInfo.Parent;
				Expression       = buildInfo.Expression;
				SelectQuery      = buildInfo.SelectQuery;
				_associationsToSubQueries = buildInfo.AssociationsAsSubQueries;

				OriginalType     = originalType;
				ObjectType       = GetObjectType();
				SqlTable         = new SqlTable(builder.MappingSchema, ObjectType);
				EntityDescriptor = Builder.MappingSchema.GetEntityDescriptor(ObjectType);

				SelectQuery.From.Table(SqlTable);

				Init(true);
			}

			public TableContext(ExpressionBuilder builder, BuildInfo buildInfo, SqlTable table)
			{
				Builder          = builder;
				Parent           = buildInfo.Parent;
				Expression       = buildInfo.Expression;
				SelectQuery      = buildInfo.SelectQuery;
				_associationsToSubQueries = buildInfo.AssociationsAsSubQueries;

				OriginalType     = table.ObjectType!;
				ObjectType       = GetObjectType();
				SqlTable         = table;
				EntityDescriptor = Builder.MappingSchema.GetEntityDescriptor(ObjectType);

				if (SqlTable.SqlTableType != SqlTableType.SystemTable)
					SelectQuery.From.Table(SqlTable);

				Init(true);
			}

			internal TableContext(ExpressionBuilder builder, SelectQuery selectQuery, SqlTable table)
			{
				Builder          = builder;
				Parent           = null;
				Expression       = null;
				SelectQuery      = selectQuery;

				OriginalType     = table.ObjectType!;
				ObjectType       = GetObjectType();
				SqlTable         = table;
				EntityDescriptor = Builder.MappingSchema.GetEntityDescriptor(ObjectType);

				if (SqlTable.SqlTableType != SqlTableType.SystemTable)
				SelectQuery.From.Table(SqlTable);

				Init(true);
			}

			protected TableContext(ExpressionBuilder builder, SelectQuery selectQuery)
			{
				Builder     = builder;
				SelectQuery = selectQuery;
			}

			public TableContext(ExpressionBuilder builder, BuildInfo buildInfo)
			{
				Builder     = builder;
				Parent      = buildInfo.Parent;
				Expression  = buildInfo.Expression;
				SelectQuery = buildInfo.SelectQuery;
				_associationsToSubQueries = buildInfo.AssociationsAsSubQueries;

				var mc   = (MethodCallExpression)Expression;
				var attr = builder.GetTableFunctionAttribute(mc.Method)!;

				if (!typeof(ITable<>).IsSameOrParentOf(mc.Method.ReturnType))
					throw new LinqException("Table function has to return Table<T>.");

				OriginalType     = mc.Method.ReturnType.GetGenericArguments()[0];
				ObjectType       = GetObjectType();
				SqlTable         = new SqlTable(builder.MappingSchema, ObjectType);
				EntityDescriptor = Builder.MappingSchema.GetEntityDescriptor(ObjectType);

				SelectQuery.From.Table(SqlTable);

				var args = mc.Arguments.Select(a => builder.ConvertToSql(this, a));

				attr.SetTable(Builder.MappingSchema, SqlTable, mc.Method, mc.Arguments, args);

				Init(true);
			}

			protected Type GetObjectType()
			{
				for (var type = OriginalType.BaseType; type != null && type != typeof(object); type = type.BaseType)
				{
					var mapping = Builder.MappingSchema.GetEntityDescriptor(type).InheritanceMapping;

					if (mapping.Count > 0)
						return type;
				}

				return OriginalType;
			}

			public List<InheritanceMapping> InheritanceMapping = null!;

			protected void Init(bool applyFilters)
			{
				Builder.Contexts.Add(this);

				InheritanceMapping = EntityDescriptor.InheritanceMapping;

				// Original table is a parent.
				//
				if (applyFilters && ObjectType != OriginalType)
				{
					var predicate = Builder.MakeIsPredicate(this, OriginalType);

					if (predicate.GetType() != typeof(SqlPredicate.Expr))
						SelectQuery.Where.SearchCondition.Conditions.Add(new SqlCondition(false, predicate));
				}
			}

			#endregion

			#region BuildQuery

			static object DefaultInheritanceMappingException(object value, Type type)
			{
				throw new LinqException("Inheritance mapping is not defined for discriminator value '{0}' in the '{1}' hierarchy.", value, type);
			}

			Dictionary<MemberInfo, Expression>? _loadWithCache;

			void SetLoadWithBindings(Type objectType, ParameterExpression parentObject, List<Expression> exprs)
			{
				var loadWith = GetLoadWith();

				if (loadWith == null)
					return;

				var members = GetLoadWith(loadWith);

				foreach (var member in members)
				{
					if (member.MemberInfo.DeclaringType.IsAssignableFrom(objectType))
					{
						var ma = Expression.MakeMemberAccess(Expression.Constant(null, objectType), member.MemberInfo);

						if (member.NextLoadWith.Count > 0)
						{
							var table = FindTable(ma, 1, false, true)!;
							table.Table.LoadWith = member.NextLoadWith;
						}

						var attr = Builder.MappingSchema.GetAttribute<AssociationAttribute>(member.MemberInfo.ReflectedType, member.MemberInfo);


						if (_loadWithCache == null || !_loadWithCache.TryGetValue(member.MemberInfo, out var ex))
						{
							ex = BuildExpression(ma, 1, parentObject);
							if (_loadWithCache == null)
								_loadWithCache = new Dictionary<MemberInfo, Expression>();
							_loadWithCache.Add(member.MemberInfo, ex);
						}

						if (member.MemberInfo.IsDynamicColumnPropertyEx())
						{
							var typeAcc = TypeAccessor.GetAccessor(member.MemberInfo.ReflectedType);
							var setter  = new MemberAccessor(typeAcc, member.MemberInfo, EntityDescriptor).SetterExpression;

							exprs.Add(Expression.Invoke(setter, parentObject, ex));
						}
						else
						{
							exprs.Add(Expression.Assign(
								attr?.Storage != null
									? ExpressionHelper.PropertyOrField(parentObject, attr.Storage)
									: Expression.MakeMemberAccess(parentObject, member.MemberInfo),
								ex));
						}
					}
				}
			}

			static bool IsRecord(Attribute[] attrs)
			{
				return attrs.Any(attr => attr.GetType().FullName == "Microsoft.FSharp.Core.CompilationMappingAttribute")
					&& attrs.All(attr => attr.GetType().FullName != "Microsoft.FSharp.Core.CLIMutableAttribute");
			}

			bool IsAnonymous(Type type)
			{
				if (!type.IsPublic &&
					 type.IsGenericType &&
					(type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal) ||
					 type.Name.StartsWith("VB$AnonymousType",   StringComparison.Ordinal)))
				{
					return Builder.MappingSchema.GetAttribute<CompilerGeneratedAttribute>(type) != null;
				}

				return false;
			}

			ParameterExpression? _variable;

			Expression BuildTableExpression(bool buildBlock, Type objectType, int[] index)
			{
				if (buildBlock && _variable != null)
					return _variable;

				var entityDescriptor = Builder.MappingSchema.GetEntityDescriptor(objectType);

				// choosing type that can be instantiated
				if ((objectType.IsInterface || objectType.IsAbstract) && !(ObjectType.IsInterface || ObjectType.IsAbstract))
				{
					objectType = ObjectType;
				}

				var expr =
					IsRecord(Builder.MappingSchema.GetAttributes<Attribute>(objectType)) ?
						BuildRecordConstructor (entityDescriptor, objectType, index, true) :
					IsAnonymous(objectType) ?
						BuildRecordConstructor (entityDescriptor, objectType, index, false) :
						BuildDefaultConstructor(entityDescriptor, objectType, index);

				expr = BuildCalculatedColumns(entityDescriptor, expr);
				expr = ProcessExpression(expr);
				expr = NotifyEntityCreated(expr);

				if (!buildBlock)
					return expr;

				return _variable = Builder.BuildVariable(expr);
			}

			[UsedImplicitly]
			static object OnEntityCreated(IDataContext context, object entity)
			{
				var onEntityCreated = context.OnEntityCreated;

				if (onEntityCreated != null)
				{
					var args = new EntityCreatedEventArgs
					{
						Entity      = entity,
						DataContext = context
					};

					onEntityCreated(args);

					return args.Entity;
				}

				return entity;
			}

			Expression NotifyEntityCreated(Expression expr)
			{
				if (Builder.DataContext is IEntityServices)
				{
//					var cex = Expression.Convert(ExpressionBuilder.DataContextParam, typeof(INotifyEntityCreated));
//
//					expr =
//						Expression.Convert(
//							Expression.Call(
//								cex,
//								MemberHelper.MethodOf((INotifyEntityCreated n) => n.EntityCreated(null)),
//								expr),
//							expr.Type);
					expr =
						Expression.Convert(
							Expression.Call(
								MemberHelper.MethodOf(() => OnEntityCreated(null!, null!)),
								ExpressionBuilder.DataContextParam,
								expr),
							expr.Type);
				}


				return expr;
			}

			Expression BuildCalculatedColumns(EntityDescriptor entityDescriptor, Expression expr)
			{
				if (!entityDescriptor.HasCalculatedMembers)
					return expr;

				var isBlockDisable = Builder.IsBlockDisable;

				Builder.IsBlockDisable = true;

				var variable    = Expression.Variable(expr.Type, expr.Type.ToString());
				var expressions = new List<Expression>
				{
					Expression.Assign(variable, expr)
				};

				foreach (var member in entityDescriptor.CalculatedMembers!)
				{
					var accessExpression    = Expression.MakeMemberAccess(variable, member.MemberInfo);
					var convertedExpression = Builder.ConvertExpressionTree(accessExpression);
					var selectorLambda      = Expression.Lambda(convertedExpression, variable);

					IBuildContext context;
					var buildInfo = new BuildInfo(this, selectorLambda.Body, new SelectQuery());

					if (Builder.IsSequence(buildInfo))
					{
						var expressionCtx = new ExpressionContext(Parent, this, selectorLambda);
						buildInfo         = new BuildInfo(expressionCtx, selectorLambda.Body, new SelectQuery());
						context           = Builder.BuildSequence(buildInfo);
						Builder.ReplaceParent(expressionCtx, this);
					}
					else
					{
						context = new SelectContext(Parent, selectorLambda, this);
					}

					var expression          = context.BuildExpression(null, 0, false);

					expressions.Add(Expression.Assign(accessExpression, expression));
				}

				expressions.Add(variable);

				Builder.IsBlockDisable = isBlockDisable;

				return Expression.Block(new[] { variable }, expressions);
			}

			Expression BuildDefaultConstructor(EntityDescriptor entityDescriptor, Type objectType, int[] index)
			{
				var members =
				(
					from idx in index.Select((n,i) => new { n, i })
					where idx.n >= 0
					let   cd = entityDescriptor.Columns[idx.i]
					where
						cd.Storage != null ||
						!(cd.MemberAccessor.MemberInfo is PropertyInfo) ||
						((PropertyInfo)cd.MemberAccessor.MemberInfo).GetSetMethod(true) != null
					select new
					{
						Column = cd,
						Expr   = new ConvertFromDataReaderExpression(cd.StorageType, idx.n, Builder.DataReaderLocal)
					}
				).ToList();

				var initExpr = Expression.MemberInit(
					Expression.New(objectType),
						members
							// IMPORTANT: refactoring this condition will affect hasComplex variable calculation below
							.Where (m => !m.Column.MemberAccessor.IsComplex)
							.Select(m => (MemberBinding)Expression.Bind(m.Column.StorageInfo, m.Expr)));

				Expression expr = initExpr;

				var hasComplex = members.Count > initExpr.Bindings.Count;
				var loadWith   = GetLoadWith();

				if (hasComplex || loadWith != null)
				{
					var obj   = Expression.Variable(expr.Type);
					var exprs = new List<Expression> { Expression.Assign(obj, expr) };

					if (hasComplex)
					{
						exprs.AddRange(
							members.Where(m => m.Column.MemberAccessor.IsComplex).Select(m =>
								m.Column.MemberAccessor.SetterExpression!.GetBody(obj, m.Expr)));
					}

					if (loadWith != null)
					{
						SetLoadWithBindings(objectType, obj, exprs);
					}

					exprs.Add(obj);

					expr = Expression.Block(new[] { obj }, exprs);
				}

				return expr;
			}

			class ColumnInfo
			{
				public bool       IsComplex;
				public string     Name = null!;
				public Expression Expression = null!;
			}

			IEnumerable<Expression?>? GetExpressions(TypeAccessor typeAccessor, bool isRecordType, List<ColumnInfo> columns)
			{
				var members = isRecordType ?
					typeAccessor.Members.Where(m =>
						IsRecord(Builder.MappingSchema.GetAttributes<Attribute>(typeAccessor.Type, m.MemberInfo))) :
					typeAccessor.Members;

				var loadWith      = GetLoadWith();
				var loadWithItems = loadWith == null ? new List<LoadWithItem>() : GetLoadWith(loadWith);

				foreach (var member in members)
				{
					var column = columns.FirstOrDefault(c => !c.IsComplex && c.Name == member.Name);

					if (column != null)
					{
						yield return column.Expression;
					}
					else
					{
						var assocAttr = Builder.MappingSchema.GetAttributes<AssociationAttribute>(typeAccessor.Type, member.MemberInfo).FirstOrDefault();
						var isAssociation = assocAttr != null;

						if (isAssociation)
						{
							var loadWithItem = loadWithItems.FirstOrDefault(_ => _.MemberInfo == member.MemberInfo);
							if (loadWithItem != null)
							{
								var ma = Expression.MakeMemberAccess(Expression.Constant(null, typeAccessor.Type), member.MemberInfo);
								if (loadWithItem.NextLoadWith.Count > 0)
								{
									var table = FindTable(ma, 1, false, true);
									table!.Table.LoadWith = loadWithItem.NextLoadWith;
								}
								yield return BuildExpression(ma, 1, false);
							}
						}
						else
						{
							var name = member.Name + '.';
							var cols = columns.Where(c => c.IsComplex && c.Name.StartsWith(name)).ToList();

							if (cols.Count == 0)
							{
								yield return null;
							}
							else
							{
								foreach (var col in cols)
								{
									col.Name      = col.Name.Substring(name.Length);
									col.IsComplex = col.Name.Contains(".");
								}

								var typeAcc  = TypeAccessor.GetAccessor(member.Type);
								var isRecord = IsRecord(Builder.MappingSchema.GetAttributes<Attribute>(member.Type));

								var exprs = GetExpressions(typeAcc, isRecord, cols).ToList();

								if (isRecord)
								{
									var ctor      = member.Type.GetConstructors().Single();
									var ctorParms = ctor.GetParameters();

									var parms =
										(
										from p in ctorParms.Select((p, i) => new { p, i })
										join e in exprs.Select((e, i) => new { e, i }) on p.i equals e.i into j
											from e in j.DefaultIfEmpty()
											select
											e?.e ?? Expression.Constant(p.p.DefaultValue ?? Builder.MappingSchema.GetDefaultValue(p.p.ParameterType), p.p.ParameterType)
										).ToList();

									yield return Expression.New(ctor, parms);
								}
								else
								{
									var expr = Expression.MemberInit(
										Expression.New(member.Type),
										from m in typeAcc.Members.Zip(exprs, (m,e) => new { m, e })
										where m.e != null
										select (MemberBinding)Expression.Bind(m.m.MemberInfo, m.e));

									yield return expr;
								}
							}
						}
					}
				}
			}

			Expression BuildRecordConstructor(EntityDescriptor entityDescriptor, Type objectType, int[] index, bool isRecord)
			{
				var ctor = objectType.GetConstructors().Single();

				var exprs = GetExpressions(entityDescriptor.TypeAccessor, isRecord,
					(
						from idx in index.Select((n,i) => new { n, i })
						where idx.n >= 0
						let   cd = entityDescriptor.Columns[idx.i]
						select new ColumnInfo
						{
							IsComplex  = cd.MemberAccessor.IsComplex,
							Name       = cd.MemberName,
							Expression = new ConvertFromDataReaderExpression(cd.MemberType, idx.n, Builder.DataReaderLocal)
						}
					).ToList()).ToList();

				var parms =
				(
					from p in ctor.GetParameters().Select((p,i) => new { p, i })
					join e in exprs.Select((e,i) => new { e, i }) on p.i equals e.i into j
					from e in j.DefaultIfEmpty()
					select e?.e ?? Expression.Constant(Builder.MappingSchema.GetDefaultValue(p.p.ParameterType), p.p.ParameterType)
				).ToList();

				var expr = Expression.New(ctor, parms);

				return expr;
			}

			protected virtual Expression ProcessExpression(Expression expression)
			{
				return expression;
			}

			int[] BuildIndex(int[] index, Type objectType)
			{
				var names = new Dictionary<string,int>();
				var n     = 0;
				var ed    = Builder.MappingSchema.GetEntityDescriptor(objectType);

				foreach (var cd in ed.Columns)
					if (cd.MemberAccessor.TypeAccessor.Type == ed.TypeAccessor.Type)
						names.Add(cd.MemberName, n++);

				var q =
					from r in SqlTable.Fields.Values.Select((f,i) => new { f, i })
					where names.ContainsKey(r.f.Name)
					orderby names[r.f.Name]
					select index[r.i];

				return q.ToArray();
			}

			protected virtual Expression BuildQuery(Type tableType, TableContext tableContext, ParameterExpression? parentObject)
			{
				SqlInfo[] info;

				var isScalar = IsScalarType(tableType);
				if (isScalar)
				{
					info = ConvertToIndex(null, 0, ConvertFlags.All);
					if (info.Length != 1)
						throw new LinqToDBException($"Invalid scalar type processing for type '{tableType.Name}'.");
					var parentIndex = ConvertToParentIndex(info[0].Index, null);
					return Builder.BuildSql(tableType, parentIndex);
				}

				if (ObjectType == tableType)
				{
					info = ConvertToIndex(null, 0, ConvertFlags.All);
				}
				else
				{
					info = ConvertToSql(null, 0, ConvertFlags.All);

					var table = new SqlTable(Builder.MappingSchema, tableType);

					var matchedFields = new List<SqlInfo>();
					foreach (var field in table.Fields.Values)
					{
						foreach (var sqlInfo in info)
						{
							var sqlField = (SqlField)sqlInfo.Sql;
							var found = sqlField.Name == field.Name;

							if (!found)
							{
								found = EntityDescriptor.Aliases != null &&
								        EntityDescriptor.Aliases.TryGetValue(field.Name, out var alias) &&
								        alias == sqlField.Name;
							}

							if (found)
							{
								matchedFields.Add(GetIndex(sqlInfo));
								break;
							}
						}
					}

					info = matchedFields.ToArray();
				}

				var index = info.Select(idx => ConvertToParentIndex(idx.Index, null)).ToArray();

				if (ObjectType != tableType || InheritanceMapping.Count == 0)
					return BuildTableExpression(!Builder.IsBlockDisable, tableType, index);

				Expression expr;

				var defaultMapping = InheritanceMapping.SingleOrDefault(m => m.IsDefault);

				if (defaultMapping != null)
				{
					expr = Expression.Convert(
						BuildTableExpression(false, defaultMapping.Type, BuildIndex(index, defaultMapping.Type)),
						ObjectType);
				}
				else
				{
					if (tableContext is AssociatedTableContext)
					{
						expr = Expression.Constant(null, ObjectType);
					}
					else
					{
					var exceptionMethod = MemberHelper.MethodOf(() => DefaultInheritanceMappingException(null!, null!));
					var dindex          =
						(
							from f in SqlTable.Fields.Values
							where f.Name == InheritanceMapping[0].DiscriminatorName
							select ConvertToParentIndex(_indexes[f].Index, null)
						).First();

					expr = Expression.Convert(
						Expression.Call(null, exceptionMethod,
							Expression.Call(
								ExpressionBuilder.DataReaderParam,
								ReflectionHelper.DataReader.GetValue,
								Expression.Constant(dindex)),
							Expression.Constant(ObjectType)),
						ObjectType);
				}
				}

				foreach (var mapping in InheritanceMapping.Select((m,i) => new { m, i }).Where(m => m.m != defaultMapping))
				{
					var dindex =
						(
							from f in SqlTable.Fields.Values
							where f.Name == InheritanceMapping[mapping.i].DiscriminatorName
							select ConvertToParentIndex(_indexes[f].Index, null)
						).First();

					Expression testExpr;

					var isNullExpr = Expression.Call(
						ExpressionBuilder.DataReaderParam,
						ReflectionHelper.DataReader.IsDBNull,
						Expression.Constant(dindex));

					if (mapping.m.Code == null)
					{
						testExpr = isNullExpr;
					}
					else
					{
						var codeType = mapping.m.Code.GetType();

						testExpr = ExpressionBuilder.Equal(
							Builder.MappingSchema,
							Builder.BuildSql(codeType, dindex),
							Expression.Constant(mapping.m.Code));

						if (mapping.m.Discriminator.CanBeNull)
						{
							testExpr =
								Expression.AndAlso(
									Expression.Not(isNullExpr),
									testExpr);
						}
					}

					expr = Expression.Condition(
						testExpr,
						Expression.Convert(BuildTableExpression(false, mapping.m.Type, BuildIndex(index, mapping.m.Type)), ObjectType),
						expr);
				}

				return expr;
			}

			private bool IsScalarType(Type tableType)
			{
				return tableType.IsArray || Builder.MappingSchema.IsScalarType(tableType);
			}

			public virtual void BuildQuery<T>(Query<T> query, ParameterExpression queryParameter)
			{
				var expr   = BuildQuery(typeof(T), this, null);
				var mapper = Builder.BuildMapper<T>(expr);

				QueryRunner.SetRunQuery(query, mapper);
			}

			#endregion

			#region BuildExpression

			public virtual Expression BuildExpression(Expression? expression, int level, bool enforceServerSide)
			{
				return BuildExpression(expression, level, null);
			}

			Expression BuildExpression(Expression? expression, int level, ParameterExpression? parentObject)
			{
				// Build table.
				//
				var table = FindTable(expression, level, false, false);

				if (table == null)
				{
					if (expression is MemberExpression memberExpression)
					{
						if (EntityDescriptor != null &&
							EntityDescriptor.TypeAccessor.Type == memberExpression.Member.DeclaringType)
						{
							return new DefaultValueExpression(Builder.MappingSchema, memberExpression.Type);
						}
					}

					throw new LinqException("'{0}' cannot be converted to SQL.", expression);
				}

				if (table.Field == null)
				{
					Expression? expr = null;
					
					if (expression is MemberExpression memberExpression)
					{
						// workaround for not mapped properties

						var orginalType = table.Table.OriginalType;
						if (table.Table is AssociatedTableContext association)
						{
							if (association.IsList)
								orginalType = typeof(IEnumerable<>).MakeGenericType(orginalType);
						}
						
						if (!orginalType.IsSameOrParentOf(memberExpression.Type))
							expr = new DefaultValueExpression(Builder.MappingSchema, memberExpression.Type);
					}
					
					if (expr == null)
						expr = table.Table.BuildQuery(table.Table.OriginalType, table.Table, parentObject);

					return expr;
				}

				// Build field.
				//
				var info = ConvertToIndex(expression, level, ConvertFlags.Field).Single();
				var idx  = ConvertToParentIndex(info.Index, null);

				return Builder.BuildSql(expression!, idx);
			}

			#endregion

			#region ConvertToSql

			public virtual SqlInfo[] ConvertToSql(Expression? expression, int level, ConvertFlags flags)
			{
				switch (flags)
				{
					case ConvertFlags.All   :
						{
							var table = FindTable(expression, level, false, true)!;

							if (table.Field == null)
							{
								SqlInfo[] result;

								if (!IsScalarType(OriginalType))
								{
									// Handling case with Associations. Needs refactoring
									if (table.Table != this && table.Table is AssociatedTableContext association && association._innerContext != null)
									{
										return association._innerContext.ConvertToSql(null, level, flags);
									}
								
									result = table.Table.SqlTable.Fields.Values
										.Where(f => !f.IsDynamic)
										.Select(f =>
											f.ColumnDescriptor != null
												? new SqlInfo(f.ColumnDescriptor.MemberInfo) { Sql = f }
												: new SqlInfo { Sql = f })
										.ToArray();

								}
								else
								{
									ISqlExpression sql = SqlTable;
									if (SqlTable is SqlRawSqlTable)
									{
										sql                  = SqlTable.All;
										((SqlField)sql).Type = ((SqlField)sql).Type?.WithSystemType(OriginalType) ?? new DbDataType(OriginalType);
									}

									result = new[]
									{
										new SqlInfo(Enumerable.Empty<MemberInfo>()) { Sql = sql }
									};
								}

								return result;
							}
							break;
						}

					case ConvertFlags.Key   :
						{
							var table = FindTable(expression, level, false, true)!;

							if (table.Field == null)
							{
								// Handling case with Associations. Needs refactoring
								if (table.Table != this && table.Table is AssociatedTableContext association && association._innerContext != null)
								{
									return association._innerContext.ConvertToSql(null, level, flags);
								}

								var q =
									from f in table.Table.SqlTable.Fields.Values
									where f.IsPrimaryKey
									orderby f.PrimaryKeyOrder
									select new SqlInfo(f.ColumnDescriptor.MemberInfo)
									{
										Sql = _associationsToSubQueries
											? table.Table.SelectQuery.Select.Field(f).SelectQuery
											: (ISqlExpression)f
									};

								var key = q.ToArray();

								return key.Length != 0 ? key : ConvertToSql(expression, level, ConvertFlags.All);
							}

							break;
						}

					case ConvertFlags.Field :
						{
							var table = FindTable(expression, level, true, true)!;

							if (table.Field != null)
								return new[]
								{
									// ???
									new SqlInfo(QueryHelper.GetUnderlyingField(table.Field)?.ColumnDescriptor.MemberInfo!) { Sql = table.Field }
								};

							if (expression == null)
								return new[]
								{
									new SqlInfo { Sql = IsScalarType(OriginalType) ? (ISqlExpression)SqlTable : table.Table.SqlTable.All }
								};

							break;
						}
				}

				throw new NotImplementedException();
			}

			#endregion

			#region ConvertToIndex

			readonly Dictionary<ISqlExpression,SqlInfo> _indexes = new Dictionary<ISqlExpression,SqlInfo>();

			protected virtual SqlInfo GetIndex(SqlInfo expr)
			{
				if (_indexes.TryGetValue(expr.Sql, out var n))
					return n;

				if (expr.Sql is SqlField field)
				{
					expr.Index = SelectQuery.Select.Add(field, field.Alias);
				}
				else
				{
					expr.Index = SelectQuery.Select.Add(expr.Sql);
				}

				expr.Query = SelectQuery;

				_indexes.Add(expr.Sql, expr);

				return expr;
			}

			public virtual SqlInfo[] ConvertToIndex(Expression? expression, int level, ConvertFlags flags)
			{
				switch (flags)
				{
					case ConvertFlags.Field :
					case ConvertFlags.Key   :
					case ConvertFlags.All   :

						var info = ConvertToSql(expression, level, flags);

						for (var i = 0; i < info.Length; i++)
							info[i] = GetIndex(info[i]);

						return info;
				}

				throw new NotImplementedException();
			}

			#endregion

			#region IsExpression

			public virtual IsExpressionResult IsExpression(Expression? expression, int level, RequestFor requestFor)
			{
				switch (requestFor)
				{
					case RequestFor.Field      :
						{
							var table = FindTable(expression, level, false, false);
							return new IsExpressionResult(table?.Field != null);
						}

					case RequestFor.Table       :
					case RequestFor.Object      :
						{
							var table   = FindTable(expression, level, false, false);
							var isTable =
								table       != null &&
								table.Field == null &&
								(expression == null || expression.GetLevelExpression(Builder.MappingSchema, table.Level) == expression);

							return new IsExpressionResult(isTable, isTable ? table!.Table : null);
						}

					case RequestFor.Expression :
						{
							if (expression == null)
								return IsExpressionResult.False;

							var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

							switch (levelExpression.NodeType)
							{
								case ExpressionType.MemberAccess :
								case ExpressionType.Parameter    :
								case ExpressionType.Call         :

									var table = FindTable(expression, level, false, false);
									return new IsExpressionResult(table == null);
							}

							return IsExpressionResult.True;
						}

					case RequestFor.Association      :
						{
							if (EntityDescriptor.Associations.Count > 0)
							{
								var table = FindTable(expression, level, false, false);
								var isat  =
									table?.Table is AssociatedTableContext &&
									table.Field == null &&
									(expression == null || expression.GetLevelExpression(Builder.MappingSchema, table.Level) == expression);

								return new IsExpressionResult(isat, isat ? table!.Table : null);
							}

							return IsExpressionResult.False;
						}
				}

				return IsExpressionResult.False;
			}

			#endregion

			#region GetContext

			interface IAssociationHelper
			{
				Expression GetExpression(Expression parent, AssociatedTableContext association);
			}

			class AssociationHelper<T> : IAssociationHelper
				where T : class
			{
				public Expression GetExpression(Expression parent, AssociatedTableContext association)
				{	
					IQueryable<T> expression = association.Builder.DataContext.GetTable<T>();

					var loadWith = association.GetLoadWith();

					if (loadWith != null)
					{
						foreach (var members in loadWith)
						{
							var pLoadWith  = Expression.Parameter(typeof(T), "t");
							var isPrevList = false;

							Expression obj = pLoadWith;

							foreach (var member in members)
							{
								if (isPrevList)
									obj = new GetItemExpression(obj, association.Builder.MappingSchema);

								obj = Expression.MakeMemberAccess(obj, member.Item1);

								isPrevList = typeof(IEnumerable).IsSameOrParentOf(obj.Type);
							}

							var queryFilter = members[members.Length - 1].Item2;

							if (queryFilter == null)
							{
								var method = Methods.LinqToDB.LoadWith.MakeGenericMethod(typeof(T), obj.Type);

								var lambda = Expression.Lambda(obj, pLoadWith);
								expression = (IQueryable<T>)method.Invoke(null, new object[] { expression, lambda });
							}
							else
							{
//								var method =
//									(isPrevList
//										? Methods.LinqToDB.LoadWithQueryMany
//										: Methods.LinqToDB.LoadWithQuerySingle).MakeGenericMethod(typeof(T), EagerLoading.GetEnumerableElementType(obj.Type, association.Builder.MappingSchema));
								var method =
									(isPrevList
										? Methods.LinqToDB.LoadWithQueryMany
										: Methods.LinqToDB.LoadWithQuerySingle).MakeGenericMethod(typeof(T), EagerLoading.GetEnumerableElementType(obj.Type, association.Builder.MappingSchema));

								if (isPrevList)
									obj = EagerLoading.EnsureEnumerable(obj, association.Builder.MappingSchema);

								var lambda = Expression.Lambda(obj, pLoadWith);

								expression = (IQueryable<T>)method.Invoke(null, new object[] { expression, lambda, queryFilter.EvaluateExpression()! });
							}
						}
					}

					Expression? expr  = null;
					var         param = Expression.Parameter(typeof(T), "c");

					var queryMethod = association.Association.GetQueryMethod(parent.Type, typeof(T));

					if (queryMethod != null)
					{
						expr = queryMethod.GetBody(parent, Expression.Constant(association.Builder.DataContext));
						return expr;
					}

					var conditions = association.ParentAssociationJoin?.Condition ?? association.SelectQuery.Where.SearchCondition;
					foreach (var cond in conditions.Conditions.Take(association.RegularConditionCount))
					{
						SqlPredicate.ExprExpr p;

						if (cond.Predicate is SqlSearchCondition condition)
						{
							p = condition.Conditions
								.Select(c => c.Predicate)
								.OfType<SqlPredicate.ExprExpr>()
								.First();
						}
						else
						{
							p = (SqlPredicate.ExprExpr)cond.Predicate;
						}

						if (parent is UnaryExpression parentUnaryExpression)
						{
							if (parentUnaryExpression.NodeType == ExpressionType.Convert &&
							    parentUnaryExpression.Type.IsAssignableFrom(parentUnaryExpression.Operand.Type))
							{
								// When parentUnaryExpression.Type is an interface and
								// parentUnaryExpression.Operand.Type is generic type that implements the interface type
								// var e1 = Expression.MakeMemberAccess (see below) fails,
								// because ((SqlField)p.Expr1).ColumnDescriptor.MemberInfo is declared in parentUnaryExpression.Operand.Type
								// and of course is missing in parentUnaryExpression.Type
								parent = parentUnaryExpression.Operand;
							}
						} 
						
						var e1 = Expression.MakeMemberAccess(parent, ((SqlField)p.Expr1).ColumnDescriptor.MemberInfo);
						var e2 = Expression.MakeMemberAccess(param,  ((SqlField)p.Expr2).ColumnDescriptor.MemberInfo);

//						while (e1.Type != e2.Type)
//						{
//							if (e1.Type.IsNullable())
//							{
//								e1 = Expression.PropertyOrField(e1, "Value");
//								continue;
//							}
//
//							if (e2.Type.IsNullable())
//							{
//								e2 = Expression.PropertyOrField(e2, "Value");
//								continue;
//							}
//
//							e2 = Expression.Convert(e2, e1.Type);
//						}

						var ex = ExpressionBuilder.Equal(association.Builder.MappingSchema, e1, e2);

						expr = expr == null ? ex : Expression.AndAlso(expr, ex);
					}

					if (association.ExpressionPredicate != null)
					{
						var l  = association.ExpressionPredicate;
						var ex = l.GetBody(parent, param);

						expr = expr == null ? ex : Expression.AndAlso(expr, ex);
					}

					if (expr == null)
						throw new LinqToDBException("Invalid association for LoadWith");

					var predicate = Expression.Lambda<Func<T,bool>>(expr, param);

					return expression.Where(predicate).Expression;
				}
			}

			public IBuildContext GetContext(Expression? expression, int level, BuildInfo buildInfo)
			{
				if (expression == null)
				{
					if (buildInfo != null && buildInfo.IsSubQuery)
					{
						var table = new TableContext(
							Builder,
							new BuildInfo(Parent is SelectManyBuilder.SelectManyContext ? this : Parent, Expression!, buildInfo.SelectQuery),
							SqlTable.ObjectType!);

						return table;
					}

					return this;
				}

//				if (EntityDescriptor.Associations.Count > 0)
				{

					if (buildInfo != null && buildInfo.IsSubQuery)
					{
						var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

						if (levelExpression == expression && expression.NodeType == ExpressionType.MemberAccess ||
						    expression.NodeType == ExpressionType.Call)
						{
							var tableLevel  = GetAssociation(expression, level)!;
							var association = (AssociatedTableContext)tableLevel.Table;

							if (association.IsList)
							{
								var ma     = expression.NodeType == ExpressionType.MemberAccess
												? ((MemberExpression)buildInfo.Expression).Expression
												: expression.NodeType == ExpressionType.Call
												? ((MethodCallExpression)buildInfo.Expression).Arguments[0]
												: buildInfo.Expression.GetRootObject(Builder.MappingSchema);

								var atype  = typeof(AssociationHelper<>).MakeGenericType(association.ObjectType);
								var helper = (IAssociationHelper)Activator.CreateInstance(atype);
								var expr   = helper.GetExpression(ma, association);

								buildInfo.IsAssociationBuilt = true;

								if (association.ParentAssociationJoin != null && (tableLevel.IsNew || buildInfo.CopyTable))
									association.ParentAssociationJoin.IsWeak = true;

								return Builder.BuildSequence(new BuildInfo(buildInfo, expr));
							}
						}
						else
						{
							var association = GetAssociation(levelExpression, level)!;
							((AssociatedTableContext)association.Table).ParentAssociationJoin!.IsWeak = false;

//							var paj         = ((AssociatedTableContext)association.Table).ParentAssociationJoin;
//
//							paj.IsWeak = paj.IsWeak && buildInfo.CopyTable;

							return association.Table.GetContext(expression, level + 1, buildInfo);
						}
					}
				}

				throw new InvalidOperationException();
			}

			public virtual SqlStatement GetResultStatement()
			{
				return Statement ?? (Statement = new SqlSelectStatement(SelectQuery));
			}

			#endregion

			#region ConvertToParentIndex

			public virtual int ConvertToParentIndex(int index, IBuildContext? context)
			{
				return Parent?.ConvertToParentIndex(index, this) ?? index;
			}

			#endregion

			#region SetAlias

			public void SetAlias(string alias)
			{
				if (alias == null)
					return;

				if (!alias.Contains('<'))
					if (SqlTable.Alias == null)
						SqlTable.Alias = alias;
			}

			#endregion

			#region GetSubQuery

			public ISqlExpression? GetSubQuery(IBuildContext context)
			{
				return null;
			}

			#endregion

			#region Helpers

			protected class LoadWithItem
			{
				public MemberInfo         MemberInfo   = null!;
				public List<Tuple<MemberInfo, Expression?>[]> NextLoadWith = null!;
			}

			protected List<LoadWithItem> GetLoadWith(List<Tuple<MemberInfo, Expression?>[]> infos)
			{
				return
				(
					from lw in infos
					select new
					{
						head = lw.First(),
						tail = lw.Skip(1).ToArray()
					}
					into info
					group info by info.head.Item1 into gr
					select new LoadWithItem
					{
						MemberInfo   = gr.Key,
						NextLoadWith = (from i in gr where i.tail.Length > 0 select i.tail).ToList()
					}
				).ToList();
			}

			protected internal virtual List<Tuple<MemberInfo, Expression?>[]>? GetLoadWith()
			{
				return LoadWith;
			}

			protected virtual ISqlExpression? GetField(Expression expression, int level, bool throwException)
			{
				if (expression.NodeType == ExpressionType.MemberAccess)
				{
					var memberExpression = (MemberExpression)expression;

					if (EntityDescriptor.Aliases != null)
					{
						if (EntityDescriptor.Aliases.TryGetValue(memberExpression.Member.Name, out var aliasName))
						{
							var alias = EntityDescriptor[aliasName!];

							if (alias == null)
							{
								foreach (var column in EntityDescriptor.Columns)
								{
									if (column.MemberInfo.EqualsTo(memberExpression.Member, SqlTable.ObjectType))
									{
										expression = memberExpression = ExpressionHelper.PropertyOrField(
											Expression.Convert(memberExpression.Expression, column.MemberInfo.DeclaringType), column.MemberName);
										break;
									}
								}
							}
							else
							{
								var expr = memberExpression.Expression;

								if (alias.MemberInfo.DeclaringType != memberExpression.Member.DeclaringType)
									expr = Expression.Convert(memberExpression.Expression, alias.MemberInfo.DeclaringType);

								expression = memberExpression = ExpressionHelper.PropertyOrField(expr, alias.MemberName);
							}
						}
					}

					var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

					if (levelExpression.NodeType == ExpressionType.MemberAccess)
					{
						if (levelExpression != expression)
						{
							var levelMember = (MemberExpression)levelExpression;

							if (memberExpression.Member.IsNullableValueMember() && memberExpression.Expression == levelExpression)
								memberExpression = levelMember;
							else
							{
								var sameType =
									levelMember.Member.ReflectedType == SqlTable.ObjectType ||
									levelMember.Member.DeclaringType     == SqlTable.ObjectType;

								if (!sameType)
								{
									var mi = SqlTable.ObjectType!.GetInstanceMemberEx(levelMember.Member.Name);
									sameType = mi.Any(_ => _.DeclaringType == levelMember.Member.DeclaringType);
								}

								if (sameType || InheritanceMapping.Count > 0)
								{
									string? pathName = null;
									foreach (var field in SqlTable.Fields.Values)
									{
										var name = levelMember.Member.Name;
										if (field.Name.IndexOf('.') >= 0)
										{
											if (pathName == null)
											{
												var suffix = string.Empty;
												for (var ex = (MemberExpression)expression;
													ex != levelMember;
													ex = (MemberExpression)ex.Expression)
												{
													suffix = string.IsNullOrEmpty(suffix)
														? ex.Member.Name
														: ex.Member.Name + "." + suffix;
												}

												pathName = !string.IsNullOrEmpty(suffix) ? name + "." + suffix : name;
											}
											
											if (field.Name == pathName)
												return field;
										}
										else if (field.Name == name)
											return field;
									}
								}
							}
						}

						if (levelExpression == memberExpression)
						{
							foreach (var field in SqlTable.Fields.Values)
							{
								if (field.ColumnDescriptor.MemberInfo.EqualsTo(memberExpression.Member, SqlTable.ObjectType))
								{
									if (field.ColumnDescriptor.MemberAccessor.IsComplex
										&& !field.ColumnDescriptor.MemberAccessor.MemberInfo.IsDynamicColumnPropertyEx())
									{
										var name = memberExpression.Member.Name;
										var me   = memberExpression;

										if (me.Expression is MemberExpression)
										{
											while (me.Expression is MemberExpression)
											{
												me = (MemberExpression)me.Expression;
												name = me.Member.Name + '.' + name;
											}

											var fld = SqlTable.Fields.Values.FirstOrDefault(f => f.Name == name);

											if (fld != null)
												return fld;
										}
									}
									else
									{
										return field;
									}
								}

								if (InheritanceMapping.Count > 0 && field.Name == memberExpression.Member.Name)
									foreach (var mapping in InheritanceMapping)
										foreach (var mm in Builder.MappingSchema.GetEntityDescriptor(mapping.Type).Columns)
											if (mm.MemberAccessor.MemberInfo.EqualsTo(memberExpression.Member))
												return field;

								if (memberExpression.Member.IsDynamicColumnPropertyEx())
								{
									var fieldName = memberExpression.Member.Name;

									// do not add association columns
									if (EntityDescriptor.Associations.All(a => a.MemberInfo != memberExpression.Member))
									{
										if (!SqlTable.Fields.TryGetValue(fieldName, out var newField))
										{
											newField = new SqlField(new ColumnDescriptor(
												Builder.MappingSchema,
												new ColumnAttribute(fieldName),
												new MemberAccessor(EntityDescriptor.TypeAccessor, memberExpression.Member, EntityDescriptor)))
												{
													IsDynamic        = true,
												};

											SqlTable.Add(newField);
										}

										return newField;
									}
								}
							}

							if (throwException &&
								EntityDescriptor != null &&
								EntityDescriptor.TypeAccessor.Type == memberExpression.Member.DeclaringType)
							{
								throw new LinqException("Member '{0}.{1}' is not a table column.",
									memberExpression.Member.DeclaringType.Name, memberExpression.Member.Name);
							}
						}
					}
				}

				return null;
			}

			readonly Dictionary<MemberInfo,AssociatedTableContext> _associations =
				new Dictionary<MemberInfo,AssociatedTableContext>(new MemberInfoComparer());

			class TableLevel
			{
				public TableContext    Table = null!;
				public ISqlExpression? Field;
				public int             Level;
				public bool            IsNew;
			}

			TableLevel? FindTable(Expression? expression, int level, bool throwException, bool throwExceptionForNull)
			{
				if (expression == null)
					return new TableLevel { Table = this };

				var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);

				TableLevel? result = null;

				switch (levelExpression.NodeType)
				{
					case ExpressionType.MemberAccess :
					case ExpressionType.Parameter    :
						{
							var field = GetField(expression, level, throwException);

							if (field != null || (level == 0 && levelExpression == expression))
								return new TableLevel { Table = this, Field = field, Level = level };

							goto case ExpressionType.Call;
						}
					case ExpressionType.Call         :
							result = GetAssociation(expression, level);
							break;
				}

				if (throwExceptionForNull && result == null)
					throw new LinqException($"Expression '{expression}' ({levelExpression}) is not a table.");

				return result;
			}

			TableLevel? GetAssociation(Expression expression, int level)
			{
				var objectMapper    = EntityDescriptor;
				var levelExpression = expression.GetLevelExpression(Builder.MappingSchema, level);
				var inheritance     =
					(
						from m in objectMapper.InheritanceMapping
						let om = Builder.MappingSchema.GetEntityDescriptor(m.Type)
						where om.Associations.Count > 0
						select om
					).ToList();

				AssociatedTableContext? tableAssociation = null;
				var isNew = false;

				if (levelExpression.NodeType == ExpressionType.Call)
				{
					var mc = (MethodCallExpression) levelExpression;
					var aa = Builder.MappingSchema.GetAttribute<AssociationAttribute>(mc.Method.DeclaringType, mc.Method, a => a.Configuration);

					if (aa != null)
						tableAssociation = new AssociatedTableContext(
							Builder,
							this,
							new AssociationDescriptor(
								EntityDescriptor.ObjectType,
								mc.Method,
								aa.GetThisKeys(),
								aa.GetOtherKeys(),
								aa.ExpressionPredicate,
								aa.Predicate,
								aa.QueryExpressionMethod,
								aa.QueryExpression,
								aa.Storage,
								aa.CanBeNull,
								aa.AliasName),
							ForceLeftJoinAssociations,
							_associationsToSubQueries);

					isNew = true;
				}

				if (tableAssociation == null && levelExpression.NodeType == ExpressionType.MemberAccess && objectMapper.Associations.Count > 0 || inheritance.Count > 0)
				{

					var memberExpression = (MemberExpression)levelExpression;

					// subquery association shouldn't be cached, because different assciation navigation pathes
					// should produce different subqueries
					if (_associationsToSubQueries || !_associations.TryGetValue(memberExpression.Member, out tableAssociation))
					{
						var q =
							from a in objectMapper.Associations.Concat(inheritance.SelectMany(om => om.Associations))
							where a.MemberInfo.EqualsTo(memberExpression.Member)
							select new AssociatedTableContext(Builder, this, a, ForceLeftJoinAssociations, _associationsToSubQueries);

						tableAssociation = q.FirstOrDefault();

						isNew = true;

						if (!_associationsToSubQueries)
							_associations.Add(memberExpression.Member, tableAssociation);
					}
				}

				if (tableAssociation != null)
				{
					if (levelExpression == expression)
						return new TableLevel { Table = tableAssociation, Level = level, IsNew = isNew };

					var al = tableAssociation.GetAssociation(expression, level + 1);

					if (al != null)
						return al;

					var field = tableAssociation.GetField(expression, level + 1, false);

					if (_associationsToSubQueries)
					{
						tableAssociation.SelectQuery.Select.Columns.Clear();
						if (field != null)
							tableAssociation.SelectQuery.Select.Add(field);
						field = tableAssociation.SelectQuery;
					}

					return new TableLevel { Table = tableAssociation, Field = field, Level = field == null ? level : level + 1, IsNew = isNew };
				}

				return null;
			}

			#endregion
		}
	}
}
