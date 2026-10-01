using System.Diagnostics;
using System.Runtime.Loader;
using Dapper;
using EzOdata.SimpleCrud.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace EzOdata.SimpleCrud.Tests;

/// <summary>Engine mechanics, all on SQLite (no Docker): isolation, laziness, parity, naming, errors, sessions.</summary>
public sealed class EngineTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly TestDatabase _db = TestDatabase.NewSqlite();

    public Task InitializeAsync() => _db.ExecuteAsync(Ddl.Customers("sqlite") +
        "CREATE TABLE order_line (id INTEGER PRIMARY KEY AUTOINCREMENT, product_name TEXT NOT NULL, unit_price NUMERIC NOT NULL);" +
        "CREATE TABLE OrderLine (Id INTEGER PRIMARY KEY AUTOINCREMENT, ProductName TEXT NOT NULL, UnitPrice NUMERIC NOT NULL);");

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_db.FilePath!);
        return Task.CompletedTask;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_db.ConnectionString);
        connection.Open();
        return connection;
    }

    [Fact]
    public void Engines_are_lazy_singletons_per_dialect()
    {
        var sqlite = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLite);
        var postgres = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.PostgreSQL);

        Assert.Same(sqlite, SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLite));
        Assert.Same(sqlite, SimpleCrudEngines.For(SimpleCRUD.Dialect.SQLite));
        Assert.NotSame(sqlite, postgres);
        Assert.True(sqlite.IsIsolated);
        Assert.NotSame(typeof(SimpleCRUD), sqlite.CrudType);
        Assert.NotSame(sqlite.CrudType, postgres.CrudType);
        Assert.Equal("SQLite", sqlite.EffectiveDialect);
        Assert.Equal("PostgreSQL", postgres.EffectiveDialect);
    }

    [Fact]
    public async Task First_use_under_contention_creates_exactly_one_engine()
    {
        var naming = new SimpleCrudNaming("race-" + Guid.NewGuid().ToString("N"));
        using var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.MySQL, naming);
        })).ToList();

        gate.Set();
        var engines = await Task.WhenAll(tasks);

        Assert.All(engines, e => Assert.Same(engines[0], e));
        Assert.Single(AssemblyLoadContext.All, c => c.Name?.Contains(naming.Name, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Isolated_engines_never_touch_the_process_wide_SimpleCRUD()
    {
        var before = SimpleCRUD.GetDialect();
        using var connection = Open();
        connection.Execute("INSERT INTO customers (full_name, Country) VALUES ('Ada', 'US')");

        // Use every dialect's engine first (each caches its own quoted names)...
        foreach (var dialect in new[] { SimpleCRUD.Dialect.PostgreSQL, SimpleCRUD.Dialect.MySQL, SimpleCRUD.Dialect.SQLite })
        {
            Assert.Equal("Ada", SimpleCrudEngines.Isolated(dialect).Get<Customer>(connection, 1)!.Name);
        }

        // ...then the host's own SimpleCRUD call: still its default dialect and quoting.
        var recording = new RecordingConnection(connection);
        Assert.Equal("Ada", recording.Get<Customer>(1).Name);
        Assert.Equal(before, SimpleCRUD.GetDialect());
        Assert.Contains("from [customers] where [Id] = @Id", recording.Sql.Single());
    }

    [Theory]
    [InlineData(SimpleCRUD.Dialect.SQLite, "\"customers\"", "\"Id\"", "LIMIT 10 OFFSET ((1-1) * 10)")]
    [InlineData(SimpleCRUD.Dialect.PostgreSQL, "\"customers\"", "\"Id\"", "LIMIT 10 OFFSET ((1-1) * 10)")]
    [InlineData(SimpleCRUD.Dialect.MySQL, "`customers`", "`Id`", "LIMIT 0,10")]
    [InlineData(SimpleCRUD.Dialect.SQLServer, "[customers]", "[Id]", "ROW_NUMBER() OVER(ORDER BY full_name)")]
    public void Each_engine_emits_its_own_dialect_in_one_process(SimpleCRUD.Dialect dialect, string table, string id, string paging)
    {
        // SQLite accepts all four quoting styles and paging forms, so every dialect's real SQL runs here.
        var engine = SimpleCrudEngines.Isolated(dialect);
        using var connection = Open();
        connection.Execute("INSERT INTO customers (full_name, Country, Code) VALUES ('Ada', 'US', 'A1'), ('Bob', 'US', 'B1')");
        var recording = new RecordingConnection(connection);

        var ada = engine.Get<Customer>(recording, 1)!;
        var page = engine.GetListPaged<Customer>(recording, 1, 10, "where full_name like @p", "full_name", new { p = "%" }).ToList();
        var count = engine.RecordCount<Customer>(recording, new { Country = "US" });
        ada.Name = "Ada L.";
        ada.Code = "IGNORED";
        engine.Update(recording, ada);
        var deleted = engine.Delete<Customer>(recording, 2);

        output.WriteLine(string.Join(Environment.NewLine, recording.Sql));
        Assert.Equal(2, page.Count);
        Assert.Equal(2, count);
        Assert.Equal(1, deleted);
        Assert.Contains($"from {table} where {id} = @Id", recording.Sql[0]);
        Assert.Contains(paging, recording.Sql[1]);
        Assert.StartsWith($"update {table} set", recording.Sql[3]);

        var reloaded = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLite).Get<Customer>(connection, 1)!;
        Assert.Equal("Ada L.", reloaded.Name);
        Assert.Equal("A1", reloaded.Code); // [IgnoreUpdate] honored
        Assert.Equal("db", reloaded.Computed); // [ReadOnly] never written, DB default kept
    }

    [Fact]
    public void Why_isolation_is_needed_switching_SimpleCRUDs_dialect_leaves_stale_sql()
    {
        // Upstream behavior (SimpleCRUD 2.3.0), reproduced in a private copy so the process-wide one is
        // untouched: SetDialect does not clear SimpleCRUD's name caches, so after switching, statements mix
        // the first dialect's quoting with the new dialect's paging.
        var crud = new SimpleCrudLoadContext("stale-cache-repro").LoadSimpleCrud().GetType("Dapper.SimpleCRUD", throwOnError: true)!;
        var dialect = crud.GetNestedType("Dialect")!;
        void SetDialect(string name) => crud.GetMethod("SetDialect")!.Invoke(null, [Enum.Parse(dialect, name)]);
        var engine = new SimpleCrudEngine(crud, SimpleCRUD.Dialect.PostgreSQL, isolated: true, "repro", naming: null);

        using var connection = Open();
        var recording = new RecordingConnection(connection);
        SetDialect("SQLServer");
        engine.GetList<Customer>(recording, new { Country = "US" });
        SetDialect("PostgreSQL");
        engine.GetListPaged<Customer>(recording, 1, 10, "", "full_name");

        Assert.Equal("PostgreSQL", engine.EffectiveDialect);
        Assert.Contains("from [customers]", recording.Sql[1]);             // SQL Server quoting, still cached...
        Assert.Contains("LIMIT 10 OFFSET ((1-1) * 10)", recording.Sql[1]); // ...inside PostgreSQL paging
    }

    [Fact]
    public void Describe_reads_the_mapping_from_SimpleCRUD_itself()
    {
        var info = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLServer).Describe<Customer>();
        var p = info.Properties.ToDictionary(x => x.Property.Name);

        Assert.Equal("[customers]", info.QuotedTableName);
        Assert.Equal("customers", info.TableName);
        Assert.Equal("Id", Assert.Single(info.Keys).Property.Name);
        Assert.Equal("full_name", p["Name"].ColumnName);
        Assert.Equal("[full_name]", p["Name"].QuotedColumnName);
        Assert.False(p["Id"].IsInsertable);      // identity key
        Assert.True(p["Name"].IsInsertable && p["Name"].IsUpdatable);
        Assert.True(p["Code"].IsInsertable);
        Assert.False(p["Code"].IsUpdatable);     // [IgnoreUpdate]
        Assert.False(p["Computed"].IsInsertable || p["Computed"].IsUpdatable); // [ReadOnly(true)]
        Assert.False(p.ContainsKey("Temp"));     // [NotMapped]
        Assert.False(p.ContainsKey("Hidden"));   // [Editable(false)]
    }

    [Fact]
    public async Task Naming_conventions_are_per_engine()
    {
        var snake = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLite, SimpleCrudNaming.SnakeCase);
        var plain = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLite);
        await using var connection = Open();

        await snake.InsertAsync(connection, new OrderLine { ProductName = "Widget", UnitPrice = 9.5m });
        await plain.InsertAsync(connection, new OrderLine { ProductName = "Gadget", UnitPrice = 3m });

        Assert.Equal("Widget", await connection.ExecuteScalarAsync<string>("SELECT product_name FROM order_line"));
        Assert.Equal("Gadget", await connection.ExecuteScalarAsync<string>("SELECT ProductName FROM OrderLine"));
        Assert.Equal("\"order_line\"", snake.Describe<OrderLine>().QuotedTableName);
        Assert.Equal("unit_price", snake.Describe<OrderLine>().Properties.Single(x => x.Property.Name == "UnitPrice").ColumnName);
        Assert.Equal("\"OrderLine\"", plain.Describe<OrderLine>().QuotedTableName);
    }

    [Fact]
    public async Task Several_custom_naming_engines_coexist()
    {
        // Regression: DispatchProxy shares one dynamic assembly across proxies, which broke every named
        // engine after the first. Each engine now emits its resolvers into its own load context.
        var a = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLite, new SimpleCrudNaming("a-" + Guid.NewGuid().ToString("N"), t => "a_" + t.Name.ToLowerInvariant()));
        var b = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLite, new SimpleCrudNaming("b-" + Guid.NewGuid().ToString("N"), t => "b_" + t.Name.ToLowerInvariant()));
        var c = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.MySQL, new SimpleCrudNaming("c-" + Guid.NewGuid().ToString("N"), t => "c_" + t.Name.ToLowerInvariant()));
        await using var connection = Open();
        await connection.ExecuteAsync(
            "CREATE TABLE a_orderline (Id INTEGER PRIMARY KEY AUTOINCREMENT, ProductName TEXT, UnitPrice NUMERIC);" +
            "CREATE TABLE b_orderline (Id INTEGER PRIMARY KEY AUTOINCREMENT, ProductName TEXT, UnitPrice NUMERIC);");

        await a.InsertAsync(connection, new OrderLine { ProductName = "from-a", UnitPrice = 1 });
        await b.InsertAsync(connection, new OrderLine { ProductName = "from-b", UnitPrice = 2 });

        Assert.Equal("from-a", await connection.ExecuteScalarAsync<string>("SELECT ProductName FROM a_orderline"));
        Assert.Equal("from-b", await connection.ExecuteScalarAsync<string>("SELECT ProductName FROM b_orderline"));
        Assert.Equal("`c_orderline`", c.Describe<OrderLine>().QuotedTableName);
    }

    [Fact]
    public void An_incompatible_SimpleCRUD_is_rejected_with_a_clear_message()
    {
        var ex = Assert.Throws<NotSupportedException>(() => SimpleCrudEngine.VerifyMetadataMembers(typeof(object)));
        Assert.Contains("is not supported by EzOdata.SimpleCrud", ex.Message);
        Assert.Contains("GetTableName(Type)", ex.Message);

        SimpleCrudEngine.VerifyMetadataMembers(typeof(SimpleCRUD)); // the referenced version passes
    }

    [Fact]
    public async Task Exceptions_surface_unwrapped()
    {
        var engine = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLite);
        await using var connection = Open();
        await connection.ExecuteAsync("CREATE UNIQUE INDEX ux_name ON customers(full_name)");
        await engine.InsertAsync(connection, new Customer { Name = "Dup", Country = "US" });

        var ex = await Assert.ThrowsAsync<SqliteException>(() => engine.InsertAsync(connection, new Customer { Name = "Dup", Country = "US" }));
        Assert.Equal(19, ex.SqliteErrorCode);
    }

    [Fact]
    public void Delegates_are_compiled_once_per_operation_and_type()
    {
        var engine = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLite, new SimpleCrudNaming("delegates-" + Guid.NewGuid().ToString("N")));
        using var connection = Open();

        engine.Get<Customer>(connection, 1);
        var afterFirst = engine.CompiledDelegateCount;
        for (var i = 0; i < 50; i++) engine.Get<Customer>(connection, 1);

        Assert.Equal(1, afterFirst);
        Assert.Equal(1, engine.CompiledDelegateCount);
    }

    [Fact]
    public async Task Sessions_commit_and_roll_back()
    {
        var crud = SimpleCrud.For(SimpleCRUD.Dialect.SQLite).WithConnection(() => new SqliteConnection(_db.ConnectionString)).Build();

        await using (var session = await crud.OpenSessionAsync(beginTransaction: true))
        {
            await session.InsertAsync(new Customer { Name = "Rolled back", Country = "US" });
            Assert.Equal(1, await session.RecordCountAsync<Customer>());
            session.Rollback();
        }

        await using (var session = await crud.OpenSessionAsync(beginTransaction: true))
        {
            var id = await session.InsertAsync(new Customer { Name = "Kept", Country = "US" });
            session.Commit();
            Assert.NotNull(id);
        }

        var all = (await crud.GetListAsync<Customer>()).ToList();
        Assert.Equal("Kept", Assert.Single(all).Name);
        Assert.Equal(1, await crud.DeleteAsync<Customer>(all[0].Id));
    }

    [Fact]
    public async Task Keyed_DI_registrations_resolve_independent_dialects()
    {
        var provider = new ServiceCollection()
            .AddSimpleCrud("local", SimpleCRUD.Dialect.SQLite, _ => new SqliteConnection(_db.ConnectionString))
            .AddSimpleCrud("warehouse", SimpleCRUD.Dialect.PostgreSQL, _ => throw new InvalidOperationException("not used"))
            .BuildServiceProvider();

        var local = provider.GetRequiredKeyedService<ISimpleCrud>("local");
        var warehouse = provider.GetRequiredKeyedService<ISimpleCrud>("warehouse");

        Assert.Equal(SimpleCRUD.Dialect.SQLite, local.Engine.Dialect);
        Assert.Equal(SimpleCRUD.Dialect.PostgreSQL, warehouse.Engine.Dialect);
        Assert.NotNull(await local.InsertAsync(new Customer { Name = "DI", Country = "US" }));
        Assert.Equal(1, await local.RecordCountAsync<Customer>(new { Country = "US" }));
    }

    [Fact]
    public void Shared_engine_never_changes_global_state_implicitly()
    {
        var shared = SimpleCrudEngines.Shared(SimpleCRUD.Dialect.SQLServer); // matches the default: no change
        Assert.False(shared.IsIsolated);
        Assert.Same(typeof(SimpleCRUD), shared.CrudType);

        var ex = Assert.Throws<InvalidOperationException>(() => SimpleCrudEngines.Shared(SimpleCRUD.Dialect.SQLite));
        Assert.Contains("Isolated", ex.Message);
        Assert.Equal("SQLServer", SimpleCRUD.GetDialect());
    }

    [Fact]
    public void Overhead_versus_calling_SimpleCRUD_directly()
    {
        // Same dialect (the process default, SQL Server quoting — accepted by SQLite) → identical SQL; only the call path differs.
        var engine = SimpleCrudEngines.Isolated(SimpleCRUD.Dialect.SQLServer);
        using var connection = Open();
        connection.Execute("INSERT INTO customers (full_name, Country) VALUES ('Perf', 'US')");

        for (var i = 0; i < 500; i++) { connection.Get<Customer>(1); engine.Get<Customer>(connection, 1); }

        // Shared CI runners stall without warning. Interleaved rounds compared best-to-best keep one stall
        // from landing on only one side of the ratio.
        const int rounds = 10, n = 500;
        var direct = TimeSpan.MaxValue;
        var facade = TimeSpan.MaxValue;
        var clock = new Stopwatch();
        for (var r = 0; r < rounds; r++)
        {
            clock.Restart();
            for (var i = 0; i < n; i++) connection.Get<Customer>(1);
            if (clock.Elapsed < direct) direct = clock.Elapsed;

            clock.Restart();
            for (var i = 0; i < n; i++) engine.Get<Customer>(connection, 1);
            if (clock.Elapsed < facade) facade = clock.Elapsed;
        }

        var ratio = facade.TotalMilliseconds / direct.TotalMilliseconds;
        output.WriteLine($"{rounds} x {n} Get<T>, best round: direct SimpleCRUD {direct.TotalMilliseconds:F1} ms, isolated engine {facade.TotalMilliseconds:F1} ms (x{ratio:F2})");
        Assert.True(ratio < 1.5, $"facade overhead too high: x{ratio:F2}");
    }
}
