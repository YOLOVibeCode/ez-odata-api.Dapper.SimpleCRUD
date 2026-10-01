using Dapper;
using EzOdata.Entities.Benchmarks;
using EzOdata.SimpleCrud.Testing;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

var argsList = args.ToList();
if (argsList.Contains("report") || argsList.Contains("--report"))
{
    var tests = Arg(argsList, "--tests") ?? Path.Combine(Directory.GetCurrentDirectory(), "tests");
    var bench = Arg(argsList, "--bench");
    var dest = Arg(argsList, "--out") ?? Directory.GetCurrentDirectory();
    var runs = Arg(argsList, "--runs") ?? dest;
    await ReportBuilder.WriteAsync(tests, runs, bench, dest);
    Console.WriteLine($"Wrote {Path.Combine(dest, "report.html")}");
    return 0;
}

var only = Arg(argsList, "--only"); // one database per process: sqlite | postgresql | mysql | sqlserver
if (only is not null) Environment.SetEnvironmentVariable("EZSC_DATABASES", only);
var sqliteOnly = argsList.Contains("--sqlite-only") || only == "sqlite";
var deep = argsList.Contains("--deep");
var dataAccess = argsList.Contains("--data-access");
var iterations = int.TryParse(Arg(argsList, "--iterations"), out var n) ? n : 200;
var artifacts = Arg(argsList, "--out") ?? Path.Combine("artifacts", "compare", "ad-hoc");
Directory.CreateDirectory(artifacts);

var all = argsList.Contains("--all");

// One process, one set of containers. The data-access suite runs first, on the bare databases; the HTTP
// services are started only afterwards so they cannot disturb it.
await using var world = await BenchWorld.StartAsync(sqliteOnly, withSessions: false);
Console.WriteLine($"Databases: {string.Join(", ", BenchWorld.DatabaseKinds)}");

if (dataAccess || all)
{
    // The libraries on their own: raw Dapper, Dapper.SimpleCRUD, EF Core. No HTTP, no ez-odata.
    var problems = await DataAccessVerification.RunAsync(world.AllDatabases);
    if (problems.Count > 0)
    {
        foreach (var problem in problems) Console.Error.WriteLine($"MISMATCH {problem}");
        return 1;
    }

    Console.WriteLine("Verified: every library reads, inserts, updates and deletes the same rows the same way.");
    BenchmarkDotNet.Running.BenchmarkRunner.Run([typeof(DataAccessReads), typeof(DataAccessWrites)],
        EngineBenchmarks.Config(Path.Combine(artifacts, "data-access"), thorough: !argsList.Contains("--quick")));
    if (!all) return 0;
}

await world.StartSessionsAsync();
Console.WriteLine($"Engines × databases: {world.Sessions.Count} HTTP services");

if (deep)
{
    EngineBenchmarks.Run(Path.Combine(artifacts, "bench"));
}
else
{
    var rows = await QuickHarness.RunAsync(world.Sessions, iterations);
    var json = Path.Combine(artifacts, "bench", "quick.json");
    await QuickHarness.WriteJsonAsync(json, rows, ReportBuilder.EnvironmentBlock());
    Console.WriteLine($"Wrote {json} ({rows.Count} rows)");
}

return 0;

static string? Arg(List<string> list, string name)
{
    var i = list.IndexOf(name);
    return i >= 0 && i + 1 < list.Count ? list[i + 1] : null;
}

public sealed class BenchWorld : IAsyncDisposable
{
    public List<BenchSession> Sessions { get; } = [];
    public List<TestDatabase> AllDatabases { get; } = [];
    private Databases? _docker;
    public static List<string> DatabaseKinds { get; private set; } = ["sqlite"];

    public static BenchWorld Current { get; private set; } = new();

    public static async Task<BenchWorld> StartAsync(bool sqliteOnly, bool withSessions = true)
    {
        var world = new BenchWorld();
        Current = world;
        var only = Environment.GetEnvironmentVariable("EZSC_DATABASES");
        var dbs = new List<TestDatabase>();
        if (string.IsNullOrWhiteSpace(only) || only.Contains("sqlite")) dbs.Add(TestDatabase.NewSqlite());
        if (!sqliteOnly)
        {
            world._docker = new Databases();
            await world._docker.InitializeAsync();
            dbs.AddRange(world._docker.Available);
            foreach (var failure in world._docker.Failures.Where(f => !f.Value.StartsWith("not selected", StringComparison.Ordinal)))
            {
                Console.WriteLine($"Skipping {failure.Key}: {failure.Value}");
            }
        }

        DatabaseKinds = dbs.Select(d => d.Kind).Distinct().ToList();
        world.AllDatabases.AddRange(dbs);
        if (withSessions) await world.StartSessionsAsync();
        return world;
    }

    public async Task StartSessionsAsync()
    {
        foreach (var engine in new[] { "stock", "simplecrud", "efcore" })
        {
            foreach (var db in AllDatabases)
            {
                if (engine == "efcore" && db.Kind == "mysql")
                {
                    Console.WriteLine("Skipping efcore/mysql: no MySqlConnector-compatible EF Core 10 provider.");
                    continue;
                }
                Console.WriteLine($"Starting {engine}/{db.Kind}…");
                var isolated = await IsolateAsync(db, engine);
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        Sessions.Add(await BenchSession.StartAsync(engine, isolated));
                        break;
                    }
                    catch (Exception ex) when (attempt < 3)
                    {
                        // A database container starved for memory can miss one connection; the schema scripts
                        // start with DROP ... IF EXISTS, so starting again is safe. SqlClient replays a failed
                        // login from its pool for a while, hence the pool clear.
                        Console.WriteLine($"Retrying {engine}/{db.Kind} ({attempt}): {ex.Message.Split('\n')[0]}");
                        SqlConnection.ClearAllPools();
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                    }
                }
            }
        }
    }

    public static TestDatabase RequireDatabase(string kind)
    {
        var db = Current.AllDatabases.First(d => d.Kind == kind);
        // Every library opens a connection per operation, as a web request would, so pool connections for all
        // of them (the shared SQLite helper turns pooling off to let tests delete the file).
        return kind == "sqlite" ? db with { ConnectionString = db.ConnectionString.Replace("Pooling=False", "Pooling=True") } : db;
    }

    public static BenchSession Require(string engine, string database) =>
        Current.Sessions.First(s => s.Engine == engine && s.Database.Kind == database);

    public async ValueTask DisposeAsync()
    {
        foreach (var session in Sessions) await session.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in AllDatabases.Select(d => d.FilePath).OfType<string>().Where(File.Exists)) File.Delete(path);
        if (_docker is not null) await _docker.DisposeAsync();
    }

    private static async Task<TestDatabase> IsolateAsync(TestDatabase db, string engine)
    {
        if (db.Kind == "sqlite") return TestDatabase.NewSqlite();
        var name = $"ez{engine}{Guid.NewGuid():N}"[..16];
        await using var admin = db.Connect();
        await admin.OpenAsync();
        switch (db.Kind)
        {
            case "postgresql":
                await admin.ExecuteAsync($"CREATE DATABASE {name}");
                return db with { Database = name, ConnectionString = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = name }.ConnectionString };
            case "mysql":
            {
                // The container's application user cannot create databases; root can (Testcontainers gives root the
                // same password). Create as root, then give the application user the new database.
                var rootCs = new MySqlConnectionStringBuilder(db.ConnectionString) { UserID = "root" };
                await using var root = new MySqlConnection(rootCs.ConnectionString);
                await root.OpenAsync();
                await root.ExecuteAsync($"CREATE DATABASE {name}; GRANT ALL PRIVILEGES ON {name}.* TO '{db.Username}'@'%'; FLUSH PRIVILEGES;");
                return db with { Database = name, ConnectionString = new MySqlConnectionStringBuilder(db.ConnectionString) { Database = name }.ConnectionString };
            }
            case "sqlserver":
                await admin.ExecuteAsync($"CREATE DATABASE [{name}]");
                return db with { Database = name, ConnectionString = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = name }.ConnectionString };
            default:
                return db;
        }
    }
}
