using EzOdata.Core.Query;

namespace EzOdata.SimpleCrud.AspNetCore;

/// <summary>Small builders for ez-odata's Query IR, for <see cref="EzTableHandler{T}.BeforeReadAsync"/>.</summary>
public static class EzFilter
{
    /// <summary><c>column eq value</c> (API column name).</summary>
    public static FilterNode Eq(string column, object? value) => new ComparisonNode(new FieldRef(column), ComparisonOp.Eq, new ConstantValue(value));
    /// <summary><c>column ne value</c> (API column name).</summary>
    public static FilterNode Ne(string column, object? value) => new ComparisonNode(new FieldRef(column), ComparisonOp.Ne, new ConstantValue(value));

    /// <summary><paramref name="left"/> AND <paramref name="right"/>; just <paramref name="right"/> when <paramref name="left"/> is null.</summary>
    public static FilterNode And(FilterNode? left, FilterNode right) =>
        left is null ? right : new LogicalNode(LogicalOp.And, [left, right]);

    /// <summary>AND a predicate into the query (never widens what the policy engine allowed).</summary>
    public static QueryRequest Where(this QueryRequest query, FilterNode predicate) =>
        query with { Filter = And(query.Filter, predicate) };
}
