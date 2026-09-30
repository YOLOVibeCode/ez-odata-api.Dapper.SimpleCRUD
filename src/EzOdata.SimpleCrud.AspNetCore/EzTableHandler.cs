using EzOdata.Core.Query;

namespace EzOdata.SimpleCrud.AspNetCore;

/// <summary>
/// Override point for one table, the typed equivalent of DreamFactory's pre/post-process scripts.
/// Every member has a working default (plain SimpleCRUD), so override only what you need.
/// Handlers are created from request services, so constructor injection works.
/// </summary>
/// <remarks>
/// Hooks run AFTER ez-odata's policy engine: role rules, row filters and field policies have already
/// been applied to the query / write, and still apply to whatever the handler does through the API.
/// Write hooks run inside the write's transaction; throwing (or <see cref="EzHookContext.Reject"/>) rolls it back.
/// </remarks>
public abstract class EzTableHandler<T> where T : class, new()
{
    /// <summary>Rewrite the (already policy-filtered) query, e.g. add a soft-delete filter. Also applies to $count, $expand and writes' existence checks.</summary>
    public virtual Task<QueryRequest> BeforeReadAsync(QueryRequest query, EzHookContext ctx) => Task.FromResult(query);

    /// <summary>Adjust result rows (mask, format). Only change values of columns already present.</summary>
    public virtual Task AfterReadAsync(IReadOnlyList<Row> rows, EzHookContext ctx) => Task.CompletedTask;

    public virtual Task BeforeInsertAsync(T entity, EzHookContext ctx) => Task.CompletedTask;

    /// <summary>Performs the insert; returns the new key. Default: SimpleCRUD <c>InsertAsync</c>.</summary>
    public virtual Task<object?> InsertAsync(T entity, EzHookContext ctx) => ctx.Engine.InsertForKeyAsync(
        ctx.Session!.Connection, entity, ctx.Engine.Describe<T>().Keys[0].Property.PropertyType, ctx.Session.Transaction);

    public virtual Task AfterInsertAsync(T entity, EzHookContext ctx) => Task.CompletedTask;

    /// <param name="entity">The row with the request's changes applied.</param>
    /// <param name="original">The row as it was before the request.</param>
    public virtual Task BeforeUpdateAsync(T entity, T original, EzHookContext ctx) => Task.CompletedTask;

    /// <summary>Performs the update; returns rows affected. Default: SimpleCRUD <c>UpdateAsync</c>.</summary>
    public virtual Task<int> UpdateAsync(T entity, EzHookContext ctx) => ctx.Session!.UpdateAsync(entity);

    public virtual Task AfterUpdateAsync(T entity, EzHookContext ctx) => Task.CompletedTask;

    public virtual Task BeforeDeleteAsync(T entity, EzHookContext ctx) => Task.CompletedTask;

    /// <summary>Performs the delete; returns rows affected. Default: SimpleCRUD <c>DeleteAsync</c>. Override for soft delete.</summary>
    public virtual Task<int> DeleteAsync(T entity, EzHookContext ctx) => ctx.Session!.DeleteAsync(entity);

    public virtual Task AfterDeleteAsync(T entity, EzHookContext ctx) => Task.CompletedTask;
}

/// <summary>Inline hooks: <c>.Table&lt;Customer&gt;(t => t.BeforeInsert((c, ctx) => ...))</c>.</summary>
public sealed class EzTableHooks<T> where T : class, new()
{
    private readonly List<Func<QueryRequest, EzHookContext, QueryRequest>> _beforeRead = [];
    private readonly List<Action<Row, EzHookContext>> _afterRead = [];
    private readonly List<Func<T, EzHookContext, Task>> _beforeInsert = [];
    private readonly List<Func<T, EzHookContext, Task>> _afterInsert = [];
    private readonly List<Func<T, T, EzHookContext, Task>> _beforeUpdate = [];
    private readonly List<Func<T, EzHookContext, Task>> _afterUpdate = [];
    private readonly List<Func<T, EzHookContext, Task>> _beforeDelete = [];
    private readonly List<Func<T, EzHookContext, Task>> _afterDelete = [];
    private Func<T, EzHookContext, Task<object?>>? _insteadOfInsert;
    private Func<T, EzHookContext, Task<int>>? _insteadOfUpdate;
    private Func<T, EzHookContext, Task<int>>? _insteadOfDelete;

    public EzTableHooks<T> BeforeRead(Func<QueryRequest, EzHookContext, QueryRequest> hook) { _beforeRead.Add(hook); return this; }
    public EzTableHooks<T> AfterRead(Action<Row, EzHookContext> hook) { _afterRead.Add(hook); return this; }

    public EzTableHooks<T> BeforeInsert(Action<T, EzHookContext> hook) => BeforeInsert(Sync(hook));
    public EzTableHooks<T> BeforeInsert(Func<T, EzHookContext, Task> hook) { _beforeInsert.Add(hook); return this; }
    public EzTableHooks<T> AfterInsert(Action<T, EzHookContext> hook) => AfterInsert(Sync(hook));
    public EzTableHooks<T> AfterInsert(Func<T, EzHookContext, Task> hook) { _afterInsert.Add(hook); return this; }

    public EzTableHooks<T> BeforeUpdate(Action<T, T, EzHookContext> hook) =>
        BeforeUpdate((e, o, c) => { hook(e, o, c); return Task.CompletedTask; });
    public EzTableHooks<T> BeforeUpdate(Func<T, T, EzHookContext, Task> hook) { _beforeUpdate.Add(hook); return this; }
    public EzTableHooks<T> AfterUpdate(Action<T, EzHookContext> hook) => AfterUpdate(Sync(hook));
    public EzTableHooks<T> AfterUpdate(Func<T, EzHookContext, Task> hook) { _afterUpdate.Add(hook); return this; }

    public EzTableHooks<T> BeforeDelete(Action<T, EzHookContext> hook) => BeforeDelete(Sync(hook));
    public EzTableHooks<T> BeforeDelete(Func<T, EzHookContext, Task> hook) { _beforeDelete.Add(hook); return this; }
    public EzTableHooks<T> AfterDelete(Action<T, EzHookContext> hook) => AfterDelete(Sync(hook));
    public EzTableHooks<T> AfterDelete(Func<T, EzHookContext, Task> hook) { _afterDelete.Add(hook); return this; }

    /// <summary>Replace the insert itself; return the new key.</summary>
    public EzTableHooks<T> InsteadOfInsert(Func<T, EzHookContext, Task<object?>> operation) { _insteadOfInsert = operation; return this; }
    /// <summary>Replace the update itself; return rows affected.</summary>
    public EzTableHooks<T> InsteadOfUpdate(Func<T, EzHookContext, Task<int>> operation) { _insteadOfUpdate = operation; return this; }
    /// <summary>Replace the delete itself (e.g. soft delete); return rows affected.</summary>
    public EzTableHooks<T> InsteadOfDelete(Func<T, EzHookContext, Task<int>> operation) { _insteadOfDelete = operation; return this; }

    internal EzTableHandler<T> Build() => new Handler(this);

    private static Func<T, EzHookContext, Task> Sync(Action<T, EzHookContext> hook) =>
        (e, c) => { hook(e, c); return Task.CompletedTask; };

    private sealed class Handler(EzTableHooks<T> h) : EzTableHandler<T>
    {
        public override Task<QueryRequest> BeforeReadAsync(QueryRequest query, EzHookContext ctx) =>
            Task.FromResult(h._beforeRead.Aggregate(query, (q, hook) => hook(q, ctx)));

        public override Task AfterReadAsync(IReadOnlyList<Row> rows, EzHookContext ctx)
        {
            foreach (var row in rows)
            foreach (var hook in h._afterRead)
                hook(row, ctx);
            return Task.CompletedTask;
        }

        public override Task BeforeInsertAsync(T entity, EzHookContext ctx) => All(h._beforeInsert, entity, ctx);
        public override Task<object?> InsertAsync(T entity, EzHookContext ctx) =>
            h._insteadOfInsert?.Invoke(entity, ctx) ?? base.InsertAsync(entity, ctx);
        public override Task AfterInsertAsync(T entity, EzHookContext ctx) => All(h._afterInsert, entity, ctx);

        public override async Task BeforeUpdateAsync(T entity, T original, EzHookContext ctx)
        {
            foreach (var hook in h._beforeUpdate) await hook(entity, original, ctx);
        }

        public override Task<int> UpdateAsync(T entity, EzHookContext ctx) =>
            h._insteadOfUpdate?.Invoke(entity, ctx) ?? base.UpdateAsync(entity, ctx);
        public override Task AfterUpdateAsync(T entity, EzHookContext ctx) => All(h._afterUpdate, entity, ctx);

        public override Task BeforeDeleteAsync(T entity, EzHookContext ctx) => All(h._beforeDelete, entity, ctx);
        public override Task<int> DeleteAsync(T entity, EzHookContext ctx) =>
            h._insteadOfDelete?.Invoke(entity, ctx) ?? base.DeleteAsync(entity, ctx);
        public override Task AfterDeleteAsync(T entity, EzHookContext ctx) => All(h._afterDelete, entity, ctx);

        private static async Task All(List<Func<T, EzHookContext, Task>> hooks, T entity, EzHookContext ctx)
        {
            foreach (var hook in hooks) await hook(entity, ctx);
        }
    }
}
