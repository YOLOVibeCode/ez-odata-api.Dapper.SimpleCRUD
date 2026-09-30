using EzOdata.Entities.AspNetCore;

namespace EzOdata.SimpleCrud.AspNetCore;

/// <summary>SimpleCRUD write-engine registration and hook escape hatches.</summary>
public static class SimpleCrudExtensions
{
    /// <summary>Use Dapper.SimpleCRUD as this service's write engine (isolated per dialect).</summary>
    public static EzServiceExtensionBuilder UseSimpleCrud(this EzServiceExtensionBuilder builder)
    {
        return builder.UseEngine(new SimpleCrudEntityEngine(() =>
            builder.Extension.Items.TryGetValue("simplecrud.naming", out var n) ? n as SimpleCrudNaming : null));
    }

    /// <summary>Per-service SimpleCRUD naming conventions (installed into this service's isolated engine).</summary>
    public static EzServiceExtensionBuilder UseNaming(this EzServiceExtensionBuilder builder, SimpleCrudNaming naming)
    {
        builder.Extension.Items["simplecrud.naming"] = naming;
        return builder;
    }

    /// <summary>The SimpleCRUD session for this write (null on reads). Same connection and transaction as the API write.</summary>
    public static SimpleCrudSession? Session(this EzHookContext ctx) =>
        ctx.WriteStore is SimpleCrudEntityStore store ? store.Session : null;

    /// <summary>SimpleCRUD operations: the write session when present, otherwise throw (use <see cref="EzHookContext.Data"/>).</summary>
    public static ISimpleCrudOperations SimpleCrud(this EzHookContext ctx) =>
        ctx.WriteStore is SimpleCrudEntityStore store
            ? store.Session
            : throw new InvalidOperationException("SimpleCRUD is only available in write hooks. Use ctx.Data, or call UseSimpleCrud().");

    /// <summary>The isolated SimpleCRUD engine for this service's dialect (write hooks only).</summary>
    public static SimpleCrudEngine SimpleCrudEngine(this EzHookContext ctx) =>
        ctx.WriteStore is SimpleCrudEntityStore store
            ? store.Session.Engine
            : throw new InvalidOperationException("SimpleCRUD is only available in write hooks.");
}
