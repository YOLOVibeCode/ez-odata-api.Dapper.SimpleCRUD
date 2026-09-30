using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Claims;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using EzOdata.Core.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EzOdata.Entities.AspNetCore;

/// <summary>All configured extensions, by service name.</summary>
internal sealed class EzExtensionSet
{
    public EzExtensionSet(Dictionary<string, ServiceExtension> services) => Services = services;

    public Dictionary<string, ServiceExtension> Services { get; }

    public const string Marker = "+entities:";

    public static string ConnectorTypeFor(string original, string service) => original + Marker + service;
}

/// <summary>
/// Decorates ez-odata's runtime resolver: an extended service gets the overlaid schema and a
/// per-service connector type, which <see cref="ExtendedConnectorRegistry"/> resolves to the wrapped connector.
/// </summary>
internal sealed class ExtendedRuntimeResolver(IServiceRuntimeResolver inner, EzExtensionSet set, IServiceProvider services) : IServiceRuntimeResolver
{
    public async Task<ServiceRuntime?> ResolveAsync(string serviceName, CancellationToken ct)
    {
        _ = services;
        var runtime = await inner.ResolveAsync(serviceName, ct);
        if (runtime is null || !set.Services.TryGetValue(serviceName, out var extension)) return runtime;

        var model = extension.GetModel(runtime);
        return runtime with
        {
            ConnectorType = EzExtensionSet.ConnectorTypeFor(runtime.ConnectorType, extension.Name),
            Schema = model.Schema,
            SchemaVersion = runtime.SchemaVersion + "+entities",
        };
    }
}

/// <summary>Decorates ez-odata's connector registry with per-service wrapped connectors.</summary>
internal sealed class ExtendedConnectorRegistry(IConnectorRegistry inner, EzExtensionSet set, IServiceProvider services) : IConnectorRegistry
{
    private readonly ConcurrentDictionary<string, ConnectorDescriptor?> _wrapped = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> ConnectorTypes => inner.ConnectorTypes;

    public bool TryGet(string connectorType, out ConnectorDescriptor descriptor)
    {
        var marker = connectorType.IndexOf(EzExtensionSet.Marker, StringComparison.Ordinal);
        if (marker < 0) return inner.TryGet(connectorType, out descriptor);

        descriptor = _wrapped.GetOrAdd(connectorType, type =>
        {
            var original = type.Substring(0, marker);
            var service = type.Substring(marker + EzExtensionSet.Marker.Length);
            if (!inner.TryGet(original, out var stock) || !set.Services.TryGetValue(service, out var extension)) return null;

            var runtime = new ExtensionRuntime(extension, stock.Dialect, services);
            return new ConnectorDescriptor(
                type,
                stock.Tester,
                stock.Introspector,
                new ExtendedQueryExecutor(stock.Reader, runtime),
                stock.Writer is null ? null : new EntityWriteExecutor(stock.Writer, runtime),
                stock.Dialect);
        })!;

        return descriptor is not null;
    }
}

/// <summary>Per extended connector: builds hook contexts and resolves handlers.</summary>
internal sealed class ExtensionRuntime(ServiceExtension extension, ISqlDialect dialect, IServiceProvider root)
{
    private readonly IHttpContextAccessor? _http = root.GetService<IHttpContextAccessor>();

    public ILogger Logger { get; } =
        root.GetService<ILoggerFactory>()?.CreateLogger("EzOdata.Entities.AspNetCore") ?? NullLogger.Instance;

    public ServiceExtension Extension => extension;
    public ISqlDialect Dialect => dialect;
    public IServiceProvider Root => root;

    public (ITableOperation Operation, EzHookContext Context) Begin(
        ServiceModel model, BoundTable table, EzOperation operation, IEntityStore? store, CancellationToken ct)
    {
        var http = _http?.HttpContext;
        var services = http?.RequestServices ?? root;
        var user = http?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var context = new EzHookContext(extension.Name, table.Binding, operation, user, services, model.Engine, store,
            () => model.ReadStore(services), ct);
        return (table.Registration.Begin(services, table.Binding), context);
    }

    public static ConnectorException Translate(EzHookException ex) => new(ex.ErrorCode, ex.Message, inner: ex);
}

/// <summary>Reads stay on ez-odata's compiled SQL; handlers can rewrite the query and post-process rows.</summary>
internal sealed class ExtendedQueryExecutor(IQueryExecutor inner, ExtensionRuntime runtime) : IQueryExecutor
{
    public async Task<QueryResult> QueryAsync(QueryExecution execution, CancellationToken ct)
    {
        if (Bound(execution) is not ({ } model, { } table)) return await inner.QueryAsync(execution, ct);

        var (operation, context) = runtime.Begin(model, table, EzOperation.Read, null, ct);
        try
        {
            var query = await operation.BeforeReadAsync(execution.Query, context);
            var result = await inner.QueryAsync(execution with { Query = query }, ct);
            if (query.Apply is null) await operation.AfterReadAsync(result.Rows, context);
            return result;
        }
        catch (EzHookException ex)
        {
            throw ExtensionRuntime.Translate(ex);
        }
    }

    public async Task<long> CountAsync(QueryExecution execution, CancellationToken ct)
    {
        if (Bound(execution) is not ({ } model, { } table)) return await inner.CountAsync(execution, ct);

        var (operation, context) = runtime.Begin(model, table, EzOperation.Read, null, ct);
        try
        {
            var query = await operation.BeforeReadAsync(execution.Query, context);
            return await inner.CountAsync(execution with { Query = query }, ct);
        }
        catch (EzHookException ex)
        {
            throw ExtensionRuntime.Translate(ex);
        }
    }

    private (ServiceModel?, BoundTable?) Bound(QueryExecution execution)
    {
        var model = runtime.Extension.FindModel(execution.Schema);
        return (model, model?.Find(execution.Query.Table));
    }
}

/// <summary>
/// Writes to entity-mapped tables go through the configured engine and the table's handler, in one
/// transaction; other tables use the stock ez-odata writer untouched.
/// </summary>
internal sealed class EntityWriteExecutor(IWriteExecutor inner, ExtensionRuntime runtime) : IWriteExecutor
{
    public async Task<WriteResult> WriteAsync(WriteExecution execution, CancellationToken ct) =>
        (await WriteAtomicAsync([execution], ct))[0];

    public async Task<IReadOnlyList<WriteResult>> WriteAtomicAsync(IReadOnlyList<WriteExecution> executions, CancellationToken ct)
    {
        if (executions.Count == 0) return [];

        var model = runtime.Extension.FindModel(executions[0].Schema);
        var tables = executions.Select(e => model?.Find(e.Write.Table)).ToList();
        if (model is null || tables.All(t => t is null)) return await inner.WriteAtomicAsync(executions, ct);
        if (tables.Any(t => t is null))
        {
            throw new NotSupportedQueryException("A $batch changeset cannot yet mix entity-mapped and plain tables.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptAsync(model, tables!, executions, ct);
            }
            catch (EzHookException ex)
            {
                throw ExtensionRuntime.Translate(ex);
            }
            catch (Exception ex) when (FindDb(ex) is { } db && ProviderConnections.IsTransientConflict(db) && attempt < MaxAttempts)
            {
                await Task.Delay(Random.Shared.Next(10, 40) * attempt, ct);
            }
            catch (Exception ex) when (FindDb(ex) is { } db)
            {
                throw ProviderConnections.Map(db);
            }
        }
    }

    private const int MaxAttempts = 3;

    private static DbException? FindDb(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException db) return db;
        }

        return null;
    }

    private async Task<IReadOnlyList<WriteResult>> AttemptAsync(
        ServiceModel model, List<BoundTable?> tables, IReadOnlyList<WriteExecution> executions, CancellationToken ct)
    {
        await using var connection = model.CreateConnection();
        await connection.OpenAsync(ct);

        var steps = new List<(WriteExecution Execution, ITableOperation Operation, EzHookContext Context, FilterNode? ReadFilter)>();
        // Open a dummy store after we know isolation — first collect BeforeRead without a store (reads don't need it).
        var pending = new List<(WriteExecution Execution, BoundTable Table, EzOperation Kind)>();
        for (var i = 0; i < executions.Count; i++)
        {
            pending.Add((executions[i], tables[i]!, Kind(executions[i].Write.Kind)));
        }

        var readFilters = new FilterNode?[pending.Count];
        for (var i = 0; i < pending.Count; i++)
        {
            if (pending[i].Execution.Write.Kind == WriteKind.Insert) continue;
            var (op, ctx) = runtime.Begin(model, pending[i].Table, EzOperation.Read, null, ct);
            readFilters[i] = (await op.BeforeReadAsync(
                new QueryRequest { ServiceName = pending[i].Execution.Write.ServiceName, Table = pending[i].Execution.Write.Table }, ctx)).Filter;
        }

        var guarded = pending.Zip(readFilters, (p, f) => (p, f)).Any(x =>
            x.f is not null || x.p.Execution.Write.Precondition is not null
            || x.p.Execution.Write.InsertVisibilityFilter is not null);

        var isolation = ProviderConnections.IsolationFor(model.Runtime.ConnectorType, guarded);
        var request = new EntityStoreRequest(model.Runtime, connection, runtime.Root, model.Maps,
            executions[0].Options.CommandTimeoutSeconds);
        await using var store = await model.Engine.OpenStoreAsync(request, isolation, ct);

        for (var i = 0; i < pending.Count; i++)
        {
            var (operation, context) = runtime.Begin(model, pending[i].Table, pending[i].Kind, store, ct);
            steps.Add((pending[i].Execution, operation, context, readFilters[i]));
        }

        var kit = new WriteToolkit(runtime.Dialect, connection, store.Transaction, ct);
        var results = new List<WriteResult>(steps.Count);
        try
        {
            foreach (var step in steps)
            {
                results.Add(await step.Operation.WriteAsync(step.Execution, step.ReadFilter, kit, step.Context));
            }

            store.Commit();
            foreach (var step in steps) await step.Context.RunCommittedCallbacksAsync(runtime.Logger);
            return results;
        }
        catch
        {
            store.Rollback();
            throw;
        }
    }

    private static EzOperation Kind(WriteKind kind) => kind switch
    {
        WriteKind.Insert => EzOperation.Insert,
        WriteKind.Delete => EzOperation.Delete,
        _ => EzOperation.Update,
    };
}
