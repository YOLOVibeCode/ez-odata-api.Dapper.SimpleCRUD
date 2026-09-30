// Sample: an instant OData API over a SQLite database, with one table taken over by a
// Dapper.SimpleCRUD entity + handler, and SimpleCRUD used directly from a custom endpoint.
//
//   dotnet run --project samples/EzOdata.SimpleCrud.Sample
//   curl http://localhost:5199/api/odata/shop/products              # stock instant API
//   curl -X POST http://localhost:5199/api/odata/shop/customers -H 'Content-Type: application/json' \
//        -d '{"full_name":"Grace","country":"us"}'                  # SimpleCRUD + hooks
//   curl -X DELETE 'http://localhost:5199/api/odata/shop/customers(1)' # soft delete
//   curl http://localhost:5199/report                                 # ISimpleCrud in your own code

using Dapper;
using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;
using EzOdata.Core.Query;
using EzOdata.SimpleCrud;
using EzOdata.SimpleCrud.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

var dbPath = Path.Combine(AppContext.BaseDirectory, "shop.db");
Seed(dbPath);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5199");

// 1. Stock ez-odata: every table, instantly (unchanged from the ez-odata README).
builder.Services.AddEzOData(ez =>
{
    ez.AddService("shop", s => s.UseSqlite(dbPath));
    ez.AllowAnonymousInDevelopment();
});

// 2. The extension: take over "customers" with a SimpleCRUD entity and a handler.
builder.Services.ExtendEzOData(x => x.Service("shop", shop => shop.Table<Customer, CustomerHandler>()));

// 3. SimpleCRUD as an instance, for your own code (any dialect; never touches the global one).
builder.Services.AddSimpleCrud("shop", SimpleCRUD.Dialect.SQLite, _ => new SqliteConnection($"Data Source={dbPath}"));

var app = builder.Build();
app.MapEzOData("/api/odata");
app.MapGet("/report", async ([FromKeyedServices("shop")] ISimpleCrud crud) => new
{
    active = await crud.RecordCountAsync<Customer>(new { IsDeleted = false }),
    deleted = await crud.RecordCountAsync<Customer>(new { IsDeleted = true }),
    engine = crud.Engine.Name,
    processWideDialect = SimpleCRUD.GetDialect(),
});
app.Run();

static void Seed(string path)
{
    if (File.Exists(path)) File.Delete(path);
    using var connection = new SqliteConnection($"Data Source={path}");
    connection.Execute("""
        CREATE TABLE customers (id INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, country TEXT NOT NULL,
                                created_at TEXT, is_deleted INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE products (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, price REAL NOT NULL);
        INSERT INTO products (name, price) VALUES ('Widget', 9.99), ('Gadget', 24.50);
        """);
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
        return ctx.Session!.UpdateAsync(c);
    }
}
