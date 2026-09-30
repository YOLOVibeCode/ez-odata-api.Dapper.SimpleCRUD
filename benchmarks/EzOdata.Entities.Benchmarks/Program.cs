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
    await ReportBuilder.WriteAsync(tests, bench, dest, ReportBuilder.EnvironmentBlock());
    Console.WriteLine($"Wrote {Path.Combine(dest, "report.html")}");
    return 0;
}

var sqliteOnly = argsList.Contains("--sqlite-only");
var deep = argsList.Contains("--deep");
var iterations = int.TryParse(Arg(argsList, "--iterations"), out var n) ? n : 200;
var artifacts = Arg(argsList, "--out") ?? Path.Combine("artifacts", "compare", "ad-hoc");
Directory.CreateDirectory(artifacts);

await using var world = await BenchWorld.StartAsync(sqliteOnly);
Console.WriteLine($"Engines × databases: {world.Sessions.Count} sessions ({string.Join(", ", BenchWorld.DatabaseKinds)})");

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

internal sealed class BenchWorld : IAsyncDisposable
{
    public List<BenchSession> Sessions { get; } = [];
    private Databases? _docker;
    public static List<string> DatabaseKinds { get; private set; } = ["sqlite"];

    public static BenchWorld Current { get; private set; } = new();

    public static async Task<BenchWorld> StartAsync(bool sqliteOnly)
    {
        var world = new BenchWorld();
        var dbs = new List<TestDatabase> { TestDatabase.NewSqlite() };
        if (!sqliteOnly)
        {
            world._docker = new Databases();
            await world._docker.InitializeAsync();
            dbs.AddRange(world._docker.Available);
            foreach (var failure in world._docker.Failures)
            {
                Console.WriteLine($"Skipping {failure.Key}: {failure.Value}");
            }
        }

        DatabaseKinds = dbs.Select(d => d.Kind).Distinct().ToList();
        foreach (var engine in new[] { "stock", "simplecrud", "efcore" })
        {
            foreach (var db in dbs)
            {
                Console.WriteLine($"Starting {engine}/{db.Kind}…");
                world.Sessions.Add(await BenchSession.StartAsync(engine, await IsolateAsync(db, engine)));
            }
        }

        Current = world;
        return world;
    }

    public static BenchSession Require(string engine, string database) =>
        Current.Sessions.First(s => s.Engine == engine && s.Database.Kind == database);

    public async ValueTask DisposeAsync()
    {
        foreach (var session in Sessions) await session.DisposeAsync();
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
                await admin.ExecuteAsync($"CREATE DATABASE {name}");
                return db with { Database = name, ConnectionString = new MySqlConnectionStringBuilder(db.ConnectionString) { Database = name }.ConnectionString };
            case "sqlserver":
                await admin.ExecuteAsync($"CREATE DATABASE [{name}]");
                return db with { Database = name, ConnectionString = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = name }.ConnectionString };
            default:
                return db;
        }
    }
}
