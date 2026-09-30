using System.Data;
using System.Data.Common;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Schema;

namespace EzOdata.Entities.AspNetCore;

/// <summary>A write engine (SimpleCRUD, EF Core, …) that maps entities and runs inserts, updates and deletes.</summary>
public interface IEntityEngine
{
    /// <summary>Diagnostic name, e.g. <c>SimpleCRUD[SQLite]</c> or <c>EF Core (CrmDbContext)</c>.</summary>
    string Name { get; }

    /// <summary>Non-fatal mapping notes (for example, EF value converters). Logged at startup.</summary>
    IReadOnlyList<string> Warnings => [];

    /// <summary>Maps <paramref name="entityType"/> the way this engine would persist it.</summary>
    EntityMap Map(Type entityType, ServiceRuntime runtime);

    /// <summary>Engine-specific extra checks after the table has been matched. Default: none.</summary>
    void Validate(EntityMap map, TableModel table, IReadOnlyDictionary<string, EntityPropertyMap> byColumn, List<string> errors)
    {
    }

    /// <summary>Opens a store on an already-open connection (and starts a transaction at <paramref name="isolation"/>).</summary>
    Task<IEntityStore> OpenStoreAsync(EntityStoreRequest request, IsolationLevel isolation, CancellationToken ct);
}

/// <summary>Inputs for <see cref="IEntityEngine.OpenStoreAsync"/>.</summary>
public sealed class EntityStoreRequest
{
    /// <summary>Creates the request.</summary>
    public EntityStoreRequest(ServiceRuntime runtime, DbConnection connection, IServiceProvider services,
        IReadOnlyDictionary<Type, EntityMap> maps, int commandTimeoutSeconds = 30)
    {
        Runtime = runtime;
        Connection = connection;
        Services = services;
        Maps = maps;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>The ez-odata service runtime (connector type and connection spec).</summary>
    public ServiceRuntime Runtime { get; }

    /// <summary>Open connection owned by the write executor.</summary>
    public DbConnection Connection { get; }

    /// <summary>Request or root services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Maps for every bound entity type.</summary>
    public IReadOnlyDictionary<Type, EntityMap> Maps { get; }

    /// <summary>Command timeout in seconds.</summary>
    public int CommandTimeoutSeconds { get; }
}

/// <summary>A unit of work on the API write's connection and transaction.</summary>
public interface IEntityStore : IAsyncDisposable
{
    /// <summary>The open connection.</summary>
    DbConnection Connection { get; }

    /// <summary>The active transaction.</summary>
    IDbTransaction? Transaction { get; }

    /// <summary>Loads the entity with this key (a scalar for a single key, or an instance carrying key properties).</summary>
    Task<T?> GetAsync<T>(object key) where T : class;

    /// <summary>Inserts <paramref name="entity"/> and returns the generated single key, if any.</summary>
    Task<object?> InsertAsync<T>(T entity) where T : class;

    /// <summary>Updates <paramref name="entity"/>. When <paramref name="original"/> is set, only changed properties are written.</summary>
    Task<int> UpdateAsync<T>(T entity, T? original = null) where T : class;

    /// <summary>Deletes <paramref name="entity"/> by key.</summary>
    Task<int> DeleteAsync<T>(T entity) where T : class;

    /// <summary>Commits the transaction.</summary>
    void Commit();

    /// <summary>Rolls back the transaction, if any.</summary>
    void Rollback();
}
