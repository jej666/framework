﻿using Signum.Utilities;
using Signum.Utilities.ExpressionTrees;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Signum.Engine.Linq
{
    class ScalarSubqueryRewriter : DbExpressionVisitor
    {
        SourceExpression currentFrom;

        Connector connector = Connector.Current; 
        
        bool inAggregate = false;

        public static Expression Rewrite(Expression expression)
        {
            return new ScalarSubqueryRewriter().Visit(expression);
        }

        protected override Expression VisitAggregate(AggregateExpression aggregate)
        {
            var saveInAggregate = this.inAggregate;

            this.inAggregate = true;

            var result = base.VisitAggregate(aggregate);

            this.inAggregate = saveInAggregate;

            return result;
        }

        protected override Expression VisitSelect(SelectExpression select)
        {
            var saveFrom = this.currentFrom;
            var saveInAggregate = this.inAggregate;

            this.inAggregate = false;

            SourceExpression from = this.VisitSource(select.From);
            this.currentFrom = from;

            Expression top = this.Visit(select.Top);
            Expression where = this.Visit(select.Where);
            ReadOnlyCollection<ColumnDeclaration> columns = select.Columns.NewIfChange(VisitColumnDeclaration);
            ReadOnlyCollection<OrderExpression> orderBy = select.OrderBy.NewIfChange(VisitOrderBy);
            ReadOnlyCollection<Expression> groupBy = select.GroupBy.NewIfChange(Visit);

            from = this.currentFrom;

            this.inAggregate = saveInAggregate;
            this.currentFrom = saveFrom;

            if (top != select.Top || from != select.From || where != select.Where || columns != select.Columns || orderBy != select.OrderBy || groupBy != select.GroupBy)
                return new SelectExpression(select.Alias, select.IsDistinct, select.IsReverse, top, columns, from, where, orderBy, groupBy);

            return select;

        }

        protected override Expression VisitScalar(ScalarExpression scalar)
        {
            if (connector.SupportsScalarSubquery &&
               (!inAggregate || connector.SupportsScalarSubqueryInAggregates))
            {
                return base.VisitScalar(scalar);
            }
            else
            {
                var select = scalar.Select;
                if (string.IsNullOrEmpty(select.Columns[0].Name))
                {
                    select = new SelectExpression(select.Alias, select.IsDistinct, select.IsReverse, select.Top,
                        new[] { new ColumnDeclaration("scalar", select.Columns[0].Expression) },
                        select.From, select.Where, select.OrderBy, select.GroupBy);
                }
                this.currentFrom = new JoinExpression(JoinType.OuterApply, this.currentFrom, select, null);
                return new ColumnExpression(scalar.Type, scalar.Select.Alias, select.Columns[0].Name);
            }
        }
    }
}