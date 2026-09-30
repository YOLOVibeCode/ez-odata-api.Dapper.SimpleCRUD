using Dapper;
using EzOdata.Core.Query;
using EzOdata.SimpleCrud.AspNetCore;

namespace EzOdata.SimpleCrud.AspNetCore.Tests;

// Plain Dapper.SimpleCRUD entities — nothing ez-odata specific on them.

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
    // "notes" is deliberately not mapped: it stays readable but becomes read-only in the API.
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

/// <summary>Singleton journal a handler writes to, proving handlers are created with DI.</summary>
public sealed class HookJournal
{
    public List<string> Calls { get; } = [];
}

/// <summary>The DreamFactory-style override, as a typed class: validation, stamping, audit, soft delete.</summary>
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
        ctx.Crud.InsertAsync(new AuditLog { Entity = "customer", EntityId = customer.Id, Action = "insert", Actor = ctx.UserId });

    public override Task BeforeUpdateAsync(Customer customer, Customer original, EzHookContext ctx)
    {
        if (customer.Country != original.Country && !ctx.User.IsInRole("admin"))
        {
            ctx.Forbid("Only admins can move a customer to another country.");
        }

        return Task.CompletedTask;
    }

    /// <summary>Soft delete: the API's DELETE becomes an update through SimpleCRUD.</summary>
    public override Task<int> DeleteAsync(Customer customer, EzHookContext ctx)
    {
        customer.IsDeleted = true;
        return ctx.Session!.UpdateAsync(customer);
    }

    public override Task AfterDeleteAsync(Customer customer, EzHookContext ctx) =>
        ctx.Crud.InsertAsync(new AuditLog { Entity = "customer", EntityId = customer.Id, Action = "soft-delete", Actor = ctx.UserId });
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
}
