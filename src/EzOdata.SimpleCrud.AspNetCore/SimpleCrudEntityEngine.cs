using System.Data;
using System.Data.Common;
using Dapper;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Schema;
using EzOdata.Core.Services;
using EzOdata.Entities.AspNetCore;

namespace EzOdata.SimpleCrud.AspNetCore;

/// <summary>Write engine that maps entities and runs writes through Dapper.SimpleCRUD.</summary>
public sealed class SimpleCrudEntityEngine : IEntityEngine
{
    private readonly Func<SimpleCrudNaming?> _naming;

    /// <summary>Creates an engine that uses <see cref="SimpleCrudEngines.For"/> per dialect.</summary>
    public SimpleCrudEntityEngine(SimpleCrudNaming? naming = null) : this(() => naming) { }

    /// <summary>Creates an engine whose naming is read when a service is bound (so <c>UseNaming</c> can come after <c>UseSimpleCrud</c>).</summary>
    public SimpleCrudEntityEngine(Func<SimpleCrudNaming?> naming) => _naming = naming;

    /// <inheritdoc />
    public string Name => "SimpleCRUD";

    /// <inheritdoc />
    public EntityMap Map(Type entityType, ServiceRuntime runtime)
    {
        var info = EngineFor(runtime).Describe(entityType);
        return new EntityMap(
            info.EntityType,
            info.TableName,
            info.Schema,
            info.Properties.Select(p => new EntityPropertyMap(p.Property, p.ColumnName, p.IsKey, p.IsInsertable, p.IsUpdatable, p.IsSelectable)).ToList(),
            info.QuotedTableName);
    }

    /// <inheritdoc />
    public void Validate(EntityMap map, TableModel table, IReadOnlyDictionary<string, EntityPropertyMap> byColumn, List<string> errors)
    {
        if (map.Keys.Count > 1 && map.Keys.FirstOrDefault(k => !k.IsInsertable) is { } missing)
        {
            errors.Add($"{map.EntityType.Name}: composite key part {missing.Property.Name} must be [Required] so SimpleCRUD inserts it.");
        }
    }

    /// <inheritdoc />
    public Task<IEntityStore> OpenStoreAsync(EntityStoreRequest request, IsolationLevel isolation, CancellationToken ct)
    {
        var engine = EngineFor(request.Runtime);
        var session = new SimpleCrudSession(engine, request.Connection, null, request.CommandTimeoutSeconds);
        session.BeginTransaction(isolation);
        return Task.FromResult<IEntityStore>(new SimpleCrudEntityStore(session, request.Maps, ownsSession: true));
    }

    /// <summary>A connection-per-call SimpleCRUD client wrapped as <see cref="IEntityStore"/> (for read-side hooks).</summary>
    internal ISimpleCrud Client(ServiceRuntime runtime, Func<DbConnection> create) =>
        _naming() is { } naming
            ? SimpleCrud.For(DialectFor(runtime.ConnectorType)).WithNaming(naming).WithConnection(create).Build()
            : SimpleCrud.For(DialectFor(runtime.ConnectorType)).WithConnection(create).Build();

    internal SimpleCrudEngine EngineFor(ServiceRuntime runtime) => SimpleCrudEngines.For(DialectFor(runtime.ConnectorType), _naming());

    internal static SimpleCRUD.Dialect DialectFor(string connectorType) => connectorType switch
    {
        ConnectorTypes.Sqlite => SimpleCRUD.Dialect.SQLite,
        ConnectorTypes.PostgreSql => SimpleCRUD.Dialect.PostgreSQL,
        ConnectorTypes.MySql => SimpleCRUD.Dialect.MySQL,
        ConnectorTypes.SqlServer => SimpleCRUD.Dialect.SQLServer,
        _ => throw new NotSupportedException($"No SimpleCRUD dialect for connector '{connectorType}'."),
    };
}

internal sealed class SimpleCrudEntityStore : IEntityStore
{
    private readonly SimpleCrudSession _session;
    private readonly IReadOnlyDictionary<Type, EntityMap> _maps;
    private readonly bool _ownsSession;

    public SimpleCrudEntityStore(SimpleCrudSession session, IReadOnlyDictionary<Type, EntityMap> maps, bool ownsSession)
    {
        _session = session;
        _maps = maps;
        _ownsSession = ownsSession;
    }

    public SimpleCrudSession Session => _session;
    public DbConnection Connection => (DbConnection)_session.Connection;
    public IDbTransaction? Transaction => _session.Transaction;

    public Task<T?> GetAsync<T>(object key) where T : class => _session.GetAsync<T>(key);

    public Task<object?> InsertAsync<T>(T entity) where T : class
    {
        var keyType = Map<T>().Keys[0].Property.PropertyType;
        return _session.Engine.InsertForKeyAsync(_session.Connection, entity, keyType, _session.Transaction);
    }

    public Task<int> UpdateAsync<T>(T entity, T? original = null) where T : class => _session.UpdateAsync(entity);

    public Task<int> DeleteAsync<T>(T entity) where T : class => _session.DeleteAsync(entity);

    public void Commit() => _session.Commit();
    public void Rollback() => _session.Rollback();

    public ValueTask DisposeAsync()
    {
        if (_ownsSession) _session.Dispose();
        return ValueTask.CompletedTask;
    }

    private EntityMap Map<T>()
    {
        if (_maps.TryGetValue(typeof(T), out var map)) return map;
        var info = _session.Engine.Describe(typeof(T));
        return new EntityMap(
            info.EntityType,
            info.TableName,
            info.Schema,
            info.Properties.Select(p => new EntityPropertyMap(p.Property, p.ColumnName, p.IsKey, p.IsInsertable, p.IsUpdatable, p.IsSelectable)).ToList(),
            info.QuotedTableName);
    }
}
