using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Policy;
using EzOdata.Entities.AspNetCore;
using EzOdata.SimpleCrud;
using EzOdata.SimpleCrud.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace EzOdata.Entities.AspNetCore.Tests;

public abstract class MultiDialectSuite(Databases databases, ITestOutputHelper output) : IClassFixture<Databases>
{
    protected abstract IEngineUnderTest Engine { get; }

    [SkippableFact]
    public async Task Every_dialect_behind_one_api_in_one_process()
    {
        var dbs = databases.Available.Where(Include).ToList();
        Skip.If(dbs.Count < 2, $"needs at least two Docker databases: {string.Join("; ", databases.Failures.Select(f => $"{f.Key}: {f.Value}"))}");
        dbs.Add(TestDatabase.NewSqlite());

        foreach (var db in dbs) await db.ExecuteAsync(Schema.Crm(db.Kind) + Schema.Seed);
        var processWide = SimpleCRUD.GetDialect();

        using var host = await TestApp.StartAsync(
            ez =>
            {
                foreach (var db in dbs)
                {
                    ez.AddService(db.Kind, s => _ = db.Kind switch
                    {
                        "postgresql" => s.UsePostgreSql(Spec(db, "disable")),
                        "mysql" => s.UseMySql(Spec(db, "prefer")),
                        "sqlserver" => s.UseSqlServer(Spec(db, "require") with { Tls = new TlsSpec { Mode = "require", AllowInvalid = true } }),
                        _ => s.UseSqlite(db.FilePath!),
                    });
                }

                ez.AddRole("admin", r => r.Allow(null, "*", Verb.All));
                ez.UseHostRoles();
            },
            services =>
            {
                services.AddSingleton<HookJournal>();
                services.ExtendEzOData(x =>
                {
                    foreach (var db in dbs)
                    {
                        x.Service(db.Kind, s =>
                        {
                            Engine.Configure(s);
                            s.Table<Customer, CustomerHandler>().Table<Order>();
                        });
                    }
                });
            });

        var client = host.As("root", "admin");
        await Task.WhenAll(dbs.Select(async db =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var root = $"/api/odata/{db.Kind}";

            var created = await Task.WhenAll(Enumerable.Range(1, 10).Select(async i =>
            {
                var response = await client.PostAsJsonAsync($"{root}/customers",
                    new { full_name = $"{db.Kind}-{i}", email = $"{db.Kind}-{i}@example.com", country = "us" });
                Assert.True(response.StatusCode == HttpStatusCode.Created, $"{db.Kind}: POST {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            }));
            Assert.All(created, c => Assert.Equal("root", c.GetProperty("created_by").GetString()));

            var ids = created.Select(c => c.GetProperty("id").GetInt32()).ToList();
            var patch = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"{root}/customers({ids[0]})")
                { Content = JsonContent.Create(new { email = $"patched@{db.Kind}.example" }) });
            Assert.True(patch.IsSuccessStatusCode, $"{db.Kind}: PATCH {(int)patch.StatusCode} {await patch.Content.ReadAsStringAsync()}");

            var deletes = await Task.WhenAll(ids.Skip(1).Take(3).Select(id => client.DeleteAsync($"{root}/customers({id})")));
            Assert.All(deletes, d => Assert.Equal(HttpStatusCode.NoContent, d.StatusCode));

            var page = JsonDocument.Parse(await client.GetStringAsync($"{root}/customers?$count=true&$expand=orders")).RootElement;
            Assert.Equal(2 + 10 - 3, page.GetProperty("@odata.count").GetInt32());

            await using var connection = db.Connect();
            var breakdown = await connection.QueryAsync<(string Action, int N)>("SELECT action, COUNT(*) FROM audit_log GROUP BY action");
            output.WriteLine($"{Engine.Name}/{db.Kind}: audit {string.Join(", ", breakdown.Select(b => $"{b.Action}={b.N}"))}; " +
                $"customers={await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM customers")}; {watch.ElapsedMilliseconds} ms");
            Assert.Equal(12, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM customers"));
            Assert.Equal(13, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_log"));
            Assert.Equal($"patched@{db.Kind}.example",
                await connection.ExecuteScalarAsync<string>($"SELECT email FROM customers WHERE id = {ids[0]}"));
        }));

        Assert.Equal(processWide, SimpleCRUD.GetDialect());
        AssertEngineSpecific(dbs);
        foreach (var failure in databases.Failures) output.WriteLine($"Not available: {failure.Key} ({failure.Value})");
    }

    protected virtual bool Include(TestDatabase db) => true;

    protected virtual void AssertEngineSpecific(IReadOnlyList<TestDatabase> dbs) { }

    private static ConnectionSpec Spec(TestDatabase db, string tls) => new()
    {
        Host = db.Host,
        Port = db.Port,
        Database = db.Database,
        Username = db.Username,
        Password = db.Password,
        Tls = new TlsSpec { Mode = tls },
    };
}

public sealed class SimpleCrud_MultiDialectTests(Databases databases, ITestOutputHelper output)
    : MultiDialectSuite(databases, output)
{
    protected override IEngineUnderTest Engine => SimpleCrudEngineUnderTest.Instance;

    protected override void AssertEngineSpecific(IReadOnlyList<TestDatabase> dbs)
    {
        var engines = SimpleCrudEngines.All.Where(e => e.IsIsolated && e.Naming is null).Select(e => e.Dialect).ToHashSet();
        foreach (var db in dbs) Assert.Contains(db.Dialect, engines);
    }
}

public sealed class EfCore_MultiDialectTests(Databases databases, ITestOutputHelper output)
    : MultiDialectSuite(databases, output)
{
    protected override IEngineUnderTest Engine => EfCoreEngineUnderTest.Instance;

    // ez-odata shares a MySqlConnector connection; no EF Core 10 provider accepts that type.
    protected override bool Include(TestDatabase db) => db.Kind != "mysql";
}
