using Dapper;
using EzOdata.Core.Query;
using EzOdata.Entities.AspNetCore;
using EzOdata.EntityFrameworkCore.AspNetCore;
using EzOdata.SimpleCrud.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace EzOdata.Entities.AspNetCore.Tests;

[Table("customers")]
public class Customer
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("full_name")] public string Name { get; set; } = "";
    [Column("email")] public string? Email { get; set; }
    [Column("country")] public string Country { get; set; } = "";
    [Column("owner_id")] public string? OwnerId { get; set; }
    [Column("created_by"), IgnoreUpdate] public string? CreatedBy { get; set; }
    [Column("is_deleted")] public bool IsDeleted { get; set; }
}

[Table("orders")]
public class Order
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("customer_id")] public int CustomerId { get; set; }
    [Column("total")] public double Total { get; set; }
}

[Table("audit_log")]
public class AuditLog
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("entity")] public string Entity { get; set; } = "";
    [Column("entity_id")] public long? EntityId { get; set; }
    [Column("action")] public string Action { get; set; } = "";
    [Column("actor")] public string? Actor { get; set; }
}

[Table("order_lines")]
public class OrderLine
{
    [Key, Required, Column("order_id")] public int OrderId { get; set; }
    [Key, Required, Column("line_no")] public int LineNo { get; set; }
    [Column("product")] public string Product { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
}

/// <summary>Singleton journal a handler writes to, proving handlers are created with DI.</summary>
public sealed class HookJournal
{
    public List<string> Calls { get; } = [];
}

/// <summary>Typed hooks: validation, stamping, audit, soft delete. Uses <see cref="EzHookContext.Data"/> so both engines work.</summary>
public sealed class CustomerHandler(HookJournal journal) : EzTableHandler<Customer>
{
    public override Task<QueryRequest> BeforeReadAsync(QueryRequest query, EzHookContext ctx) =>
        Task.FromResult(query.Where(EzFilter.Eq(ctx.Column(nameof(Customer.IsDeleted)), false)));

    public override Task BeforeInsertAsync(Customer customer, EzHookContext ctx)
    {
        if (string.IsNullOrWhiteSpace(customer.Name)) ctx.Reject("Customer name is required.");
        customer.CreatedBy = ctx.UserId;
        customer.Country = customer.Country.ToUpperInvariant();
        lock (journal) journal.Calls.Add($"{ctx.ServiceName}:BeforeInsert:{customer.Name}");
        return Task.CompletedTask;
    }

    public override Task AfterInsertAsync(Customer customer, EzHookContext ctx) =>
        ctx.Data.InsertAsync(new AuditLog { Entity = "customer", EntityId = customer.Id, Action = "insert", Actor = ctx.UserId });

    public override Task BeforeUpdateAsync(Customer customer, Customer original, EzHookContext ctx)
    {
        if (customer.Country != original.Country && !ctx.User.IsInRole("admin"))
        {
            ctx.Forbid("Only admins can move a customer to another country.");
        }

        return Task.CompletedTask;
    }

    public override Task<int> DeleteAsync(Customer customer, EzHookContext ctx)
    {
        customer.IsDeleted = true;
        return ctx.Data.UpdateAsync(customer);
    }

    public override Task AfterDeleteAsync(Customer customer, EzHookContext ctx) =>
        ctx.Data.InsertAsync(new AuditLog { Entity = "customer", EntityId = customer.Id, Action = "soft-delete", Actor = ctx.UserId });
}

/// <summary>Fluent EF Core mapping that mirrors the SimpleCRUD attributes (including CreatedBy ignore-on-update).</summary>
public sealed class CrmDbContext : DbContext
{
    public CrmDbContext(DbContextOptions<CrmDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("full_name");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.Country).HasColumnName("country");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.CreatedBy).HasColumnName("created_by")
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        });

        model.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.Total).HasColumnName("total");
        });

        model.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Entity).HasColumnName("entity");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.Actor).HasColumnName("actor");
        });

        model.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines");
            e.HasKey(x => new { x.OrderId, x.LineNo });
            e.Property(x => x.OrderId).HasColumnName("order_id");
            e.Property(x => x.LineNo).HasColumnName("line_no");
            e.Property(x => x.Product).HasColumnName("product");
            e.Property(x => x.Qty).HasColumnName("qty");
        });
    }
}

public interface IEngineUnderTest
{
    string Name { get; }
    void Configure(EzServiceExtensionBuilder builder);
}

public sealed class SimpleCrudEngineUnderTest : IEngineUnderTest
{
    public static readonly SimpleCrudEngineUnderTest Instance = new();
    public string Name => "simplecrud";
    public void Configure(EzServiceExtensionBuilder builder) => builder.UseSimpleCrud();
}

public sealed class EfCoreEngineUnderTest : IEngineUnderTest
{
    public static readonly EfCoreEngineUnderTest Instance = new();
    public string Name => "efcore";
    public void Configure(EzServiceExtensionBuilder builder) => builder.UseEfCore<CrmDbContext>();
}

public static class Schema
{
    public static string Crm(string kind) => kind switch
    {
        "sqlite" => """
            CREATE TABLE customers (id INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, email TEXT UNIQUE, country TEXT NOT NULL,
                                    owner_id TEXT, created_by TEXT, is_deleted INTEGER NOT NULL DEFAULT 0, notes TEXT);
            CREATE TABLE orders (id INTEGER PRIMARY KEY AUTOINCREMENT, customer_id INTEGER NOT NULL REFERENCES customers(id), total REAL NOT NULL);
            CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, entity TEXT NOT NULL, entity_id INTEGER, action TEXT NOT NULL, actor TEXT);
            CREATE TABLE products (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);
            """,
        "postgresql" => """
            DROP TABLE IF EXISTS orders, audit_log, products, customers;
            CREATE TABLE customers (id SERIAL PRIMARY KEY, full_name VARCHAR(200) NOT NULL, email VARCHAR(200) UNIQUE, country VARCHAR(10) NOT NULL,
                                    owner_id VARCHAR(50), created_by VARCHAR(50), is_deleted BOOLEAN NOT NULL DEFAULT FALSE, notes TEXT);
            CREATE TABLE orders (id SERIAL PRIMARY KEY, customer_id INT NOT NULL REFERENCES customers(id), total DOUBLE PRECISION NOT NULL);
            CREATE TABLE audit_log (id SERIAL PRIMARY KEY, entity VARCHAR(50) NOT NULL, entity_id BIGINT, action VARCHAR(50) NOT NULL, actor VARCHAR(50));
            CREATE TABLE products (id SERIAL PRIMARY KEY, name VARCHAR(100) NOT NULL);
            """,
        "mysql" => """
            DROP TABLE IF EXISTS orders, audit_log, products, customers;
            CREATE TABLE customers (id INT AUTO_INCREMENT PRIMARY KEY, full_name VARCHAR(200) NOT NULL, email VARCHAR(200) UNIQUE, country VARCHAR(10) NOT NULL,
                                    owner_id VARCHAR(50), created_by VARCHAR(50), is_deleted BOOLEAN NOT NULL DEFAULT FALSE, notes TEXT);
            CREATE TABLE orders (id INT AUTO_INCREMENT PRIMARY KEY, customer_id INT NOT NULL, total DOUBLE NOT NULL, FOREIGN KEY (customer_id) REFERENCES customers(id));
            CREATE TABLE audit_log (id INT AUTO_INCREMENT PRIMARY KEY, entity VARCHAR(50) NOT NULL, entity_id BIGINT, action VARCHAR(50) NOT NULL, actor VARCHAR(50));
            CREATE TABLE products (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL);
            """,
        "sqlserver" => """
            IF OBJECT_ID('orders') IS NOT NULL DROP TABLE orders; IF OBJECT_ID('audit_log') IS NOT NULL DROP TABLE audit_log;
            IF OBJECT_ID('products') IS NOT NULL DROP TABLE products; IF OBJECT_ID('customers') IS NOT NULL DROP TABLE customers;
            CREATE TABLE customers (id INT IDENTITY PRIMARY KEY, full_name NVARCHAR(200) NOT NULL, email NVARCHAR(200) UNIQUE, country NVARCHAR(10) NOT NULL,
                                    owner_id NVARCHAR(50), created_by NVARCHAR(50), is_deleted BIT NOT NULL DEFAULT 0, notes NVARCHAR(MAX));
            CREATE TABLE orders (id INT IDENTITY PRIMARY KEY, customer_id INT NOT NULL REFERENCES customers(id), total FLOAT NOT NULL);
            CREATE TABLE audit_log (id INT IDENTITY PRIMARY KEY, entity NVARCHAR(50) NOT NULL, entity_id BIGINT, action NVARCHAR(50) NOT NULL, actor NVARCHAR(50));
            CREATE TABLE products (id INT IDENTITY PRIMARY KEY, name NVARCHAR(100) NOT NULL);
            """,
        _ => throw new NotSupportedException(kind),
    };

    public const string Seed = """
        INSERT INTO customers (full_name, email, country, owner_id) VALUES ('Ada', 'ada@example.com', 'US', 'u1');
        INSERT INTO customers (full_name, email, country, owner_id) VALUES ('Bob', 'bob@example.com', 'EU', 'u2');
        INSERT INTO orders (customer_id, total) VALUES (1, 10.26);
        INSERT INTO products (name) VALUES ('Widget');
        """;

    public const string OrderLines = """
        CREATE TABLE order_lines (order_id INTEGER NOT NULL REFERENCES orders(id), line_no INTEGER NOT NULL,
                                  product TEXT NOT NULL, qty INTEGER NOT NULL, PRIMARY KEY (order_id, line_no));
        INSERT INTO order_lines (order_id, line_no, product, qty) VALUES (1, 1, 'Widget', 2);
        """;
}
