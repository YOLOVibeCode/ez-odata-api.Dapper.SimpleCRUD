extern alias baseline;
extern alias cached;

using System.Data.Common;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using Testcontainers.PostgreSql;
using B = baseline::Dapper.SimpleCRUD;
using C = cached::Dapper.SimpleCRUD;

// Dapper.SimpleCRUD master vs the same code with per-type property caching, in one process.
// Usage: dotnet run -c Release -- sqlite|postgresql [--out dir]
var kind = args.FirstOrDefault() ?? "sqlite";
var outDir = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine("results", kind);

PostgreSqlContainer? container = null;
if (kind == "postgresql")
{
    container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    await container.StartAsync();
    World.ConnectionString = container.GetConnectionString();
}
else
{
    World.ConnectionString = "Data Source=simplecrud-ab;Mode=Memory;Cache=Shared";
    World.KeepAlive = new SqliteConnection(World.ConnectionString); // an in-memory database lives while a connection is open
    World.KeepAlive.Open();
}

World.Kind = kind;
World.Reset();
var problems = World.Verify();
if (problems.Count > 0)
{
    problems.ForEach(p => Console.Error.WriteLine("MISMATCH " + p));
    return 1;
}

Console.WriteLine("Verified: both builds return identical rows and write identical data.");
var config = ManualConfig.CreateEmpty()
    .AddJob(Job.Default.WithWarmupCount(5).WithIterationCount(20)
        .WithIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(250))
        .WithToolchain(InProcessEmitToolchain.Instance).WithId(kind))
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddColumnProvider(DefaultColumnProviders.Instance)
    .AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default)
    .AddExporter(MarkdownExporter.GitHub)
    .AddExporter(JsonExporter.Full)
    .WithArtifactsPath(outDir)
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);
BenchmarkRunner.Run<SimpleCrudAB>(config);

World.KeepAlive?.Dispose();
if (container is not null) await container.DisposeAsync();
return 0;

[baseline::Dapper.Table("people")]
public class Person
{
    [baseline::Dapper.Key] public int Id { get; set; }
    [baseline::Dapper.Column("full_name")] public string Name { get; set; }
    public string Email { get; set; }
    public string Country { get; set; }
    public int Score { get; set; }
    public bool IsActive { get; set; }
    public double Balance { get; set; }
    public string Notes { get; set; }
}

public static class World
{
    public const int Rows = 5_000;
    public static readonly string[] Countries = ["US", "CA", "GB", "DE", "FR", "JP", "BR", "IN"];
    public static string Kind = "sqlite";
    public static string ConnectionString = "";
    public static SqliteConnection? KeepAlive;

    public static DbConnection Open()
    {
        DbConnection c = Kind == "postgresql" ? new NpgsqlConnection(ConnectionString) : new SqliteConnection(ConnectionString);
        c.Open();
        return c;
    }

    public static Person NewPerson(int i) => new()
    {
        Name = $"Person {i}", Email = $"person{i}@example.com", Country = Countries[i % Countries.Length],
        Score = i % 1000, IsActive = i % 3 != 0, Balance = i * 1.25, Notes = i % 7 == 0 ? null : "regular customer",
    };

    public static void Reset()
    {
        using var c = Open();
        c.Execute(Kind == "postgresql"
            ? """DROP TABLE IF EXISTS people; CREATE TABLE people ("Id" SERIAL PRIMARY KEY, full_name TEXT NOT NULL, "Email" TEXT NOT NULL, "Country" TEXT NOT NULL, "Score" INT NOT NULL, "IsActive" BOOLEAN NOT NULL, "Balance" DOUBLE PRECISION NOT NULL, "Notes" TEXT); CREATE INDEX ix_people_country ON people("Country");"""
            : """DROP TABLE IF EXISTS people; CREATE TABLE people ("Id" INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, "Email" TEXT NOT NULL, "Country" TEXT NOT NULL, "Score" INTEGER NOT NULL, "IsActive" INTEGER NOT NULL, "Balance" REAL NOT NULL, "Notes" TEXT); CREATE INDEX ix_people_country ON people("Country");""");
        using var tx = c.BeginTransaction();
        c.Execute("""INSERT INTO people (full_name, "Email", "Country", "Score", "IsActive", "Balance", "Notes") VALUES (@Name, @Email, @Country, @Score, @IsActive, @Balance, @Notes)""",
            Enumerable.Range(1, Rows).Select(NewPerson), tx);
        tx.Commit();
    }

    public static void SetDialects()
    {
        B.SetDialect(Kind == "postgresql" ? B.Dialect.PostgreSQL : B.Dialect.SQLite);
        C.SetDialect(Kind == "postgresql" ? C.Dialect.PostgreSQL : C.Dialect.SQLite);
    }

    /// <summary>Both builds must read the same rows and leave the same data behind.</summary>
    public static List<string> Verify()
    {
        SetDialects();
        var problems = new List<string>();
        using var c = Open();
        string J(object o) => JsonSerializer.Serialize(o);
        void Same(string what, object b, object x) { if (J(b) != J(x)) problems.Add($"{what}: {J(b)} vs {J(x)}"); }

        Same("Get", B.GetAsync<Person>(c, 42).Result, C.GetAsync<Person>(c, 42).Result);
        Same("GetList(where)", B.GetListAsync<Person>(c, new { Score = 42 }).Result, C.GetListAsync<Person>(c, new { Score = 42 }).Result);
        Same("GetListPaged", B.GetListPagedAsync<Person>(c, 3, 20, "where \"Country\" = @country", "\"Id\"", new { country = "DE" }).Result,
            C.GetListPagedAsync<Person>(c, 3, 20, "where \"Country\" = @country", "\"Id\"", new { country = "DE" }).Result);
        Same("RecordCount", B.RecordCountAsync<Person>(c, "where \"IsActive\" = @a", new { a = true }).Result,
            C.RecordCountAsync<Person>(c, "where \"IsActive\" = @a", new { a = true }).Result);

        var idB = B.InsertAsync(c, NewPerson(900_001)).Result!.Value;
        var idC = C.InsertAsync(c, NewPerson(900_001)).Result!.Value;
        var rowB = B.GetAsync<Person>(c, idB).Result; rowB.Score = 4242; B.UpdateAsync(c, rowB).Wait();
        var rowC = C.GetAsync<Person>(c, idC).Result; rowC.Score = 4242; C.UpdateAsync(c, rowC).Wait();
        var afterB = B.GetAsync<Person>(c, idB).Result; afterB.Id = 0;
        var afterC = C.GetAsync<Person>(c, idC).Result; afterC.Id = 0;
        Same("Insert + Update", afterB, afterC);
        if (afterB.Score != 4242) problems.Add("update not persisted");
        B.DeleteAsync<Person>(c, idB).Wait();
        C.DeleteAsync<Person>(c, idC).Wait();
        if (B.GetAsync<Person>(c, idB).Result is not null || C.GetAsync<Person>(c, idC).Result is not null) problems.Add("delete not persisted");
        return problems;
    }
}

[MemoryDiagnoser]
public class SimpleCrudAB
{
    [Params("master", "cached")]
    public string Build { get; set; } = "master";

    private bool M => Build == "master";
    private int _i;

    [GlobalSetup]
    public void Setup()
    {
        World.Reset();
        World.SetDialects();
        _i = World.Rows;
    }

    private int Next() => Interlocked.Increment(ref _i);

    [Benchmark(Description = "Get(id)")]
    public async Task<Person> Get()
    {
        using var c = World.Open();
        var id = Next() % World.Rows + 1;
        return M ? await B.GetAsync<Person>(c, id) : await C.GetAsync<Person>(c, id);
    }

    [Benchmark(Description = "GetList(new { Score })")]
    public async Task<int> GetListWhere()
    {
        using var c = World.Open();
        var where = new { Score = Next() % 1000 };
        return (M ? await B.GetListAsync<Person>(c, where) : await C.GetListAsync<Person>(c, where)).Count();
    }

    [Benchmark(Description = "GetListPaged(20 rows)")]
    public async Task<int> Paged()
    {
        using var c = World.Open();
        var p = new { country = World.Countries[Next() % World.Countries.Length] };
        const string where = "where \"Country\" = @country", order = "\"Id\"";
        return (M ? await B.GetListPagedAsync<Person>(c, 2, 20, where, order, p) : await C.GetListPagedAsync<Person>(c, 2, 20, where, order, p)).Count();
    }

    [Benchmark(Description = "Insert")]
    public async Task<int?> Insert()
    {
        using var c = World.Open();
        var p = World.NewPerson(Next());
        return M ? await B.InsertAsync(c, p) : await C.InsertAsync(c, p);
    }

    [Benchmark(Description = "Update")]
    public async Task<int> Update()
    {
        using var c = World.Open();
        var i = Next();
        var p = World.NewPerson(i);
        p.Id = i % World.Rows + 1;
        return M ? await B.UpdateAsync(c, p) : await C.UpdateAsync(c, p);
    }

    [Benchmark(Description = "Delete(id) after Insert")]
    public async Task<int> InsertDelete()
    {
        using var c = World.Open();
        var p = World.NewPerson(Next());
        var id = (M ? await B.InsertAsync(c, p) : await C.InsertAsync(c, p))!.Value;
        return M ? await B.DeleteAsync<Person>(c, id) : await C.DeleteAsync<Person>(c, id);
    }
}
