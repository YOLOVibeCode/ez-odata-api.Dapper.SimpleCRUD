using System.Data;

namespace EzOdata.SimpleCrud.Testing;

/// <summary>Wraps a connection and records every SQL statement (sync ADO paths, which SimpleCRUD's sync API uses).</summary>
public sealed class RecordingConnection(IDbConnection inner) : IDbConnection
{
    public List<string> Sql { get; } = [];

#pragma warning disable CS8767
    public string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
#pragma warning restore CS8767
    public int ConnectionTimeout => inner.ConnectionTimeout;
    public string Database => inner.Database;
    public ConnectionState State => inner.State;
    public IDbTransaction BeginTransaction() => inner.BeginTransaction();
    public IDbTransaction BeginTransaction(IsolationLevel il) => inner.BeginTransaction(il);
    public void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
    public void Close() => inner.Close();
    public void Open() => inner.Open();
    public void Dispose() => inner.Dispose();
    public IDbCommand CreateCommand() => new Command(inner.CreateCommand(), Sql);

    private sealed class Command(IDbCommand c, List<string> log) : IDbCommand
    {
#pragma warning disable CS8767
        public string CommandText { get => c.CommandText; set { c.CommandText = value; lock (log) log.Add(value.Trim()); } }
#pragma warning restore CS8767
        public int CommandTimeout { get => c.CommandTimeout; set => c.CommandTimeout = value; }
        public CommandType CommandType { get => c.CommandType; set => c.CommandType = value; }
        public IDbConnection? Connection { get => c.Connection; set => c.Connection = value; }
        public IDataParameterCollection Parameters => c.Parameters;
        public IDbTransaction? Transaction { get => c.Transaction; set => c.Transaction = value; }
        public UpdateRowSource UpdatedRowSource { get => c.UpdatedRowSource; set => c.UpdatedRowSource = value; }
        public void Cancel() => c.Cancel();
        public IDbDataParameter CreateParameter() => c.CreateParameter();
        public void Dispose() => c.Dispose();
        public int ExecuteNonQuery() => c.ExecuteNonQuery();
        public IDataReader ExecuteReader() => c.ExecuteReader();
        public IDataReader ExecuteReader(CommandBehavior behavior) => c.ExecuteReader(behavior);
        public object? ExecuteScalar() => c.ExecuteScalar();
        public void Prepare() => c.Prepare();
    }
}
