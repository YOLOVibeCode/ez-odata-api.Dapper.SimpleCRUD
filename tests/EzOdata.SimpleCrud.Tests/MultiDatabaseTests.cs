using EzOdata.SimpleCrud.Testing;
using Xunit;
using Xunit.Abstractions;

namespace EzOdata.SimpleCrud.Tests;

/// <summary>
/// The point of the facade: real PostgreSQL, MySQL, SQL Server and SQLite driven through SimpleCRUD
/// at the same time, in one process, each on its own isolated engine.
/// </summary>
public sealed class MultiDatabaseTests(Databases databases, ITestOutputHelper output) : IClassFixture<Databases>
{
    [SkippableTheory]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("sqlserver")]
    public async Task Full_crud_through_the_facade(string kind)
    {
        var db = databases.Available.FirstOrDefault(d => d.Kind == kind);
        Skip.If(db is null, $"{kind} unavailable: {databases.Why(kind)}");
        await Scenario(db!);
    }

    [SkippableFact]
    public async Task Every_database_at_once_in_one_process()
    {
        var all = databases.Available.ToList();
        Skip.If(all.Count < 2, $"needs at least two Docker databases: {string.Join("; ", databases.Failures.Select(f => $"{f.Key}: {f.Value}"))}");

        var sqlite = TestDatabase.NewSqlite();
        all.Add(sqlite);
        var before = Dapper.SimpleCRUD.GetDialect();

        await Task.WhenAll(all.Select(async db =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Scenario(db);
            output.WriteLine($"{db.Kind}: {watch.ElapsedMilliseconds} ms");
        }));

        Assert.Equal(before, Dapper.SimpleCRUD.GetDialect());
        output.WriteLine("Ran concurrently: " + string.Join(", ", all.Select(d => d.Kind)));
        foreach (var failure in databases.Failures) output.WriteLine($"Not available: {failure.Key} ({failure.Value})");
    }

    private async Task Scenario(TestDatabase db)
    {
        await db.ExecuteAsync(Ddl.Customers(db.Kind));
        var crud = SimpleCrud.For(db.Dialect).WithConnection(db.Connect).Build();
        Assert.True(crud.Engine.IsIsolated);
        Assert.Equal(db.Dialect.ToString(), crud.Engine.EffectiveDialect);

        // 20 concurrent inserts, each on its own connection
        var ids = await Task.WhenAll(Enumerable.Range(1, 20).Select(i =>
            crud.InsertAsync(new Customer { Name = $"{db.Kind}-{i:D2}", Country = i % 2 == 0 ? "US" : "EU", Code = $"C{i}" })));
        Assert.Equal(20, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 0));

        var first = (await crud.GetAsync<Customer>(ids[0]!.Value))!;
        Assert.Equal($"{db.Kind}-01", first.Name);
        Assert.Equal("db", first.Computed);

        first.Name = "renamed";
        first.Code = "ignored";
        Assert.Equal(1, await crud.UpdateAsync(first));
        var reloaded = (await crud.GetAsync<Customer>(first.Id))!;
        Assert.Equal("renamed", reloaded.Name);
        Assert.Equal("C1", reloaded.Code);

        var page = (await crud.GetListPagedAsync<Customer>(2, 5, "where full_name like @p", "full_name", new { p = db.Kind + "-%" })).ToList();
        Assert.Equal(5, page.Count);
        Assert.Equal(10, await crud.RecordCountAsync<Customer>(new { Country = "US" }));

        await using (var session = await crud.OpenSessionAsync(beginTransaction: true))
        {
            await session.DeleteListAsync<Customer>(new { Country = "EU" });
            session.Rollback();
        }

        Assert.Equal(20, await crud.RecordCountAsync<Customer>());
        Assert.Equal(10, await crud.DeleteListAsync<Customer>(new { Country = "EU" }));
        var us = (await crud.GetAsync<Customer>(ids[1]!.Value))!;
        Assert.Equal(1, await crud.DeleteAsync(us));
        Assert.Equal(9, await crud.RecordCountAsync<Customer>());
    }
}
