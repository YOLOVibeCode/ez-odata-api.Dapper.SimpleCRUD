using System.Net;
using System.Net.Http.Json;
using Dapper;
using EzOdata.Core.Policy;
using EzOdata.Entities.AspNetCore;
using EzOdata.EntityFrameworkCore.AspNetCore;
using EzOdata.SimpleCrud;
using EzOdata.SimpleCrud.AspNetCore;
using EzOdata.SimpleCrud.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EzOdata.Entities.AspNetCore.Tests;

public sealed class StartupValidationTests
{
    [Table("customers")]
    public class DriftedCustomer
    {
        [Key, Column("id")] public int Id { get; set; }
        [Column("nickname")] public string? Nickname { get; set; }
    }

    [Table("no_such_table")]
    public class Ghost
    {
        public int Id { get; set; }
    }

    [Table("order_lines")]
    public class IncompleteComposite
    {
        [Key, Column("order_id")] public int OrderId { get; set; }
        [Key, Required, Column("line_no")] public int LineNo { get; set; }
        [Column("product")] public string Product { get; set; } = "";
        [Column("qty")] public int Qty { get; set; }
    }

    [Fact]
    public async Task An_entity_that_does_not_match_the_database_stops_startup()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync(Schema.Crm("sqlite"));

        var ex = await Assert.ThrowsAsync<EzExtensionConfigurationException>(() => TestApp.StartAsync(
            ez => ez.AddService("crm", s => s.UseSqlite(db.FilePath!)),
            s => s.ExtendEzOData(x => x.Service("crm", crm => crm.UseSimpleCrud().Table<DriftedCustomer>().Table<Ghost>()))));

        Assert.Contains(ex.Problems, p => p.Contains("\"nickname\"", StringComparison.Ordinal) || p.Contains("nickname", StringComparison.Ordinal));
        Assert.Contains(ex.Problems, p => p.Contains("no_such_table", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtendEzOData_requires_AddEzOData_first()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().ExtendEzOData(x => x.Service("crm", crm => crm.UseSimpleCrud().Table<Customer>())));
        Assert.Contains("AddEzOData", ex.Message);
    }

    [Fact]
    public async Task Missing_engine_stops_startup()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync(Schema.Crm("sqlite"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ez => ez.AddService("crm", s => s.UseSqlite(db.FilePath!)),
            s => s.ExtendEzOData(x => x.Service("crm", crm => crm.Table<Customer>()))));
        Assert.Contains("UseSimpleCrud", ex.Message);
    }

    [Fact]
    public async Task Composite_key_parts_must_be_Required_for_SimpleCRUD()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync(Schema.Crm("sqlite") + Schema.Seed + Schema.OrderLines);

        var ex = await Assert.ThrowsAsync<EzExtensionConfigurationException>(() => TestApp.StartAsync(
            ez => ez.AddService("crm", s => s.UseSqlite(db.FilePath!)),
            s => s.ExtendEzOData(x => x.Service("crm", crm => crm.UseSimpleCrud().Table<IncompleteComposite>()))));

        Assert.Contains(ex.Problems, p => p.Contains("[Required]", StringComparison.Ordinal));
    }
}

public sealed class SimpleCrudNamingTests
{
    public class SnakeOrderLine
    {
        public int Id { get; set; }
        public string ProductName { get; set; } = "";
        public int UnitPrice { get; set; }
    }

    [Fact]
    public async Task UseNaming_maps_snake_case_tables()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync("""
            CREATE TABLE snake_order_line (id INTEGER PRIMARY KEY AUTOINCREMENT, product_name TEXT NOT NULL, unit_price INTEGER NOT NULL);
            INSERT INTO snake_order_line (product_name, unit_price) VALUES ('Widget', 5);
            """);

        using var host = await TestApp.StartAsync(
            ez =>
            {
                ez.AddService("shop", s => s.UseSqlite(db.FilePath!));
                ez.AddRole("admin", r => r.Allow("shop", "*", Verb.All));
                ez.UseHostRoles();
            },
            s => s.ExtendEzOData(x => x.Service("shop", shop => shop
                .UseSimpleCrud()
                .UseNaming(SimpleCrudNaming.SnakeCase)
                .Table<SnakeOrderLine>())));

        var client = host.As("root", "admin");
        var created = await client.PostAsJsonAsync("/api/odata/shop/snake_order_line", new { product_name = "Gadget", unit_price = 9 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using var connection = db.Connect();
        Assert.Equal(2L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM snake_order_line"));
    }
}

public sealed class EscapeHatchTests
{
    public sealed class SimpleCrudEscapeHandler : EzTableHandler<Customer>
    {
        public override Task AfterInsertAsync(Customer customer, EzHookContext ctx)
        {
            Assert.NotNull(ctx.Session());
            Assert.Same(ctx.Session(), ctx.SimpleCrud());
            Assert.True(ctx.SimpleCrudEngine().IsIsolated);
            return ctx.SimpleCrud().InsertAsync(new AuditLog { Entity = "customer", EntityId = customer.Id, Action = "escape", Actor = ctx.UserId });
        }
    }

    public sealed class EfCoreEscapeHandler : EzTableHandler<Customer>
    {
        public override async Task AfterInsertAsync(Customer customer, EzHookContext ctx)
        {
            var db = ctx.DbContext<CrmDbContext>();
            db.AuditLogs.Add(new AuditLog { Entity = "customer", EntityId = customer.Id, Action = "escape", Actor = ctx.UserId });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task SimpleCRUD_escape_hatch_shares_the_write_transaction()
    {
        await using var host = await EscapeHost(s => s.UseSimpleCrud().Table<Customer, SimpleCrudEscapeHandler>());
        var client = host.As("root", "admin");
        var created = await client.PostAsJsonAsync("/api/odata/crm/customers", new { full_name = "Esc", email = "esc@example.com", country = "US" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task EfCore_escape_hatch_shares_the_write_transaction()
    {
        await using var host = await EscapeHost(s => s.UseEfCore<CrmDbContext>().Table<Customer, EfCoreEscapeHandler>());
        var client = host.As("root", "admin");
        var created = await client.PostAsJsonAsync("/api/odata/crm/customers", new { full_name = "Esc", email = "esc@example.com", country = "US" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private static async Task<IAsyncDisposableHost> EscapeHost(Action<EzServiceExtensionBuilder> configure)
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync(Schema.Crm("sqlite") + Schema.Seed);
        var host = await TestApp.StartAsync(
            ez =>
            {
                ez.AddService("crm", s => s.UseSqlite(db.FilePath!));
                ez.AddRole("admin", r => r.Allow("crm", "*", Verb.All));
                ez.UseHostRoles();
            },
            s =>
            {
                s.AddSingleton<HookJournal>();
                s.ExtendEzOData(x => x.Service("crm", crm => configure(crm)));
            });
        return new IAsyncDisposableHost(host, db);
    }

    private sealed class IAsyncDisposableHost(Microsoft.Extensions.Hosting.IHost host, TestDatabase db) : IAsyncDisposable
    {
        public HttpClient As(string user, params string[] roles) => host.As(user, roles);

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (db.FilePath is { } path && File.Exists(path)) File.Delete(path);
        }
    }
}

public sealed class EfCoreGuardTests
{
    public class Dated
    {
        public DateTime Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Animal
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Dog : Animal
    {
        public string Breed { get; set; } = "";
    }

    public class Address
    {
        public string City { get; set; } = "";
    }

    public class CustomerWithHome
    {
        public int Id { get; set; }
        public Address Home { get; set; } = new();
    }

    public class Converted
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public CountryCode Country { get; set; }
    }

    public readonly record struct CountryCode(string Value);

    public sealed class RetryingContext(DbContextOptions<RetryingContext> options) : DbContext(options)
    {
        public DbSet<Customer> Customers => Set<Customer>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Customer>(e =>
            {
                e.ToTable("customers");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.Name).HasColumnName("full_name");
            });
        }
    }

    public sealed class DatedContext(DbContextOptions<DatedContext> options) : DbContext(options)
    {
        public DbSet<Dated> Dates => Set<Dated>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Dated>(e =>
            {
                e.ToTable("dated");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.Name).HasColumnName("name");
            });
        }
    }

    public sealed class TphContext(DbContextOptions<TphContext> options) : DbContext(options)
    {
        public DbSet<Animal> Animals => Set<Animal>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Animal>().ToTable("animals").HasDiscriminator<string>("kind");
            model.Entity<Dog>();
        }
    }

    public sealed class OwnedContext(DbContextOptions<OwnedContext> options) : DbContext(options)
    {
        public DbSet<CustomerWithHome> Customers => Set<CustomerWithHome>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<CustomerWithHome>(e =>
            {
                e.ToTable("homes");
                e.OwnsOne(x => x.Home, o => o.Property(a => a.City).HasColumnName("city"));
            });
        }
    }

    public sealed class ConverterContext(DbContextOptions<ConverterContext> options) : DbContext(options)
    {
        public DbSet<Converted> Items => Set<Converted>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Converted>(e =>
            {
                e.ToTable("converted");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.Name).HasColumnName("name");
                e.Property(x => x.Country).HasColumnName("country")
                    .HasConversion(v => v.Value, v => new CountryCode(v));
            });
        }
    }

    [Fact]
    public async Task Retrying_execution_strategy_is_rejected()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync(Schema.Crm("sqlite"));

        var ex = await Assert.ThrowsAsync<EzExtensionConfigurationException>(() => TestApp.StartAsync(
            ez => ez.AddService("crm", s => s.UseSqlite(db.FilePath!)),
            s => s.ExtendEzOData(x => x.Service("crm", crm => crm
                .UseEfCore<RetryingContext>((o, _, c) =>
                {
                    if (c is not null) o.UseSqlServer(c, sql => sql.EnableRetryOnFailure());
                    else o.UseSqlServer("Server=localhost;Database=ez;TrustServerCertificate=True", sql => sql.EnableRetryOnFailure());
                })
                .Table<Customer>()))));

        Assert.Contains(ex.Problems, p => p.Contains("retrying", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unsupported_key_types_are_rejected()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync("CREATE TABLE dated (id TEXT PRIMARY KEY, name TEXT NOT NULL);");

        var ex = await Assert.ThrowsAsync<EzExtensionConfigurationException>(() => TestApp.StartAsync(
            ez => ez.AddService("crm", s => s.UseSqlite(db.FilePath!)),
            s => s.ExtendEzOData(x => x.Service("crm", crm => crm.UseEfCore<DatedContext>().Table<Dated>()))));

        Assert.Contains(ex.Problems, p => p.Contains("DateTime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inheritance_mapping_is_rejected()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync("CREATE TABLE animals (id INTEGER PRIMARY KEY, name TEXT, kind TEXT, breed TEXT);");

        var ex = await Assert.ThrowsAsync<EzExtensionConfigurationException>(() => TestApp.StartAsync(
            ez => ez.AddService("crm", s => s.UseSqlite(db.FilePath!)),
            s => s.ExtendEzOData(x => x.Service("crm", crm => crm.UseEfCore<TphContext>().Table<Animal>()))));

        Assert.Contains(ex.Problems, p => p.Contains("inheritance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Owned_types_are_rejected()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync("CREATE TABLE homes (id INTEGER PRIMARY KEY, city TEXT);");

        var ex = await Assert.ThrowsAsync<EzExtensionConfigurationException>(() => TestApp.StartAsync(
            ez => ez.AddService("crm", s => s.UseSqlite(db.FilePath!)),
            s => s.ExtendEzOData(x => x.Service("crm", crm => crm.UseEfCore<OwnedContext>().Table<CustomerWithHome>()))));

        Assert.Contains(ex.Problems, p => p.Contains("owned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Value_converters_produce_a_startup_warning()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync("CREATE TABLE converted (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, country TEXT NOT NULL);");
        var sink = new CollectingLoggerProvider();

        using var host = await TestApp.StartAsync(
            ez =>
            {
                ez.AddService("crm", s => s.UseSqlite(db.FilePath!));
                ez.AddRole("admin", r => r.Allow("crm", "*", Verb.All));
                ez.UseHostRoles();
            },
            s =>
            {
                s.AddSingleton<ILoggerProvider>(sink);
                s.ExtendEzOData(x => x.Service("crm", crm => crm.UseEfCore<ConverterContext>().Table<Converted>()));
            });

        Assert.Contains(sink.Messages, m => m.Contains("value converter", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }

        private sealed class Logger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (messages) messages.Add(formatter(state, exception));
            }
        }
    }
}
