using System.Data;
using System.Data.Common;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using EzOdata.Core.Query;
using EzOdata.Core.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace EzOdata.Entities.AspNetCore;

/// <summary>
/// Connector type → ADO.NET connection, isolation, and error taxonomy. Connection settings
/// mirror ez-odata's own connectors (whose builders are internal to those packages).
/// </summary>
internal static class ProviderConnections
{
    public static IsolationLevel IsolationFor(string connectorType, bool guarded)
    {
        if (!guarded) return IsolationLevel.Unspecified;
        return connectorType == ConnectorTypes.PostgreSql ? IsolationLevel.RepeatableRead : IsolationLevel.Serializable;
    }

    public static DbConnection Create(string connectorType, ConnectionSpec spec) => connectorType switch
    {
        ConnectorTypes.Sqlite => new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = spec.FilePath,
            Mode = spec.ReadOnlyFile ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString()),

        ConnectorTypes.PostgreSql => new NpgsqlConnection(Postgres(spec)),

        ConnectorTypes.MySql => new MySqlConnection(new MySqlConnectionStringBuilder
        {
            Server = spec.Host,
            Port = (uint)(spec.Port ?? 3306),
            Database = spec.Database,
            UserID = spec.Username,
            Password = spec.Password,
            ApplicationName = "ez-odata-api+entities",
            SslMode = spec.Tls.Mode switch
            {
                "disable" => MySqlSslMode.None,
                "require" when spec.Tls.AllowInvalid => MySqlSslMode.Required,
                "require" => MySqlSslMode.VerifyFull,
                _ => MySqlSslMode.Preferred,
            },
            AllowPublicKeyRetrieval = spec.Tls.Mode == "disable",
        }.ConnectionString),

        ConnectorTypes.SqlServer => new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = spec.Port is { } port ? $"{spec.Host},{port}" : spec.Host,
            InitialCatalog = spec.Database,
            UserID = spec.Username,
            Password = spec.Password,
            ApplicationName = "ez-odata-api+entities",
            Encrypt = spec.Tls.Mode != "disable",
            TrustServerCertificate = spec.Tls.AllowInvalid,
        }.ConnectionString),

        _ => throw new NotSupportedException($"No connection factory for connector '{connectorType}'; use UseConnection(...)."),
    };

    private static string Postgres(ConnectionSpec spec)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = spec.Host,
            Database = spec.Database,
            Username = spec.Username,
            Password = spec.Password,
            ApplicationName = "ez-odata-api+entities",
            SslMode = spec.Tls.Mode switch
            {
                "disable" => SslMode.Disable,
                "require" when spec.Tls.AllowInvalid => SslMode.Require,
                "require" => SslMode.VerifyFull,
                _ => SslMode.Prefer,
            },
        };
        if (spec.Port is { } port) builder.Port = port;
        return builder.ConnectionString;
    }

    /// <summary>Deadlocks / serialization failures / busy: safe to retry the whole transaction.</summary>
    public static bool IsTransientConflict(DbException exception) => exception switch
    {
        SqliteException e => e.SqliteErrorCode is 5 or 6,
        PostgresException e => e.SqlState is "40001" or "40P01",
        MySqlException e => e.Number is 1213 or 1205,
        SqlException e => e.Number is 1205,
        _ => false,
    };

    /// <summary>Provider exception → ez-odata error taxonomy (→ HTTP status in the engine).</summary>
    public static Exception Map(DbException exception) => exception switch
    {
        SqliteException e => e.SqliteExtendedErrorCode switch
        {
            2067 or 1555 => Unique(e),
            787 => ForeignKey(e),
            1299 => NotNull(e),
            _ when e.SqliteErrorCode == 5 => Conflict(e),
            _ => Unmapped(e),
        },
        PostgresException e => e.SqlState switch
        {
            "23505" => Unique(e),
            "23503" => ForeignKey(e),
            "23502" => NotNull(e),
            "40001" or "40P01" => Conflict(e),
            _ => Unmapped(e),
        },
        MySqlException e => e.Number switch
        {
            1062 => Unique(e),
            1451 or 1452 => ForeignKey(e),
            1048 => NotNull(e),
            1213 or 1205 => Conflict(e),
            _ => Unmapped(e),
        },
        SqlException e => e.Number switch
        {
            2627 or 2601 => Unique(e),
            547 => ForeignKey(e),
            515 => NotNull(e),
            1205 => Conflict(e),
            _ => Unmapped(e),
        },
        _ => Unmapped(exception),
    };

    private static ConnectorException Unique(Exception e) => new(ErrorCodes.ConflictUniqueViolation, "Unique constraint violated.", inner: e);
    private static ConnectorException ForeignKey(Exception e) => new(ErrorCodes.ConflictForeignKeyViolation, "Foreign key constraint violated.", inner: e);
    private static ConnectorException NotNull(Exception e) => new(ErrorCodes.ValidationNotNullViolation, "A required column cannot be null.", inner: e);
    private static ConnectorException Conflict(Exception e) => new(ErrorCodes.UpstreamUnavailable, "The write conflicted with a concurrent transaction; retry.", isTransient: true, inner: e);
    private static ConnectorException Unmapped(Exception e) => new(ErrorCodes.InternalUnmapped, "Database error.", inner: e);
}
