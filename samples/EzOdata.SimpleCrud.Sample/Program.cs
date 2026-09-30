// Sample: two SQLite databases behind one API (shop + warehouse), with Swagger/OpenAPI
// on both the OData and REST prefixes (ez-odata-api 1.0.6 server URLs).
//
//   dotnet run --project samples/EzOdata.SimpleCrud.Sample
//   ./demo-swagger.sh          # look up Swagger, read both DBs, time both
//   curl http://localhost:5199/api/odata/shop/products
//   curl http://localhost:5199/api/odata/warehouse/products
//   curl http://localhost:5199/api/odata/shop/openapi.json
//   curl http://localhost:5199/api/rest/shop/openapi.json

using Dapper;
using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;
using EzOdata.Core.Query;
using EzOdata.Entities.AspNetCore;
using EzOdata.SimpleCrud;
using EzOdata.SimpleCrud.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
    Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

var shopDb = Path.Combine(AppContext.BaseDirectory, "shop.db");
var warehouseDb = Path.Combine(AppContext.BaseDirectory, "warehouse.db");
Seed(shopDb, ("Widget", 9.99), ("Gadget", 24.50));
Seed(warehouseDb, ("Bolt", 0.15), ("Nut", 0.12), ("Washer", 0.05));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5199");

builder.Services.AddEzOData(ez =>
{
    ez.AddService("shop", s => s.UseSqlite(shopDb));
    ez.AddService("warehouse", s => s.UseSqlite(warehouseDb));
    ez.AllowAnonymousInDevelopment();
});

builder.Services.ExtendEzOData(x =>
    x.Service("shop", shop => shop.UseSimpleCrud().Table<Customer, CustomerHandler>()));

builder.Services.AddSimpleCrud("shop", SimpleCRUD.Dialect.SQLite, _ => new SqliteConnection($"Data Source={shopDb}"));

var app = builder.Build();
app.MapEzOData("/api/odata");
app.MapEzODataRest("/api/rest");
app.MapGet("/report", async ([FromKeyedServices("shop")] ISimpleCrud crud) => new
{
    active = await crud.RecordCountAsync<Customer>(new { IsDeleted = false }),
    deleted = await crud.RecordCountAsync<Customer>(new { IsDeleted = true }),
    engine = crud.Engine.Name,
    processWideDialect = SimpleCRUD.GetDialect(),
});
app.Run();

static void Seed(string path, params (string Name, double Price)[] products)
{
    if (File.Exists(path)) File.Delete(path);
    using var connection = new SqliteConnection($"Data Source={path}");
    connection.Execute("""
        CREATE TABLE customers (id INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, country TEXT NOT NULL,
                                created_at TEXT, is_deleted INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE products (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, price REAL NOT NULL);
        """);
    foreach (var (name, price) in products)
    {
        connection.Execute("INSERT INTO products (name, price) VALUES (@name, @price)", new { name, price });
    }
}

[Table("customers")]
public class Customer
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("full_name")] public string Name { get; set; } = "";
    [Column("country")] public string Country { get; set; } = "";
    [Column("created_at"), IgnoreUpdate] public string? CreatedAt { get; set; }
    [Column("is_deleted")] public bool IsDeleted { get; set; }
}

public sealed class CustomerHandler(ILogger<CustomerHandler> log) : EzTableHandler<Customer>
{
    public override Task<QueryRequest> BeforeReadAsync(QueryRequest query, EzHookContext ctx) =>
        Task.FromResult(query.Where(EzFilter.Eq(ctx.Column(nameof(Customer.IsDeleted)), false)));

    public override Task BeforeInsertAsync(Customer c, EzHookContext ctx)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) ctx.Reject("full_name is required.");
        c.Country = c.Country.ToUpperInvariant();
        c.CreatedAt = DateTime.UtcNow.ToString("O");
        log.LogInformation("Creating customer {Name}", c.Name);
        return Task.CompletedTask;
    }

    public override Task<int> DeleteAsync(Customer c, EzHookContext ctx)
    {
        c.IsDeleted = true;
        return ctx.Data.UpdateAsync(c);
    }
}
