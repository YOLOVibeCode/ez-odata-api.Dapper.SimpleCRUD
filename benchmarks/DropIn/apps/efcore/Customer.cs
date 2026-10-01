using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

[Table("customers")]
public class Customer
{
    [Column("id")] public int Id { get; set; }
    [Column("full_name")] public string Name { get; set; } = "";
    [Column("email")] public string? Email { get; set; }
    [Column("country")] public string Country { get; set; } = "";
    [Column("owner_id")] public string? OwnerId { get; set; }
    [Column("created_by")] public string? CreatedBy { get; set; }
    [Column("is_deleted")] public bool IsDeleted { get; set; }
}

public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
}
