using System.Data;
using Dapper;

namespace EzOdata.SimpleCrud;

/// <summary>A SimpleCRUD client for one database: an engine plus a connection factory.</summary>
public interface ISimpleCrud : ISimpleCrudOperations
{
    /// <summary>Opens a connection (and optionally a transaction) for several operations.</summary>
    Task<SimpleCrudSession> OpenSessionAsync(bool beginTransaction = false, IsolationLevel isolationLevel = IsolationLevel.Unspecified);

    /// <summary>A new, unopened connection from the configured factory.</summary>
    IDbConnection CreateConnection();
}

/// <summary>Entry point: <c>SimpleCrud.For(Dialect.PostgreSQL).WithConnection(...).Build()</c>.</summary>
public static class SimpleCrud
{
    public static SimpleCrudBuilder For(SimpleCRUD.Dialect dialect) => new(dialect);
}

public sealed class SimpleCrudBuilder
{
    private readonly SimpleCRUD.Dialect _dialect;
    private Func<IDbConnection>? _connectionFactory;
    private SimpleCrudNaming? _naming;
    private bool _shared;
    private bool _claimProcessDialect;
    private int? _commandTimeout;

    internal SimpleCrudBuilder(SimpleCRUD.Dialect dialect) => _dialect = dialect;

    public SimpleCrudBuilder WithConnection(Func<IDbConnection> factory) { _connectionFactory = factory; return this; }

    /// <summary>Per-engine naming (isolated engines only). Reuse the same instance across clients to share an engine.</summary>
    public SimpleCrudBuilder WithNaming(SimpleCrudNaming naming) { _naming = naming; return this; }

    public SimpleCrudBuilder WithCommandTimeout(int seconds) { _commandTimeout = seconds; return this; }

    /// <summary>Use the process-wide SimpleCRUD instead of an isolated copy (see <see cref="SimpleCrudEngines.Shared"/>).</summary>
    public SimpleCrudBuilder UseProcessWideSimpleCrud(bool claimProcessDialect = false)
    {
        _shared = true;
        _claimProcessDialect = claimProcessDialect;
        return this;
    }

    public ISimpleCrud Build()
    {
        var factory = _connectionFactory ?? throw new InvalidOperationException("Call WithConnection(...) before Build().");
        if (_shared && _naming is not null)
        {
            throw new InvalidOperationException("Per-engine naming needs an isolated engine; the process-wide SimpleCRUD uses its global resolvers.");
        }

        var engine = _shared
            ? SimpleCrudEngines.Shared(_dialect, _claimProcessDialect)
            : SimpleCrudEngines.For(_dialect, _naming);
        return new SimpleCrudClient(engine, factory, _commandTimeout);
    }
}

/// <summary>Each call opens a connection, runs one SimpleCRUD operation, and disposes it.</summary>
internal sealed class SimpleCrudClient : ISimpleCrud
{
    private readonly Func<IDbConnection> _factory;
    private readonly int? _commandTimeout;

    public SimpleCrudClient(SimpleCrudEngine engine, Func<IDbConnection> factory, int? commandTimeout)
    {
        Engine = engine;
        _factory = factory;
        _commandTimeout = commandTimeout;
    }

    public SimpleCrudEngine Engine { get; }

    public IDbConnection CreateConnection() => _factory();

    public async Task<SimpleCrudSession> OpenSessionAsync(bool beginTransaction = false, IsolationLevel isolationLevel = IsolationLevel.Unspecified)
    {
        var connection = _factory();
        try
        {
            await SimpleCrudSession.OpenAsync(connection).ConfigureAwait(false);
            var session = new SimpleCrudSession(Engine, connection, null, _commandTimeout, ownsConnection: true);
            return beginTransaction ? session.BeginTransaction(isolationLevel) : session;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task<TResult> Run<TResult>(Func<SimpleCrudSession, Task<TResult>> operation)
    {
        using var session = await OpenSessionAsync().ConfigureAwait(false);
        return await operation(session).ConfigureAwait(false);
    }

    public Task<T?> GetAsync<T>(object id) => Run(s => s.GetAsync<T>(id));
    public Task<IEnumerable<T>> GetListAsync<T>() => Run(s => s.GetListAsync<T>());
    public Task<IEnumerable<T>> GetListAsync<T>(object whereConditions) => Run(s => s.GetListAsync<T>(whereConditions));
    public Task<IEnumerable<T>> GetListAsync<T>(string conditions, object? parameters = null) => Run(s => s.GetListAsync<T>(conditions, parameters));
    public Task<IEnumerable<T>> GetListPagedAsync<T>(int pageNumber, int rowsPerPage, string conditions, string orderby, object? parameters = null) =>
        Run(s => s.GetListPagedAsync<T>(pageNumber, rowsPerPage, conditions, orderby, parameters));
    public Task<int?> InsertAsync<TEntity>(TEntity entity) => Run(s => s.InsertAsync(entity));
    public Task<TKey> InsertAsync<TKey, TEntity>(TEntity entity) => Run(s => s.InsertAsync<TKey, TEntity>(entity));
    public Task<int> UpdateAsync<TEntity>(TEntity entity) => Run(s => s.UpdateAsync(entity));
    public Task<int> DeleteAsync<T>(T entity) => Run(s => s.DeleteAsync(entity));
    public Task<int> DeleteAsync<T>(object id) => Run(s => s.DeleteAsync<T>(id));
    public Task<int> DeleteListAsync<T>(object whereConditions) => Run(s => s.DeleteListAsync<T>(whereConditions));
    public Task<int> DeleteListAsync<T>(string conditions, object? parameters = null) => Run(s => s.DeleteListAsync<T>(conditions, parameters));
    public Task<int> RecordCountAsync<T>(string conditions = "", object? parameters = null) => Run(s => s.RecordCountAsync<T>(conditions, parameters));
    public Task<int> RecordCountAsync<T>(object whereConditions) => Run(s => s.RecordCountAsync<T>(whereConditions));
}
