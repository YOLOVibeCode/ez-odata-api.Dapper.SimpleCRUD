using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using Dapper;
using EzOdata.Core.Query;
using EzOdata.SimpleCrud;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EzOdata.SimpleCrud.AspNetCore;

/// <summary>All configured extensions, by service name.</summary>
internal sealed class EzExtensionSet
{
    public EzExtensionSet(Dictionary<string, ServiceExtension> services) => Services = services;

    public Dictionary<string, ServiceExtension> Services { get; }

    public const string Marker = "+simplecrud:";

    public static string ConnectorTypeFor(string original, string service) => original + Marker + service;
}

/// <summary>
/// Decorates ez-odata's runtime resolver: an extended service gets the overlaid schema and a
/// per-service connector type, which <see cref="ExtendedConnectorRegistry"/> resolves to the wrapped connector.
/// </summary>
internal sealed class ExtendedRuntimeResolver(IServiceRuntimeResolver inner, EzExtensionSet set) : IServiceRuntimeResolver
{
    public async Task<ServiceRuntime?> ResolveAsync(string serviceName, CancellationToken ct)
    {
        var runtime = await inner.ResolveAsync(serviceName, ct);
        if (runtime is null || !set.Services.TryGetValue(serviceName, out var extension)) return runtime;

        var model = extension.GetModel(runtime);
        return runtime with
        {
            ConnectorType = EzExtensionSet.ConnectorTypeFor(runtime.ConnectorType, extension.Name),
            Schema = model.Schema,
            SchemaVersion = runtime.SchemaVersion + "+simplecrud",
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
                stock.Writer is null ? null : new SimpleCrudWriteExecutor(stock.Writer, runtime),
                stock.Dialect);
        })!;

        return descriptor is not null;
    }
}

/// <summary>Per extended connector: builds hook contexts and resolves handlers.</summary>
internal sealed class ExtensionRuntime(ServiceExtension extension, ISqlDialect dialect, IServiceProvider root)
{
    private readonly IHttpContextAccessor? _http = root.GetService<IHttpContextAccessor>();

    public ServiceExtension Extension => extension;
    public ISqlDialect Dialect => dialect;

    public (ITableOperation Operation, EzHookContext Context) Begin(
        ServiceModel model, BoundTable table, EzOperation operation, SimpleCrudSession? session, CancellationToken ct)
    {
        var http = _http?.HttpContext;
        var services = http?.RequestServices ?? root;
        var user = http?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var context = new EzHookContext(extension.Name, table.Binding, operation, user, services, model.Engine, session, () => model.Client, ct);
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
/// Writes to entity-mapped tables go through SimpleCRUD (on this service's isolated engine) and the
/// table's handler, in one transaction; other tables use the stock ez-odata writer untouched.
/// </summary>
internal sealed class SimpleCrudWriteExecutor(IWriteExecutor inner, ExtensionRuntime runtime) : IWriteExecutor
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
            throw new NotSupportedQueryException("A $batch changeset cannot yet mix SimpleCRUD-extended and plain tables.");
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
            catch (DbException ex) when (ProviderConnections.IsTransientConflict(ex) && attempt < MaxAttempts)
            {
                // Deadlock / serialization failure: the transaction rolled back; re-run the whole unit.
                await Task.Delay(Random.Shared.Next(10, 40) * attempt, ct);
            }
            catch (DbException ex)
            {
                throw ProviderConnections.Map(ex);
            }
        }
    }

    private const int MaxAttempts = 3;

    private async Task<IReadOnlyList<WriteResult>> AttemptAsync(
        ServiceModel model, List<BoundTable?> tables, IReadOnlyList<WriteExecution> executions, CancellationToken ct)
    {
        await using var connection = model.CreateConnection();
        await connection.OpenAsync(ct);
        using var session = new SimpleCrudSession(model.Engine, connection, null, executions[0].Options.CommandTimeoutSeconds);

        var steps = new List<(WriteExecution Execution, ITableOperation Operation, EzHookContext Context, FilterNode? ReadFilter)>();
        for (var i = 0; i < executions.Count; i++)
        {
            var (operation, context) = runtime.Begin(model, tables[i]!, Kind(executions[i].Write.Kind), session, ct);
            FilterNode? readFilter = null;
            if (executions[i].Write.Kind != WriteKind.Insert)
            {
                readFilter = (await operation.BeforeReadAsync(
                    new QueryRequest { ServiceName = executions[i].Write.ServiceName, Table = executions[i].Write.Table }, context)).Filter;
            }

            steps.Add((executions[i], operation, context, readFilter));
        }

        // A write gated on "can this caller see the row" must not be separable from its check by a
        // concurrent change. PostgreSQL: REPEATABLE READ (a concurrent change to the checked row makes our
        // write fail with 40001; SERIALIZABLE would also abort on unrelated rows). Others: SERIALIZABLE
        // (key-range / shared row locks; SQLite locks the database).
        var guarded = steps.Any(s => s.ReadFilter is not null || s.Execution.Write.Precondition is not null
                                     || s.Execution.Write.InsertVisibilityFilter is not null);
        session.BeginTransaction(!guarded ? IsolationLevel.Unspecified
            : model.Dialect == SimpleCRUD.Dialect.PostgreSQL ? IsolationLevel.RepeatableRead
            : IsolationLevel.Serializable);

        var kit = new WriteToolkit(runtime.Dialect, connection, session, ct);
        var results = new List<WriteResult>(steps.Count);
        try
        {
            foreach (var step in steps)
            {
                results.Add(await step.Operation.WriteAsync(step.Execution, step.ReadFilter, kit, step.Context));
            }

            session.Commit();
            return results;
        }
        catch
        {
            session.Rollback();
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
