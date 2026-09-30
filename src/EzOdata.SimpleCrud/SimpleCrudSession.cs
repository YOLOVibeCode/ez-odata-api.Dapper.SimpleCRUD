using System.Data;
using System.Data.Common;

namespace EzOdata.SimpleCrud;

/// <summary>SimpleCRUD's operations bound to a connection source and engine — no statics, no connection plumbing.</summary>
public interface ISimpleCrudOperations
{
    /// <summary>The SimpleCRUD engine (dialect + naming) these operations run on.</summary>
    SimpleCrudEngine Engine { get; }

    /// <summary>SimpleCRUD <c>GetAsync</c>: the entity with this key, or <c>null</c>.</summary>
    Task<T?> GetAsync<T>(object id);
    /// <summary>SimpleCRUD <c>GetListAsync</c>: every row of the entity's table.</summary>
    Task<IEnumerable<T>> GetListAsync<T>();
    /// <summary>SimpleCRUD <c>GetListAsync</c>: rows matching an anonymous object of exact-match conditions, e.g. <c>new { Country = "US" }</c>.</summary>
    Task<IEnumerable<T>> GetListAsync<T>(object whereConditions);
    /// <summary>SimpleCRUD <c>GetListAsync</c>: rows matching a raw <c>where</c> clause. Always pass values through <paramref name="parameters"/>.</summary>
    Task<IEnumerable<T>> GetListAsync<T>(string conditions, object? parameters = null);
    /// <summary>SimpleCRUD <c>GetListPagedAsync</c>: one page (1-based) of rows, ordered by <paramref name="orderby"/>.</summary>
    Task<IEnumerable<T>> GetListPagedAsync<T>(int pageNumber, int rowsPerPage, string conditions, string orderby, object? parameters = null);
    /// <summary>SimpleCRUD <c>InsertAsync</c>: inserts the entity and returns its generated integer key.</summary>
    Task<int?> InsertAsync<TEntity>(TEntity entity);
    /// <summary>SimpleCRUD <c>InsertAsync&lt;TKey, TEntity&gt;</c>: inserts the entity and returns its key (generated or supplied).</summary>
    Task<TKey> InsertAsync<TKey, TEntity>(TEntity entity);
    /// <summary>SimpleCRUD <c>UpdateAsync</c>: updates every updatable column by key; returns rows affected.</summary>
    Task<int> UpdateAsync<TEntity>(TEntity entity);
    /// <summary>SimpleCRUD <c>DeleteAsync</c>: deletes the entity by its key; returns rows affected.</summary>
    Task<int> DeleteAsync<T>(T entity);
    /// <summary>SimpleCRUD <c>DeleteAsync</c>: deletes the row with this key; returns rows affected.</summary>
    Task<int> DeleteAsync<T>(object id);
    /// <summary>SimpleCRUD <c>DeleteListAsync</c>: deletes rows matching exact-match conditions; returns rows affected.</summary>
    Task<int> DeleteListAsync<T>(object whereConditions);
    /// <summary>SimpleCRUD <c>DeleteListAsync</c>: deletes rows matching a raw <c>where</c> clause; returns rows affected.</summary>
    Task<int> DeleteListAsync<T>(string conditions, object? parameters = null);
    /// <summary>SimpleCRUD <c>RecordCountAsync</c>: counts rows, optionally filtered by a raw <c>where</c> clause.</summary>
    Task<int> RecordCountAsync<T>(string conditions = "", object? parameters = null);
    /// <summary>SimpleCRUD <c>RecordCountAsync</c>: counts rows matching exact-match conditions.</summary>
    Task<int> RecordCountAsync<T>(object whereConditions);
}

/// <summary>
/// A unit of work: one open connection (and optional transaction) with SimpleCRUD's operations on it.
/// Create from <see cref="ISimpleCrud.OpenSessionAsync"/>, or wrap a connection you already have.
/// </summary>
public sealed class SimpleCrudSession : ISimpleCrudOperations, IDisposable
#if NET
    , IAsyncDisposable
#endif
{
    private readonly bool _ownsConnection;
    private readonly int? _commandTimeout;

    /// <summary>Wraps an existing connection/transaction; the caller keeps ownership of both.</summary>
    public SimpleCrudSession(SimpleCrudEngine engine, IDbConnection connection, IDbTransaction? transaction = null, int? commandTimeout = null)
        : this(engine, connection, transaction, commandTimeout, ownsConnection: false) { }

    internal SimpleCrudSession(SimpleCrudEngine engine, IDbConnection connection, IDbTransaction? transaction, int? commandTimeout, bool ownsConnection)
    {
        Engine = engine;
        Connection = connection;
        Transaction = transaction;
        _commandTimeout = commandTimeout;
        _ownsConnection = ownsConnection;
    }

    /// <inheritdoc />
    public SimpleCrudEngine Engine { get; }
    /// <summary>The session's open connection.</summary>
    public IDbConnection Connection { get; }
    /// <summary>The active transaction, or <c>null</c>. Every operation on this session enlists in it.</summary>
    public IDbTransaction? Transaction { get; private set; }

    /// <summary>Starts a transaction that subsequent operations on this session enlist in.</summary>
    public SimpleCrudSession BeginTransaction(IsolationLevel isolationLevel = IsolationLevel.Unspecified)
    {
        if (Transaction is not null) throw new InvalidOperationException("A transaction is already active on this session.");
        Transaction = isolationLevel == IsolationLevel.Unspecified ? Connection.BeginTransaction() : Connection.BeginTransaction(isolationLevel);
        return this;
    }

    /// <summary>Commits the active transaction.</summary>
    public void Commit()
    {
        (Transaction ?? throw new InvalidOperationException("No active transaction.")).Commit();
        Transaction.Dispose();
        Transaction = null;
    }

    /// <summary>Rolls back the active transaction, if any.</summary>
    public void Rollback()
    {
        Transaction?.Rollback();
        Transaction?.Dispose();
        Transaction = null;
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(object id) => Engine.GetAsync<T>(Connection, id, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<IEnumerable<T>> GetListAsync<T>() => Engine.GetListAsync<T>(Connection, "", null, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<IEnumerable<T>> GetListAsync<T>(object whereConditions) => Engine.GetListAsync<T>(Connection, whereConditions, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<IEnumerable<T>> GetListAsync<T>(string conditions, object? parameters = null) => Engine.GetListAsync<T>(Connection, conditions, parameters, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<IEnumerable<T>> GetListPagedAsync<T>(int pageNumber, int rowsPerPage, string conditions, string orderby, object? parameters = null) =>
        Engine.GetListPagedAsync<T>(Connection, pageNumber, rowsPerPage, conditions, orderby, parameters, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<int?> InsertAsync<TEntity>(TEntity entity) => Engine.InsertAsync(Connection, entity, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<TKey> InsertAsync<TKey, TEntity>(TEntity entity) => Engine.InsertAsync<TKey, TEntity>(Connection, entity, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<int> UpdateAsync<TEntity>(TEntity entity) => Engine.UpdateAsync(Connection, entity, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<int> DeleteAsync<T>(T entity) => Engine.DeleteAsync(Connection, entity, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<int> DeleteAsync<T>(object id) => Engine.DeleteAsync<T>(Connection, id, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<int> DeleteListAsync<T>(object whereConditions) => Engine.DeleteListAsync<T>(Connection, whereConditions, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<int> DeleteListAsync<T>(string conditions, object? parameters = null) => Engine.DeleteListAsync<T>(Connection, conditions, parameters, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<int> RecordCountAsync<T>(string conditions = "", object? parameters = null) => Engine.RecordCountAsync<T>(Connection, conditions, parameters, Transaction, _commandTimeout);
    /// <inheritdoc />
    public Task<int> RecordCountAsync<T>(object whereConditions) => Engine.RecordCountAsync<T>(Connection, whereConditions, Transaction, _commandTimeout);

    /// <summary>Disposes the transaction (rolling it back if uncommitted) and, if the session opened it, the connection.</summary>
    public void Dispose()
    {
        Transaction?.Dispose();
        Transaction = null;
        if (_ownsConnection) Connection.Dispose();
    }

#if NET
    /// <inheritdoc cref="Dispose"/>
    public async ValueTask DisposeAsync()
    {
        if (Transaction is DbTransaction dbTransaction) await dbTransaction.DisposeAsync().ConfigureAwait(false);
        else Transaction?.Dispose();
        Transaction = null;
        if (!_ownsConnection) return;
        if (Connection is DbConnection dbConnection) await dbConnection.DisposeAsync().ConfigureAwait(false);
        else Connection.Dispose();
    }
#endif

    internal static async Task OpenAsync(IDbConnection connection)
    {
        if (connection.State == ConnectionState.Open) return;
        if (connection is DbConnection db) await db.OpenAsync().ConfigureAwait(false);
        else connection.Open();
    }
}
