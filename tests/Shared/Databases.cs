using System.Data.Common;
using System.Runtime.InteropServices;
using DotNet.Testcontainers.Builders;
using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EzOdata.SimpleCrud.Testing;

/// <summary>A reachable test database.</summary>
public sealed record TestDatabase(
    string Kind, SimpleCRUD.Dialect Dialect, string ConnectionString,
    string? Host = null, int? Port = null, string? Database = null, string? Username = null, string? Password = null, string? FilePath = null)
{
    public DbConnection Connect() => Kind switch
    {
        "sqlite" => new SqliteConnection(ConnectionString),
        "postgresql" => new NpgsqlConnection(ConnectionString),
        "mysql" => new MySqlConnection(ConnectionString),
        "sqlserver" => new SqlConnection(ConnectionString),
        _ => throw new NotSupportedException(Kind),
    };

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = Connect();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }

    public static TestDatabase NewSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ezsc-{Guid.NewGuid():N}.db");
        return new("sqlite", SimpleCRUD.Dialect.SQLite, $"Data Source={path};Foreign Keys=True;Pooling=False", FilePath: path);
    }
}

/// <summary>
/// Real PostgreSQL, MySQL and SQL Server in Docker (Testcontainers), started in parallel once per test
/// class. A database that cannot start is recorded in <see cref="Failures"/> and its tests skip with the
/// reason. Set EZSC_SKIP_DOCKER=1 to skip all of them.
/// </summary>
public sealed class Databases : IAsyncLifetime
{
    private readonly List<IContainer> _containers = [];
    private FileStream? _machineLock;

    public TestDatabase? Postgres { get; private set; }
    public TestDatabase? MySql { get; private set; }
    public TestDatabase? SqlServer { get; private set; }
    public Dictionary<string, string> Failures { get; } = [];
    public string? SqlServerImage { get; private set; }

    public IEnumerable<TestDatabase> Available => new[] { Postgres, MySql, SqlServer }.Where(d => d is not null)!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("EZSC_SKIP_DOCKER") == "1")
        {
            Failures["docker"] = "EZSC_SKIP_DOCKER=1";
            return;
        }

        ConfigureDockerEndpoint();

        // Test assemblies run in parallel; without this, each would start its own three database
        // servers at once (six containers, two SQL Servers) and starve a small Docker VM.
        _machineLock = await AcquireMachineLockAsync();

        await Task.WhenAll(
            Start("postgresql", async () =>
            {
                var c = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
                _containers.Add(c);
                await c.StartAsync();
                var b = new NpgsqlConnectionStringBuilder(c.GetConnectionString());
                Postgres = new("postgresql", SimpleCRUD.Dialect.PostgreSQL, c.GetConnectionString(), b.Host, b.Port, b.Database, b.Username, b.Password);
            }),
            Start("mysql", async () =>
            {
                var c = new MySqlBuilder().WithImage("mysql:8.4").Build();
                _containers.Add(c);
                await c.StartAsync();
                var b = new MySqlConnectionStringBuilder(c.GetConnectionString()) { AllowPublicKeyRetrieval = true, SslMode = MySqlSslMode.None };
                MySql = new("mysql", SimpleCRUD.Dialect.MySQL, b.ConnectionString, b.Server, (int)b.Port, b.Database, b.UserID, b.Password);
            }),
            Start("sqlserver", async () =>
            {
                // SQL Server 2022 is x86-only and crashes under QEMU; on ARM hosts use Azure SQL Edge
                // (same engine and T-SQL dialect, ARM-native). Override with EZSC_MSSQL_IMAGE.
                SqlServerImage = Environment.GetEnvironmentVariable("EZSC_MSSQL_IMAGE")
                    ?? (RuntimeInformation.OSArchitecture == Architecture.Arm64
                        ? "mcr.microsoft.com/azure-sql-edge:latest"
                        : "mcr.microsoft.com/mssql/server:2022-latest");
                var c = new MsSqlBuilder()
                    .WithImage(SqlServerImage)
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Recovery is complete"))
                    .Build();
                _containers.Add(c);
                await c.StartAsync();
                var b = new SqlConnectionStringBuilder(c.GetConnectionString()) { TrustServerCertificate = true };
                await WaitForSqlServerAsync(b.ConnectionString);
                var hostPort = b.DataSource.Split(',');
                SqlServer = new("sqlserver", SimpleCRUD.Dialect.SQLServer, b.ConnectionString, hostPort[0], int.Parse(hostPort[1]), b.InitialCatalog, b.UserID, b.Password);
            }));
    }

    private async Task Start(string kind, Func<Task> start)
    {
        // EZSC_DATABASES=postgresql,sqlserver starts only those (one at a time keeps a small Docker VM happy).
        var only = Environment.GetEnvironmentVariable("EZSC_DATABASES");
        if (!string.IsNullOrWhiteSpace(only) && !only.Split(',', StringSplitOptions.TrimEntries).Contains(kind))
        {
            Failures[kind] = $"not selected (EZSC_DATABASES={only})";
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await start().WaitAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            Failures[kind] = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var container in _containers)
        {
            try { await container.DisposeAsync(); } catch { /* best effort */ }
        }

        _machineLock?.Dispose();
    }

    private static async Task<FileStream> AcquireMachineLockAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "ezodata-simplecrud-docker.lock");
        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
    }

    private static async Task WaitForSqlServerAsync(string connectionString)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.databases", connection);
                await command.ExecuteScalarAsync();
                return;
            }
            catch (SqlException) when (attempt < 30)
            {
                await Task.Delay(1000);
            }
        }
    }

    public string Why(string kind) => Failures.TryGetValue(kind, out var why) ? why : Failures.GetValueOrDefault("docker", "not started");

    /// <summary>Colima / Docker Desktop: point Testcontainers at the active socket when DOCKER_HOST is unset.</summary>
    private static void ConfigureDockerEndpoint()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST"))) return;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var colima = Path.Combine(home, ".colima", "default", "docker.sock");
        if (File.Exists(colima))
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", "unix://" + colima);
            Environment.SetEnvironmentVariable("TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE", "/var/run/docker.sock");
            Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
        }
    }
}
