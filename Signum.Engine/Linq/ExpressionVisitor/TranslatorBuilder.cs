﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Linq.Expressions;
using Signum.Utilities;
using System.Diagnostics;
using Signum.Utilities.DataStructures;
using Signum.Entities;
using Signum.Engine;
using Signum.Entities.Reflection;
using Signum.Utilities.Reflection;
using Signum.Utilities.ExpressionTrees;
using System.Collections;
using Signum.Engine.Maps;
using System.Data.Common;

namespace Signum.Engine.Linq
{  
    internal static class TranslatorBuilder
    {
        internal static ITranslateResult Build(ProjectionExpression proj)
        {
            Type type = proj.UniqueFunction == null ? proj.Type.ElementType() : proj.Type;

            return miBuildPrivate.GetInvoker(type)(proj);
        }

        static GenericInvoker<Func<ProjectionExpression, ITranslateResult>> miBuildPrivate = new GenericInvoker<Func<ProjectionExpression, ITranslateResult>>(pe => BuildPrivate<int>(pe));

        static TranslateResult<T> BuildPrivate<T>(ProjectionExpression proj)
        {
            var eagerChildProjections = EagerChildProjectionGatherer.Gatherer(proj).Select(cp => BuildChild(cp)).ToList();
            var lazyChildProjections = LazyChildProjectionGatherer.Gatherer(proj).Select(cp => BuildChild(cp)).ToList();

            Scope scope = new Scope
            {
                Alias = proj.Select.Alias,
                Positions = proj.Select.Columns.Select((c, i) => new { c.Name, i }).ToDictionary(p => p.Name, p => p.i),
            };

            Expression<Func<IProjectionRow, T>> lambda = ProjectionBuilder.Build<T>(proj.Projector, scope);

            Expression<Func<DbParameter[]>> createParams;
            string sql = QueryFormatter.Format(proj.Select, out createParams);

            var result = new TranslateResult<T>
            {
                EagerProjections = eagerChildProjections,
                LazyChildProjections = lazyChildProjections,

                CommandText = sql,

                ProjectorExpression = lambda,

                GetParameters = createParams.Compile(),
                GetParametersExpression = createParams,

                Unique = proj.UniqueFunction,
            };

            return result;
        }

        static IChildProjection BuildChild(ChildProjectionExpression childProj)
        {
            var proj = childProj.Projection;

            Type type = proj.UniqueFunction == null ? proj.Type.ElementType() : proj.Type;

            if(!type.IsInstantiationOf(typeof(KeyValuePair<,>)))
                throw new InvalidOperationException("All child projections should create KeyValuePairs");

            return miBuildChildPrivate.GetInvoker(type.GetGenericArguments())(childProj);
        }

        static GenericInvoker<Func<ChildProjectionExpression, IChildProjection>> miBuildChildPrivate = new GenericInvoker<Func<ChildProjectionExpression, IChildProjection>>(proj => BuildChildPrivate<int, bool>(proj));

        static IChildProjection BuildChildPrivate<K, V>(ChildProjectionExpression childProj)
        {
            var proj = childProj.Projection;

            Scope scope = new Scope
            {
                Alias = proj.Select.Alias,
                Positions = proj.Select.Columns.Select((c, i) => new { c.Name, i }).ToDictionary(p => p.Name, p => p.i),
            };

            Expression<Func<IProjectionRow, KeyValuePair<K, V>>> lambda = ProjectionBuilder.Build<KeyValuePair<K, V>>(proj.Projector, scope);

            Expression<Func<DbParameter[]>> createParamsExpression;
            string sql = QueryFormatter.Format(proj.Select, out createParamsExpression);
            Func<DbParameter[]> createParams = createParamsExpression.Compile();

            if (childProj.IsLazyMList)
                return new LazyChildProjection<K, V>
                {
                    Name = proj.Token,

                    CommandText = sql,
                    ProjectorExpression = lambda,

                    GetParameters = createParams,
                    GetParametersExpression = createParamsExpression,
                };
            else
                return new EagerChildProjection<K, V>
                {
                    Name = proj.Token,

                    CommandText = sql,
                    ProjectorExpression = lambda,

                    GetParameters = createParams,
                    GetParametersExpression = createParamsExpression,
                };
        }

        public static CommandResult BuildCommandResult(CommandExpression command)
        {
            Expression<Func<DbParameter[]>> createParams;
            string sql = QueryFormatter.Format(command, out createParams);

            return new CommandResult
            {
                GetParameters = createParams.Compile(),
                GetParametersExpression = createParams,
                CommandText = sql,
            }; 
        }

        public class LazyChildProjectionGatherer : DbExpressionVisitor
        {
            List<ChildProjectionExpression> list = new List<ChildProjectionExpression>();

            public static List<ChildProjectionExpression> Gatherer(ProjectionExpression proj)
            {
                LazyChildProjectionGatherer pg = new LazyChildProjectionGatherer();

                pg.Visit(proj);

                return pg.list; 
            }

            protected override Expression VisitChildProjection(ChildProjectionExpression child)
            {
                if (child.IsLazyMList)
                    list.Add(child);
                
                var result =  base.VisitChildProjection(child);

                return result;
            }
        }

        public class EagerChildProjectionGatherer : DbExpressionVisitor
        {
            List<ChildProjectionExpression> list = new List<ChildProjectionExpression>();

            public static List<ChildProjectionExpression> Gatherer(ProjectionExpression proj)
            {
                EagerChildProjectionGatherer pg = new EagerChildProjectionGatherer();

                pg.Visit(proj);

                return pg.list;
            }

            protected override Expression VisitChildProjection(ChildProjectionExpression child)
            {
                var result = base.VisitChildProjection(child);

                if (!child.IsLazyMList)
                    list.Add(child);

                return result;
            }
        }

        /// <summary>
        /// ProjectionBuilder is a visitor that converts an projector expression
        /// that constructs result objects out of ColumnExpressions into an actual
        /// LambdaExpression that constructs result objects out of accessing fields
        /// of a ProjectionRow
        /// </summary>
        public class ProjectionBuilder : DbExpressionVisitor
        {
            static ParameterExpression row = Expression.Parameter(typeof(IProjectionRow), "row");

            static PropertyInfo piRetriever = ReflectionTools.GetPropertyInfo((IProjectionRow r) => r.Retriever);
            static MemberExpression retriever = Expression.Property(row, piRetriever); 
           
            static FieldInfo fiId = ReflectionTools.GetFieldInfo((IdentifiableEntity i) => i.id);

            static MethodInfo miCached = ReflectionTools.GetMethodInfo((IRetriever r) => r.Complete<TypeDN>(null, null)).GetGenericMethodDefinition();
            static MethodInfo miRequest = ReflectionTools.GetMethodInfo((IRetriever r) => r.Request<TypeDN>(null)).GetGenericMethodDefinition();
            static MethodInfo miRequestIBA = ReflectionTools.GetMethodInfo((IRetriever r) => r.RequestIBA<TypeDN>(1, 1)).GetGenericMethodDefinition();
            static MethodInfo miRequestLite = ReflectionTools.GetMethodInfo((IRetriever r) => r.RequestLite<TypeDN>(1, null)).GetGenericMethodDefinition();
            static MethodInfo miEmbeddedPostRetrieving = ReflectionTools.GetMethodInfo((IRetriever r) => r.EmbeddedPostRetrieving<EmbeddedEntity>(null)).GetGenericMethodDefinition();

            Scope scope; 
        

            static internal Expression<Func<IProjectionRow, T>> Build<T>(Expression expression, Scope scope)
            {
                ProjectionBuilder pb = new ProjectionBuilder() { scope = scope };
                Expression body = pb.Visit(expression);
                return Expression.Lambda<Func<IProjectionRow, T>>(body, row);
            }

            Expression NullifyColumn(Expression exp)
            {
                ColumnExpression ce = exp as ColumnExpression;
                if (ce == null)
                    return exp;

                if (ce.Type.IsNullable() || ce.Type.IsClass)
                    return ce;

                return new ColumnExpression(ce.Type.Nullify(), ce.Alias, ce.Name);
            }

            protected override Expression VisitUnary(UnaryExpression u)
            {
                if (u.NodeType == ExpressionType.Convert && u.Operand is ColumnExpression && DiffersInNullability(u.Type, u.Operand.Type))
                {
                    ColumnExpression column = (ColumnExpression)u.Operand;
                    return scope.GetColumnExpression(row, column.Alias, column.Name, u.Type);
                }

                return base.VisitUnary(u);
            }

            bool DiffersInNullability(Type a, Type b)
            {
                return
                    a.IsValueType && a.Nullify() == b ||
                    b.IsValueType && b.Nullify() == a;
            }

            protected override Expression VisitColumn(ColumnExpression column)
            {
                return scope.GetColumnExpression(row, column.Alias, column.Name, column.Type);
            }

            protected override Expression VisitChildProjection(ChildProjectionExpression child)
            {
                Expression outer = Visit(child.OuterKey);

                if (outer != child.OuterKey)
                    child = new ChildProjectionExpression(child.Projection, outer, child.IsLazyMList, child.Type); 

                return scope.LookupEager(row, child);
            }

            protected Expression VisitMListChildProjection(ChildProjectionExpression child, MemberExpression field)
            {
                Expression outer = Visit(child.OuterKey);

                if (outer != child.OuterKey)
                    child = new ChildProjectionExpression(child.Projection, outer, child.IsLazyMList, child.Type);

                return scope.LookupMList(row, child, field);
            }

            protected override Expression VisitProjection(ProjectionExpression proj)
            {
                throw new InvalidOperationException("No ProjectionExpressions expected at this stage"); 
            }


            protected override Expression VisitFieldInit(FieldInitExpression fieldInit)
            {
                Expression id = Visit(NullifyColumn(fieldInit.ExternalId));

                if (fieldInit.TableAlias == null)
                    return Expression.Call(retriever, miRequest.MakeGenericMethod(fieldInit.Type), id);

                ParameterExpression e = Expression.Parameter(fieldInit.Type, fieldInit.Type.Name.ToLower().Substring(0, 1));

                var block = Expression.Block(
                    fieldInit.Bindings
                    .Where(a => !ReflectionTools.FieldEquals(FieldInitExpression.IdField, a.FieldInfo))
                    .Select(b =>
                        {
                            var field = Expression.Field(e, b.FieldInfo);

                            var value = b.Binding is ChildProjectionExpression ? 
                                VisitMListChildProjection((ChildProjectionExpression)b.Binding, field) :
                                Convert(Visit(b.Binding), b.FieldInfo.FieldType);

                            return Expression.Assign(field, value);
                        }
                    ));

                LambdaExpression lambda = Expression.Lambda(typeof(Action<>).MakeGenericType(fieldInit.Type), block, e);

                return Expression.Call(retriever, miCached.MakeGenericMethod(fieldInit.Type), id, lambda);
            }

            private Expression Convert(Expression expression, Type type)
            {
                if (expression.Type == type)
                    return expression;

                return Expression.Convert(expression, type); 
            }

            static PropertyInfo piModified = ReflectionTools.GetPropertyInfo((ModifiableEntity me) => me.Modified);

            static MemberBinding resetModified = Expression.Bind(piModified, Expression.Constant(null, typeof(bool?)));

            protected override Expression VisitEmbeddedFieldInit(EmbeddedFieldInitExpression efie)
            {
                Expression ctor = Expression.MemberInit(Expression.New(efie.Type),
                       efie.Bindings.Select(b => Expression.Bind(b.FieldInfo, Visit(b.Binding))).And(resetModified));

                var entity = Expression.Call(retriever, miEmbeddedPostRetrieving.MakeGenericMethod(efie.Type), ctor);

                if (efie.HasValue == null)
                    return entity;

                return Expression.Condition(Expression.Equal(Visit(efie.HasValue.Nullify()), Expression.Constant(true, typeof(bool?))), entity, Expression.Constant(null, ctor.Type));
            }

            protected override Expression VisitImplementedBy(ImplementedByExpression rb)
            {
                return rb.Implementations.Select(fie => new When(Visit(fie.Field.ExternalId).NotEqualsNulll(), Visit(fie.Field))).ToCondition(rb.Type);
            }

            protected override Expression VisitImplementedByAll(ImplementedByAllExpression rba)
            {
                return Expression.Call(retriever, miRequestIBA.MakeGenericMethod(rba.Type),
                    Visit(NullifyColumn(rba.Id)),
                    Visit(NullifyColumn(rba.TypeId.TypeColumn)));
            }

            static readonly ConstantExpression NullType = Expression.Constant(null, typeof(Type));
            static readonly ConstantExpression NullId = Expression.Constant(null, typeof(int?));

            protected override Expression VisitTypeFieldInit(TypeFieldInitExpression typeFie)
            {
                return Expression.Condition(
                    Expression.NotEqual(Visit(NullifyColumn(typeFie.ExternalId)), NullId),
                    Expression.Constant(typeFie.TypeValue, typeof(Type)),
                    NullType);
            }
     
            protected override Expression VisitTypeImplementedBy(TypeImplementedByExpression typeIb)
            {
                return typeIb.TypeImplementations.Reverse().Aggregate((Expression)NullType, (acum, imp) => Expression.Condition(
                    Expression.NotEqual(Visit(NullifyColumn(imp.ExternalId)), NullId),
                    Expression.Constant(imp.Type, typeof(Type)),
                    acum));
            }

            static MethodInfo miGetType = ReflectionTools.GetMethodInfo((Schema s) => s.GetType(1));

            protected override Expression VisitTypeImplementedByAll(TypeImplementedByAllExpression typeIba)
            {
                return Expression.Condition(
                    Expression.NotEqual(Visit(NullifyColumn(typeIba.TypeColumn)), NullId),
                    Expression.Call(Expression.Constant(Schema.Current), miGetType, Visit(typeIba.TypeColumn).UnNullify()),
                    NullType);
            }

            protected override Expression VisitLiteReference(LiteReferenceExpression lite)
            {
                var id = Visit(NullifyColumn(lite.Id));
                var toStr = Visit(lite.ToStr);

                Type liteType = Lite.Extract(lite.Type);

                if (id == null)
                    return Expression.Constant(null, lite.Type);
                else if (toStr == null)
                {
                    var type = Visit(lite.TypeId);

                    return Expression.Call(retriever, miRequestLite.MakeGenericMethod(liteType), id.Nullify(), type);
                }
                else
                {
                    var type = Visit(lite.TypeId);

                    Type constantTypeId = ConstantType(type);
             
                    NewExpression liteConstructor;
                    if (constantTypeId == liteType)
                    {
                        ConstructorInfo ciLite = lite.Type.GetConstructor(new[] { typeof(int), typeof(string) });
                        liteConstructor = Expression.New(ciLite, id.UnNullify(), toStr);
                    }
                    else
                    {
                        ConstructorInfo ciLite = lite.Type.GetConstructor(new[] { typeof(Type), typeof(int), typeof(string) });
                        liteConstructor = Expression.New(ciLite, type, id.UnNullify(), toStr);
                    }

                    return Expression.Condition(id.NotEqualsNulll(), liteConstructor, Expression.Constant(null, lite.Type));
                }
            }

            protected override Expression VisitMListElement(MListElementExpression mle)
            {
                Type type = mle.Type;

                return Expression.MemberInit(Expression.New(type),
                    Expression.Bind(type.GetProperty("RowId"), Visit(mle.RowId)),
                    Expression.Bind(type.GetProperty("Parent"), Visit(mle.Parent)),
                    Expression.Bind(type.GetProperty("Element"), Visit(mle.Element)));
            }

            private Type ConstantType(Expression typeId)
            {
                if (typeId.NodeType == ExpressionType.Convert)
                    typeId = ((UnaryExpression)typeId).Operand;

                if (typeId.NodeType == ExpressionType.Constant)
                    return (Type)((ConstantExpression)typeId).Value;

                return null;
            }

            static ConstructorInfo ciLite = ReflectionTools.GetConstuctorInfo(() => new Lite<IdentifiableEntity>(typeof(IdentifiableEntity), 2, ""));

            protected override Expression VisitSqlConstant(SqlConstantExpression sce)
            {
                return Expression.Constant(sce.Value, sce.Type);
            }
        }
    }

    internal class Scope
    {
        public Alias Alias;

        public Dictionary<string, int> Positions;

        static PropertyInfo miReader = ReflectionTools.GetPropertyInfo((IProjectionRow row) => row.Reader);

        public Expression GetColumnExpression(Expression row, Alias alias, string name, Type type)
        {
            if (alias != Alias)
                throw new InvalidOperationException("alias '{0}' not found".Formato(alias));

            int position = Positions.GetOrThrow(name, "column name '{0}' not found in alias '" + alias + "'");

            return FieldReader.GetExpression(Expression.Property(row, miReader), position, type);
        }

        static MethodInfo miLookupRequest = ReflectionTools.GetMethodInfo((IProjectionRow row) => row.LookupRequest<int, double>(null, 0, null)).GetGenericMethodDefinition();
        static MethodInfo miLookup = ReflectionTools.GetMethodInfo((IProjectionRow row) => row.Lookup<int, double>(null, 0)).GetGenericMethodDefinition();

        public Expression LookupEager(Expression row, ChildProjectionExpression cProj)
        {
            if (cProj.IsLazyMList)
                throw new InvalidOperationException("IsLazyMList not expected at this stage");

            Type type = cProj.Projection.UniqueFunction == null ? cProj.Type.ElementType() : cProj.Type;

            MethodInfo mi = miLookup.MakeGenericMethod(cProj.OuterKey.Type, type);

            Expression call = Expression.Call(row, mi, Expression.Constant(cProj.Projection.Token), cProj.OuterKey);

            if (cProj.Projection.UniqueFunction == null)
                return call;

            MethodInfo miUnique = UniqueMethod(cProj.Projection.UniqueFunction.Value);
            return Expression.Call(miUnique.MakeGenericMethod(type), call);
        }

        public Expression LookupMList(Expression row, ChildProjectionExpression cProj, MemberExpression field)
        {
            if (!cProj.IsLazyMList)
                throw new InvalidOperationException("Not IsLazyMList not expected at this stage");

            if (!cProj.Type.IsMList())
                throw new InvalidOperationException("Lazy ChildProyection of type '{0}' instead of MList".Formato(cProj.Type.TypeName()));

            if (cProj.Projection.UniqueFunction != null)
                throw new InvalidOperationException("Lazy ChildProyection with UniqueFunction '{0}'".Formato(cProj.Projection.UniqueFunction));

            MethodInfo mi = miLookupRequest.MakeGenericMethod(cProj.OuterKey.Type, cProj.Type.ElementType());

            return Expression.Call(row, mi, Expression.Constant(cProj.Projection.Token), cProj.OuterKey, field);
        }

        static MethodInfo miSingle = ReflectionTools.GetMethodInfo(() => EnumerableUniqueExtensions.SingleEx<int>(null)).GetGenericMethodDefinition();
        static MethodInfo miSingleOrDefault = ReflectionTools.GetMethodInfo(() => EnumerableUniqueExtensions.SingleOrDefaultEx<int>(null)).GetGenericMethodDefinition();
        static MethodInfo miFirst = ReflectionTools.GetMethodInfo(() => EnumerableUniqueExtensions.FirstEx<int>(null)).GetGenericMethodDefinition();
        static MethodInfo miFirstOrDefault = ReflectionTools.GetMethodInfo(() => Enumerable.FirstOrDefault<int>(null)).GetGenericMethodDefinition();

        internal MethodInfo UniqueMethod(UniqueFunction uniqueFunction)
        {
            switch (uniqueFunction)
            {
                case UniqueFunction.First: return miFirst;
                case UniqueFunction.FirstOrDefault: return miFirstOrDefault;
                case UniqueFunction.Single: return miSingle;
                case UniqueFunction.SingleOrDefault: return miSingleOrDefault;
                default:
                    throw new InvalidOperationException();
            }
        }
    }
}
