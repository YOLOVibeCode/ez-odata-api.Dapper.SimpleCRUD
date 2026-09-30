using Dapper;
using EzOdata.Entities.AspNetCore;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Entities.Benchmarks;

[Table("customers")]
public class Customer
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("full_name")] public string Name { get; set; } = "";
    [Column("email")] public string? Email { get; set; }
    [Column("country")] public string Country { get; set; } = "";
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
}

public sealed class BenchDbContext : DbContext
{
    public BenchDbContext(DbContextOptions<BenchDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

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
        });
    }
}

public static class BenchSchema
{
    public static string Create(string kind) => kind switch
    {
        "sqlite" => """
            CREATE TABLE customers (id INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, email TEXT, country TEXT NOT NULL);
            CREATE TABLE orders (id INTEGER PRIMARY KEY AUTOINCREMENT, customer_id INTEGER NOT NULL REFERENCES customers(id), total REAL NOT NULL);
            CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, entity TEXT NOT NULL, entity_id INTEGER, action TEXT NOT NULL);
            """,
        "postgresql" => """
            DROP TABLE IF EXISTS orders, audit_log, customers;
            CREATE TABLE customers (id SERIAL PRIMARY KEY, full_name VARCHAR(200) NOT NULL, email VARCHAR(200), country VARCHAR(10) NOT NULL);
            CREATE TABLE orders (id SERIAL PRIMARY KEY, customer_id INT NOT NULL REFERENCES customers(id), total DOUBLE PRECISION NOT NULL);
            CREATE TABLE audit_log (id SERIAL PRIMARY KEY, entity VARCHAR(50) NOT NULL, entity_id BIGINT, action VARCHAR(50) NOT NULL);
            """,
        "mysql" => """
            DROP TABLE IF EXISTS orders, audit_log, customers;
            CREATE TABLE customers (id INT AUTO_INCREMENT PRIMARY KEY, full_name VARCHAR(200) NOT NULL, email VARCHAR(200), country VARCHAR(10) NOT NULL);
            CREATE TABLE orders (id INT AUTO_INCREMENT PRIMARY KEY, customer_id INT NOT NULL, total DOUBLE NOT NULL);
            CREATE TABLE audit_log (id INT AUTO_INCREMENT PRIMARY KEY, entity VARCHAR(50) NOT NULL, entity_id BIGINT, action VARCHAR(50) NOT NULL);
            """,
        "sqlserver" => """
            IF OBJECT_ID('orders') IS NOT NULL DROP TABLE orders;
            IF OBJECT_ID('audit_log') IS NOT NULL DROP TABLE audit_log;
            IF OBJECT_ID('customers') IS NOT NULL DROP TABLE customers;
            CREATE TABLE customers (id INT IDENTITY PRIMARY KEY, full_name NVARCHAR(200) NOT NULL, email NVARCHAR(200), country NVARCHAR(10) NOT NULL);
            CREATE TABLE orders (id INT IDENTITY PRIMARY KEY, customer_id INT NOT NULL REFERENCES customers(id), total FLOAT NOT NULL);
            CREATE TABLE audit_log (id INT IDENTITY PRIMARY KEY, entity NVARCHAR(50) NOT NULL, entity_id BIGINT, action NVARCHAR(50) NOT NULL);
            """,
        _ => throw new NotSupportedException(kind),
    };

    public const string Seed = """
        INSERT INTO customers (full_name, email, country) VALUES ('Ada', 'ada@example.com', 'US');
        INSERT INTO customers (full_name, email, country) VALUES ('Bob', 'bob@example.com', 'EU');
        INSERT INTO orders (customer_id, total) VALUES (1, 10.5);
        """;
}
