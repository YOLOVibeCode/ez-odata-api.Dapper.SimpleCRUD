using EzOdata.Core.Query;

namespace EzOdata.SimpleCrud.AspNetCore;

/// <summary>Small builders for ez-odata's Query IR, for <see cref="EzTableHandler{T}.BeforeReadAsync"/>.</summary>
public static class EzFilter
{
    public static FilterNode Eq(string column, object? value) => new ComparisonNode(new FieldRef(column), ComparisonOp.Eq, new ConstantValue(value));
    public static FilterNode Ne(string column, object? value) => new ComparisonNode(new FieldRef(column), ComparisonOp.Ne, new ConstantValue(value));

    public static FilterNode And(FilterNode? left, FilterNode right) =>
        left is null ? right : new LogicalNode(LogicalOp.And, [left, right]);

    /// <summary>AND a predicate into the query (never widens what the policy engine allowed).</summary>
    public static QueryRequest Where(this QueryRequest query, FilterNode predicate) =>
        query with { Filter = And(query.Filter, predicate) };
}
