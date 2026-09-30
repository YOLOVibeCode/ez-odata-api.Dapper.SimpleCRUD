// Smoke test for the published packages: runs against the .nupkg files, not project references.
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;
using EzOdata.Entities.AspNetCore;
using EzOdata.SimpleCrud;
using EzOdata.SimpleCrud.AspNetCore;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;

var db = Path.Combine(Path.GetTempPath(), $"ezsc-smoke-{Guid.NewGuid():N}.db");
using (var c = new SqliteConnection($"Data Source={db}"))
{
    c.Execute("CREATE TABLE widgets (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, created_by TEXT)");
}

// 1. EzOdata.SimpleCrud: two isolated dialect engines, process-wide SimpleCRUD untouched.
var processWide = SimpleCRUD.GetDialect();
var sqlite = SimpleCrud.For(SimpleCRUD.Dialect.SQLite).WithConnection(() => new SqliteConnection($"Data Source={db}")).Build();
var mssqlQuoting = SimpleCrud.For(SimpleCRUD.Dialect.SQLServer).WithConnection(() => new SqliteConnection($"Data Source={db}")).Build();
var id = await sqlite.InsertAsync(new Widget { Name = "facade" });
Check((await mssqlQuoting.GetAsync<Widget>(id!.Value))?.Name == "facade", "facade: insert on one engine, read on another");
Check(sqlite.Engine.IsIsolated && sqlite.Engine != mssqlQuoting.Engine, "facade: one isolated engine per dialect");
Check(SimpleCRUD.GetDialect() == processWide, "facade: process-wide SimpleCRUD untouched");

// 2. EzOdata.SimpleCrud.AspNetCore on a real Kestrel server with ez-odata from nuget.org.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddEzOData(ez => { ez.AddService("shop", s => s.UseSqlite(db)); ez.AllowAnonymousInDevelopment(); });
builder.Services.ExtendEzOData(x => x.Service("shop", shop => shop.UseSimpleCrud().Table<Widget>(t => t
    .BeforeInsert((w, ctx) => { if (string.IsNullOrWhiteSpace(w.Name)) ctx.Reject("name is required"); w.CreatedBy = "smoke"; }))));
var app = builder.Build();
app.MapEzOData("/api/odata");
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

using var http = new HttpClient { BaseAddress = new Uri(address) };
var created = await http.PostAsJsonAsync("/api/odata/shop/widgets", new { name = "api" });
var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
Check(created.IsSuccessStatusCode && body.GetProperty("created_by").GetString() == "smoke", "extension: POST through SimpleCRUD + hook");
var rejected = await http.PostAsJsonAsync("/api/odata/shop/widgets", new { name = "" });
Check((int)rejected.StatusCode == 400, "extension: hook rejection is a 400");
var list = JsonDocument.Parse(await http.GetStringAsync("/api/odata/shop/widgets?$count=true")).RootElement;
Check(list.GetProperty("@odata.count").GetInt32() == 2, "extension: OData read");

await app.StopAsync();
SqliteConnection.ClearAllPools();
File.Delete(db);
Console.WriteLine($"SMOKE OK: EzOdata.SimpleCrud {typeof(SimpleCrud).Assembly.GetName().Version}, " +
                  $"EzOdata.AspNetCore {typeof(EzODataEndpoints).Assembly.GetName().Version}, Dapper.SimpleCRUD {typeof(SimpleCRUD).Assembly.GetName().Version}");

static void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) Environment.Exit(1);
}

[Table("widgets")]
public class Widget
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("created_by")] public string? CreatedBy { get; set; }
}
