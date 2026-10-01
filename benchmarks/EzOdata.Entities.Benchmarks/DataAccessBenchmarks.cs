using System.Data.Common;
using BenchmarkDotNet.Attributes;
using Dapper;
using EzOdata.SimpleCrud;
using EzOdata.SimpleCrud.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EzOdata.Entities.Benchmarks;

// The data-access layer, head to head: hand-written SQL through Dapper, Dapper.SimpleCRUD (through the
// EzOdata.SimpleCrud facade), and EF Core, on the same table and rows. No HTTP, no ez-odata.

[Table("bench_people")]
public class Person
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("full_name")] public string Name { get; set; } = "";
    [Column("email")] public string Email { get; set; } = "";
    [Column("country")] public string Country { get; set; } = "";
    [Column("score")] public int Score { get; set; }
}

public sealed class PeopleContext(DbContextOptions<PeopleContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();

    protected override void OnModelCreating(ModelBuilder model) => model.Entity<Person>(e =>
    {
        e.ToTable("bench_people").HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id");
        e.Property(x => x.Name).HasColumnName("full_name");
        e.Property(x => x.Email).HasColumnName("email");
        e.Property(x => x.Country).HasColumnName("country");
        e.Property(x => x.Score).HasColumnName("score");
    });
}

/// <summary>One library's way of doing each operation, the way its documentation recommends.</summary>
public abstract class PeopleLibrary(TestDatabase db)
{
    public const int SeedRows = 5_000;
    protected TestDatabase Db { get; } = db;
    public abstract string Name { get; }

    public abstract Task<int> InsertAsync(Person p);
    public abstract Task<Person?> GetAsync(int id);
    public abstract Task<List<Person>> PageAsync(string country);
    public abstract Task UpdateAsync(Person p);
    public abstract Task DeleteAsync(int id);
    public abstract Task InsertBatchAsync(IReadOnlyList<Person> people);

    protected async Task<DbConnection> OpenAsync()
    {
        var connection = Db.Connect();
        await connection.OpenAsync();
        return connection;
    }

    public static PeopleLibrary Create(string library, TestDatabase db) => library switch
    {
        "Dapper" => new DapperPeople(db),
        "SimpleCRUD" => new SimpleCrudPeople(db),
        "SimpleCRUDStatic" => new StaticSimpleCrudPeople(db),
        "EFCore" => new EfPeople(db, tracking: true),
        "EFCoreNoTracking" => new EfPeople(db, tracking: false),
        _ => throw new ArgumentOutOfRangeException(nameof(library), library, null),
    };

    public static string Ddl(string kind) => kind switch
    {
        "sqlite" => "DROP TABLE IF EXISTS bench_people; CREATE TABLE bench_people (id INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, email TEXT NOT NULL, country TEXT NOT NULL, score INTEGER NOT NULL); CREATE INDEX ix_bench_people_country ON bench_people(country);",
        "postgresql" => "DROP TABLE IF EXISTS bench_people; CREATE TABLE bench_people (id SERIAL PRIMARY KEY, full_name VARCHAR(100) NOT NULL, email VARCHAR(200) NOT NULL, country VARCHAR(8) NOT NULL, score INT NOT NULL); CREATE INDEX ix_bench_people_country ON bench_people(country);",
        "mysql" => "DROP TABLE IF EXISTS bench_people; CREATE TABLE bench_people (id INT AUTO_INCREMENT PRIMARY KEY, full_name VARCHAR(100) NOT NULL, email VARCHAR(200) NOT NULL, country VARCHAR(8) NOT NULL, score INT NOT NULL, INDEX ix_bench_people_country (country));",
        "sqlserver" => "IF OBJECT_ID('bench_people') IS NOT NULL DROP TABLE bench_people; CREATE TABLE bench_people (id INT IDENTITY PRIMARY KEY, full_name NVARCHAR(100) NOT NULL, email NVARCHAR(200) NOT NULL, country NVARCHAR(8) NOT NULL, score INT NOT NULL); CREATE INDEX ix_bench_people_country ON bench_people(country);",
        _ => throw new NotSupportedException(kind),
    };

    public static readonly string[] Countries = ["US", "CA", "GB", "DE", "FR", "JP", "BR", "IN"];

    public static Person NewPerson(int i) => new()
    {
        Name = $"Person {i}", Email = $"person{i}@example.com", Country = Countries[i % Countries.Length], Score = i % 1000,
    };

    /// <summary>Fresh table with <see cref="SeedRows"/> rows, identical for every library.</summary>
    public static async Task ResetAsync(TestDatabase db)
    {
        await using var c = db.Connect();
        await c.OpenAsync();
        await c.ExecuteAsync(Ddl(db.Kind));
        if (db.Kind == "sqlite") await c.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await using var tx = await c.BeginTransactionAsync();
        foreach (var chunk in Enumerable.Range(1, SeedRows).Chunk(500))
        {
            var values = string.Join(",", chunk.Select(i => $"('Person {i}','person{i}@example.com','{Countries[i % Countries.Length]}',{i % 1000})"));
            await c.ExecuteAsync($"INSERT INTO bench_people (full_name, email, country, score) VALUES {values}", transaction: tx);
        }

        await tx.CommitAsync();
    }
}

internal sealed class DapperPeople(TestDatabase db) : PeopleLibrary(db)
{
    private const string Columns = "id AS Id, full_name AS Name, email AS Email, country AS Country, score AS Score";
    public override string Name => "Dapper";

    private string InsertSql => Db.Kind switch
    {
        "postgresql" => "INSERT INTO bench_people (full_name, email, country, score) VALUES (@Name, @Email, @Country, @Score) RETURNING id",
        "mysql" => "INSERT INTO bench_people (full_name, email, country, score) VALUES (@Name, @Email, @Country, @Score); SELECT LAST_INSERT_ID();",
        "sqlserver" => "INSERT INTO bench_people (full_name, email, country, score) OUTPUT INSERTED.id VALUES (@Name, @Email, @Country, @Score)",
        _ => "INSERT INTO bench_people (full_name, email, country, score) VALUES (@Name, @Email, @Country, @Score); SELECT last_insert_rowid();",
    };

    private string PageSql => Db.Kind == "sqlserver"
        ? $"SELECT {Columns} FROM bench_people WHERE country = @country ORDER BY id OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY"
        : $"SELECT {Columns} FROM bench_people WHERE country = @country ORDER BY id LIMIT 20";

    public override async Task<int> InsertAsync(Person p) { await using var c = await OpenAsync(); return await c.ExecuteScalarAsync<int>(InsertSql, p); }
    public override async Task<Person?> GetAsync(int id) { await using var c = await OpenAsync(); return await c.QuerySingleOrDefaultAsync<Person>($"SELECT {Columns} FROM bench_people WHERE id = @id", new { id }); }
    public override async Task<List<Person>> PageAsync(string country) { await using var c = await OpenAsync(); return (await c.QueryAsync<Person>(PageSql, new { country })).AsList(); }
    public override async Task UpdateAsync(Person p) { await using var c = await OpenAsync(); await c.ExecuteAsync("UPDATE bench_people SET full_name = @Name, email = @Email, country = @Country, score = @Score WHERE id = @Id", p); }
    public override async Task DeleteAsync(int id) { await using var c = await OpenAsync(); await c.ExecuteAsync("DELETE FROM bench_people WHERE id = @id", new { id }); }

    public override async Task InsertBatchAsync(IReadOnlyList<Person> people)
    {
        await using var c = await OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("INSERT INTO bench_people (full_name, email, country, score) VALUES (@Name, @Email, @Country, @Score)", people, tx);
        await tx.CommitAsync();
    }
}

internal sealed class SimpleCrudPeople(TestDatabase db) : PeopleLibrary(db)
{
    private readonly SimpleCrudEngine _crud = SimpleCrudEngines.For(db.Dialect);
    public override string Name => "SimpleCRUD";

    public override async Task<int> InsertAsync(Person p) { await using var c = await OpenAsync(); return (await _crud.InsertAsync(c, p))!.Value; }
    public override async Task<Person?> GetAsync(int id) { await using var c = await OpenAsync(); return await _crud.GetAsync<Person>(c, id); }
    public override async Task<List<Person>> PageAsync(string country) { await using var c = await OpenAsync(); return (await _crud.GetListPagedAsync<Person>(c, 1, 20, "where country = @country", "id", new { country })).AsList(); }
    public override async Task UpdateAsync(Person p) { await using var c = await OpenAsync(); await _crud.UpdateAsync(c, p); }
    public override async Task DeleteAsync(int id) { await using var c = await OpenAsync(); await _crud.DeleteAsync<Person>(c, id); }

    public override async Task InsertBatchAsync(IReadOnlyList<Person> people)
    {
        await using var c = await OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        foreach (var p in people) await _crud.InsertAsync(c, p, tx);
        await tx.CommitAsync();
    }
}

/// <summary>
/// Dapper.SimpleCRUD called directly (its static extension methods, process-wide dialect), to show what the
/// EzOdata.SimpleCrud facade costs on top. SQLite only: the static dialect is global to the process.
/// </summary>
internal sealed class StaticSimpleCrudPeople : PeopleLibrary
{
    public StaticSimpleCrudPeople(TestDatabase db) : base(db) => SimpleCRUD.SetDialect(db.Dialect);
    public override string Name => "SimpleCRUDStatic";

    public override async Task<int> InsertAsync(Person p) { await using var c = await OpenAsync(); return (await c.InsertAsync(p))!.Value; }
    public override async Task<Person?> GetAsync(int id) { await using var c = await OpenAsync(); return await c.GetAsync<Person>(id); }
    public override async Task<List<Person>> PageAsync(string country) { await using var c = await OpenAsync(); return (await c.GetListPagedAsync<Person>(1, 20, "where country = @country", "id", new { country })).AsList(); }
    public override async Task UpdateAsync(Person p) { await using var c = await OpenAsync(); await c.UpdateAsync(p); }
    public override async Task DeleteAsync(int id) { await using var c = await OpenAsync(); await c.DeleteAsync<Person>(id); }

    public override async Task InsertBatchAsync(IReadOnlyList<Person> people)
    {
        await using var c = await OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        foreach (var p in people) await c.InsertAsync(p, tx);
        await tx.CommitAsync();
    }
}

/// <summary>EF Core with DbContext pooling (the documented high-throughput setup); a context per operation.</summary>
internal sealed class EfPeople : PeopleLibrary
{
    private readonly PooledDbContextFactory<PeopleContext> _factory;
    private readonly bool _tracking;

    public EfPeople(TestDatabase db, bool tracking) : base(db)
    {
        _tracking = tracking;
        var options = new DbContextOptionsBuilder<PeopleContext>();
        switch (db.Kind)
        {
            case "postgresql": options.UseNpgsql(db.ConnectionString); break;
            case "sqlserver": options.UseSqlServer(db.ConnectionString); break;
            case "sqlite": options.UseSqlite(db.ConnectionString); break;
            default: throw new NotSupportedException($"EF Core has no MySqlConnector-compatible provider for EF Core 10 ({db.Kind}).");
        }

        _factory = new PooledDbContextFactory<PeopleContext>(options.Options);
    }

    public override string Name => _tracking ? "EFCore" : "EFCoreNoTracking";

    private IQueryable<Person> People(PeopleContext ctx) => _tracking ? ctx.People : ctx.People.AsNoTracking();

    public override async Task<int> InsertAsync(Person p) { await using var ctx = _factory.CreateDbContext(); ctx.People.Add(p); await ctx.SaveChangesAsync(); return p.Id; }
    public override async Task<Person?> GetAsync(int id) { await using var ctx = _factory.CreateDbContext(); return await People(ctx).FirstOrDefaultAsync(x => x.Id == id); }
    public override async Task<List<Person>> PageAsync(string country) { await using var ctx = _factory.CreateDbContext(); return await People(ctx).Where(x => x.Country == country).OrderBy(x => x.Id).Take(20).ToListAsync(); }
    public override async Task UpdateAsync(Person p) { await using var ctx = _factory.CreateDbContext(); ctx.People.Update(p); await ctx.SaveChangesAsync(); }
    public override async Task DeleteAsync(int id) { await using var ctx = _factory.CreateDbContext(); ctx.People.Remove(new Person { Id = id }); await ctx.SaveChangesAsync(); }
    public override async Task InsertBatchAsync(IReadOnlyList<Person> people) { await using var ctx = _factory.CreateDbContext(); ctx.People.AddRange(people); await ctx.SaveChangesAsync(); }
}

/// <summary>Shared setup for the data-access benchmarks: one fresh, seeded table per (library, database) case.</summary>
public abstract class DataAccessBench
{
    public static IEnumerable<string> Databases => BenchWorld.DatabaseKinds;

    [ParamsSource(nameof(Databases))]
    public string Database { get; set; } = "sqlite";

    protected PeopleLibrary Lib = null!;
    private int _i;

    protected abstract string LibraryName { get; }

    [GlobalSetup]
    public void Setup()
    {
        var db = BenchWorld.RequireDatabase(Database);
        PeopleLibrary.ResetAsync(db).GetAwaiter().GetResult();
        Lib = PeopleLibrary.Create(LibraryName, db);
        _i = PeopleLibrary.SeedRows;
    }

    protected int Next() => Interlocked.Increment(ref _i);
}

[MemoryDiagnoser]
public class DataAccessReads : DataAccessBench
{
    public static IEnumerable<string> Libraries => ["Dapper", "SimpleCRUD", "SimpleCRUDStatic", "EFCore", "EFCoreNoTracking"];

    [ParamsSource(nameof(Libraries))]
    public string Library { get; set; } = "Dapper";

    protected override string LibraryName => Library;

    [Benchmark(Description = "Get by id")]
    public async Task<Person?> GetById() => await Lib.GetAsync(Next() % PeopleLibrary.SeedRows + 1)
        ?? throw new InvalidOperationException("row not found");

    [Benchmark(Description = "Filtered page (20 rows)")]
    public async Task<int> Page()
    {
        var rows = await Lib.PageAsync(PeopleLibrary.Countries[Next() % PeopleLibrary.Countries.Length]);
        return rows.Count == 20 ? rows.Count : throw new InvalidOperationException($"expected 20 rows, got {rows.Count}");
    }
}

[MemoryDiagnoser]
public class DataAccessWrites : DataAccessBench
{
    public static IEnumerable<string> Libraries => ["Dapper", "SimpleCRUD", "SimpleCRUDStatic", "EFCore"];

    [ParamsSource(nameof(Libraries))]
    public string Library { get; set; } = "Dapper";

    protected override string LibraryName => Library;

    [Benchmark(Description = "Insert one")]
    public async Task<int> Insert() => await Lib.InsertAsync(PeopleLibrary.NewPerson(Next())) is > 0 and var id ? id : throw new InvalidOperationException("no id");

    [Benchmark(Description = "Update one")]
    public Task Update()
    {
        var i = Next();
        var p = PeopleLibrary.NewPerson(i);
        p.Id = i % PeopleLibrary.SeedRows + 1;
        return Lib.UpdateAsync(p);
    }

    [Benchmark(Description = "Insert + delete")]
    public async Task InsertThenDelete() => await Lib.DeleteAsync(await Lib.InsertAsync(PeopleLibrary.NewPerson(Next())));

    [Benchmark(Description = "Insert 100 (one transaction)")]
    public Task InsertBatch()
    {
        var start = Next() * 1000;
        return Lib.InsertBatchAsync(Enumerable.Range(start, 100).Select(PeopleLibrary.NewPerson).ToList());
    }
}

/// <summary>Before timing anything: every library must read and write the same data the same way.</summary>
public static class DataAccessVerification
{
    public static async Task<List<string>> RunAsync(IEnumerable<TestDatabase> databases)
    {
        var problems = new List<string>();
        foreach (var db in databases.Select(d => BenchWorld.RequireDatabase(d.Kind)))
        {
            await PeopleLibrary.ResetAsync(db);
            var libs = new[] { "Dapper", "SimpleCRUD", "SimpleCRUDStatic", "EFCore", "EFCoreNoTracking" }
                .Where(l => !(l.StartsWith("EFCore", StringComparison.Ordinal) && db.Kind == "mysql"))
                .Where(l => l != "SimpleCRUDStatic" || db.Kind == "sqlite")
                .Select(l => PeopleLibrary.Create(l, db)).ToList();

            var reference = System.Text.Json.JsonSerializer.Serialize(await libs[0].PageAsync("DE"));
            foreach (var lib in libs)
            {
                var page = System.Text.Json.JsonSerializer.Serialize(await lib.PageAsync("DE"));
                if (page != reference) problems.Add($"{db.Kind}/{lib.Name}: filtered page differs from Dapper's");

                var id = await lib.InsertAsync(PeopleLibrary.NewPerson(900_000));
                var read = await lib.GetAsync(id);
                if (read?.Email != "person900000@example.com") problems.Add($"{db.Kind}/{lib.Name}: insert/get round-trip failed");
                read!.Score = 4242;
                await lib.UpdateAsync(read);
                if ((await lib.GetAsync(id))?.Score != 4242) problems.Add($"{db.Kind}/{lib.Name}: update not persisted");
                await lib.DeleteAsync(id);
                if (await lib.GetAsync(id) is not null) problems.Add($"{db.Kind}/{lib.Name}: delete not persisted");
            }
        }

        return problems;
    }
}
