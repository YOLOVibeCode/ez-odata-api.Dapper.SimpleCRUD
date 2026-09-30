using System.Data;
using System.Data.Common;
using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.Abstractions.Sql;
using EzOdata.Core.Query;

namespace EzOdata.Entities.AspNetCore;

/// <summary>
/// Reads inside the write's transaction using ez-odata's own compiler and row reader, so results have
/// exactly the shape the stock connector returns (and every value stays a parameter).
/// </summary>
internal sealed class WriteToolkit
{
    private readonly SqlCompiler _compiler;
    private readonly DbConnection _connection;
    private readonly IDbTransaction? _transaction;
    private readonly CancellationToken _ct;

    public WriteToolkit(ISqlDialect dialect, DbConnection connection, IDbTransaction? transaction, CancellationToken ct)
    {
        _compiler = new SqlCompiler(dialect);
        _connection = connection;
        _transaction = transaction;
        _ct = ct;
    }

    public async Task<Row?> ReadByKeyAsync(WriteExecution execution, EntityBinding binding, IReadOnlyDictionary<string, object?> key)
    {
        var compiled = _compiler.CompileSelect(execution.Schema, new QueryRequest
        {
            ServiceName = execution.Write.ServiceName,
            Table = binding.Table.ExposedName,
            Filter = KeyFilter(key),
            Top = 1,
        });

        using var command = AdoQueryExecutor.Build(_connection, (DbTransaction?)_transaction, compiled, execution.Options);
        using var reader = await command.ExecuteReaderAsync(_ct);
        return await reader.ReadAsync(_ct) ? AdoQueryExecutor.ReadRow(reader) : null;
    }

    public async Task<bool> ExistsAsync(WriteExecution execution, EntityBinding binding, IReadOnlyDictionary<string, object?> key, FilterNode predicate)
    {
        var compiled = _compiler.CompileCount(execution.Schema, new QueryRequest
        {
            ServiceName = execution.Write.ServiceName,
            Table = binding.Table.ExposedName,
            Filter = EzFilter.And(KeyFilter(key), predicate),
        });

        using var command = AdoQueryExecutor.Build(_connection, (DbTransaction?)_transaction, compiled, execution.Options);
        return Convert.ToInt64(await command.ExecuteScalarAsync(_ct)) > 0;
    }

    private static FilterNode KeyFilter(IReadOnlyDictionary<string, object?> key) =>
        key.Aggregate((FilterNode?)null, (filter, pair) => EzFilter.And(filter, EzFilter.Eq(pair.Key, pair.Value)))!;
}
