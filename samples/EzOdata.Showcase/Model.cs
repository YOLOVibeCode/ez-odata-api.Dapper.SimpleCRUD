using Dapper;
using EzOdata.Core.Query;
using EzOdata.Entities.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EzOdata.Showcase;

// One set of plain classes serves both engines: SimpleCRUD reads the attributes, EF Core reads ShopContext.

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
    [Column("status")] public string Status { get; set; } = "open";
    [Column("ordered_at")] public string OrderedAt { get; set; } = "";
    [Column("total")] public double Total { get; set; }
}

[Table("order_lines")]
public class OrderLine
{
    [Key, Required, Column("order_id")] public int OrderId { get; set; }
    [Key, Required, Column("line_no")] public int LineNo { get; set; }
    [Column("product_id")] public int ProductId { get; set; }
    [Column("qty")] public int Qty { get; set; }
    [Column("unit_price")] public double UnitPrice { get; set; }
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

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Customer>(e =>
        {
            e.ToTable("customers").HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("full_name");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.Country).HasColumnName("country");
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.CreatedBy).HasColumnName("created_by").Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        });
        model.Entity<Order>(e =>
        {
            e.ToTable("orders").HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.OrderedAt).HasColumnName("ordered_at");
            e.Property(x => x.Total).HasColumnName("total");
        });
        model.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines").HasKey(x => new { x.OrderId, x.LineNo });
            e.Property(x => x.OrderId).HasColumnName("order_id");
            e.Property(x => x.LineNo).HasColumnName("line_no");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.Qty).HasColumnName("qty");
            e.Property(x => x.UnitPrice).HasColumnName("unit_price");
        });
        model.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log").HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Entity).HasColumnName("entity");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.Actor).HasColumnName("actor");
        });
    }
}

/// <summary>Messages queued by OnCommitted: proof that side effects only happen after a successful commit.</summary>
public sealed class Outbox
{
    private readonly List<string> _messages = [];
    public void Add(string message) { lock (_messages) _messages.Add(message); }
    public IReadOnlyList<string> Messages { get { lock (_messages) return _messages.ToList(); } }
}

/// <summary>Engine-neutral business rules: the same class runs on SimpleCRUD and on EF Core.</summary>
public sealed class CustomerHandler(Outbox outbox) : EzTableHandler<Customer>
{
    public override Task<QueryRequest> BeforeReadAsync(QueryRequest query, EzHookContext ctx) =>
        Task.FromResult(query.Where(EzFilter.Eq(ctx.Column(nameof(Customer.IsDeleted)), false)));

    public override Task BeforeInsertAsync(Customer c, EzHookContext ctx)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) ctx.Reject("full_name is required.");
        if (c.Email is { } email && !email.Contains('@')) ctx.Reject("email must be a valid address.");
        c.Country = c.Country.Trim().ToUpperInvariant();
        c.CreatedBy = ctx.UserId ?? "dev";
        return Task.CompletedTask;
    }

    public override async Task AfterInsertAsync(Customer c, EzHookContext ctx)
    {
        await ctx.Data.InsertAsync(new AuditLog { Entity = "customer", EntityId = c.Id, Action = "insert", Actor = ctx.UserId ?? "dev" });
        ctx.OnCommitted(() => outbox.Add($"{ctx.ServiceName}: welcome email to {c.Email ?? c.Name}"));
    }

    public override Task BeforeUpdateAsync(Customer c, Customer original, EzHookContext ctx)
    {
        if (c.Country != original.Country && !ctx.User.IsInRole("admin")) ctx.Forbid("Only admins can move a customer to another country.");
        return Task.CompletedTask;
    }

    /// <summary>DELETE becomes a soft delete.</summary>
    public override Task<int> DeleteAsync(Customer c, EzHookContext ctx)
    {
        var original = new Customer { Id = c.Id, Name = c.Name, Email = c.Email, Country = c.Country, OwnerId = c.OwnerId, IsDeleted = c.IsDeleted };
        c.IsDeleted = true;
        return ctx.Data.UpdateAsync(c, original);
    }

    public override Task AfterDeleteAsync(Customer c, EzHookContext ctx) =>
        ctx.Data.InsertAsync(new AuditLog { Entity = "customer", EntityId = c.Id, Action = "soft-delete", Actor = ctx.UserId ?? "dev" });
}

public sealed class OrderHandler : EzTableHandler<Order>
{
    public const double ApprovalLimit = 10_000;

    public override Task BeforeInsertAsync(Order o, EzHookContext ctx)
    {
        if (o.Total <= 0) ctx.Reject("total must be positive.");
        if (string.IsNullOrEmpty(o.OrderedAt)) o.OrderedAt = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return Task.CompletedTask;
    }

    public override async Task AfterInsertAsync(Order o, EzHookContext ctx)
    {
        await ctx.Data.InsertAsync(new AuditLog { Entity = "order", EntityId = o.Id, Action = "insert", Actor = ctx.UserId ?? "dev" });
        if (o.Total > ApprovalLimit) ctx.Reject($"Orders over {ApprovalLimit:N0} need approval."); // rolls back the order AND the audit row
    }
}
