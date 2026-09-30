using System.Data;
using System.Data.Common;

namespace EzOdata.SimpleCrud;

/// <summary>SimpleCRUD's operations bound to a connection source and engine — no statics, no connection plumbing.</summary>
public interface ISimpleCrudOperations
{
    SimpleCrudEngine Engine { get; }

    Task<T?> GetAsync<T>(object id);
    Task<IEnumerable<T>> GetListAsync<T>();
    Task<IEnumerable<T>> GetListAsync<T>(object whereConditions);
    Task<IEnumerable<T>> GetListAsync<T>(string conditions, object? parameters = null);
    Task<IEnumerable<T>> GetListPagedAsync<T>(int pageNumber, int rowsPerPage, string conditions, string orderby, object? parameters = null);
    Task<int?> InsertAsync<TEntity>(TEntity entity);
    Task<TKey> InsertAsync<TKey, TEntity>(TEntity entity);
    Task<int> UpdateAsync<TEntity>(TEntity entity);
    Task<int> DeleteAsync<T>(T entity);
    Task<int> DeleteAsync<T>(object id);
    Task<int> DeleteListAsync<T>(object whereConditions);
    Task<int> DeleteListAsync<T>(string conditions, object? parameters = null);
    Task<int> RecordCountAsync<T>(string conditions = "", object? parameters = null);
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

    public SimpleCrudEngine Engine { get; }
    public IDbConnection Connection { get; }
    public IDbTransaction? Transaction { get; private set; }

    public SimpleCrudSession BeginTransaction(IsolationLevel isolationLevel = IsolationLevel.Unspecified)
    {
        if (Transaction is not null) throw new InvalidOperationException("A transaction is already active on this session.");
        Transaction = isolationLevel == IsolationLevel.Unspecified ? Connection.BeginTransaction() : Connection.BeginTransaction(isolationLevel);
        return this;
    }

    public void Commit()
    {
        (Transaction ?? throw new InvalidOperationException("No active transaction.")).Commit();
        Transaction.Dispose();
        Transaction = null;
    }

    public void Rollback()
    {
        Transaction?.Rollback();
        Transaction?.Dispose();
        Transaction = null;
    }

    public Task<T?> GetAsync<T>(object id) => Engine.GetAsync<T>(Connection, id, Transaction, _commandTimeout);
    public Task<IEnumerable<T>> GetListAsync<T>() => Engine.GetListAsync<T>(Connection, "", null, Transaction, _commandTimeout);
    public Task<IEnumerable<T>> GetListAsync<T>(object whereConditions) => Engine.GetListAsync<T>(Connection, whereConditions, Transaction, _commandTimeout);
    public Task<IEnumerable<T>> GetListAsync<T>(string conditions, object? parameters = null) => Engine.GetListAsync<T>(Connection, conditions, parameters, Transaction, _commandTimeout);
    public Task<IEnumerable<T>> GetListPagedAsync<T>(int pageNumber, int rowsPerPage, string conditions, string orderby, object? parameters = null) =>
        Engine.GetListPagedAsync<T>(Connection, pageNumber, rowsPerPage, conditions, orderby, parameters, Transaction, _commandTimeout);
    public Task<int?> InsertAsync<TEntity>(TEntity entity) => Engine.InsertAsync(Connection, entity, Transaction, _commandTimeout);
    public Task<TKey> InsertAsync<TKey, TEntity>(TEntity entity) => Engine.InsertAsync<TKey, TEntity>(Connection, entity, Transaction, _commandTimeout);
    public Task<int> UpdateAsync<TEntity>(TEntity entity) => Engine.UpdateAsync(Connection, entity, Transaction, _commandTimeout);
    public Task<int> DeleteAsync<T>(T entity) => Engine.DeleteAsync(Connection, entity, Transaction, _commandTimeout);
    public Task<int> DeleteAsync<T>(object id) => Engine.DeleteAsync<T>(Connection, id, Transaction, _commandTimeout);
    public Task<int> DeleteListAsync<T>(object whereConditions) => Engine.DeleteListAsync<T>(Connection, whereConditions, Transaction, _commandTimeout);
    public Task<int> DeleteListAsync<T>(string conditions, object? parameters = null) => Engine.DeleteListAsync<T>(Connection, conditions, parameters, Transaction, _commandTimeout);
    public Task<int> RecordCountAsync<T>(string conditions = "", object? parameters = null) => Engine.RecordCountAsync<T>(Connection, conditions, parameters, Transaction, _commandTimeout);
    public Task<int> RecordCountAsync<T>(object whereConditions) => Engine.RecordCountAsync<T>(Connection, whereConditions, Transaction, _commandTimeout);

    public void Dispose()
    {
        Transaction?.Dispose();
        Transaction = null;
        if (_ownsConnection) Connection.Dispose();
    }

#if NET
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
