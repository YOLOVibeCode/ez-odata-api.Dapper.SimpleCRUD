using Dapper;

namespace EzOdata.SimpleCrud.Tests;

[Table("customers")]
public class Customer
{
    [Key] public int Id { get; set; }
    [Column("full_name")] public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    [IgnoreUpdate] public string? Code { get; set; }
    [ReadOnly(true)] public string? Computed { get; set; }
    [NotMapped] public string? Temp { get; set; }
    [Editable(false)] public string? Hidden { get; set; }
}

/// <summary>No attributes: naming comes entirely from the engine's convention.</summary>
public class OrderLine
{
    public int Id { get; set; }
    public string ProductName { get; set; } = "";
    public decimal UnitPrice { get; set; }
}

public static class Ddl
{
    public static string Customers(string kind) => kind switch
    {
        "sqlite" => "DROP TABLE IF EXISTS customers; CREATE TABLE customers (Id INTEGER PRIMARY KEY AUTOINCREMENT, full_name TEXT NOT NULL, Country TEXT NOT NULL, Code TEXT, Computed TEXT DEFAULT 'db');",
        "postgresql" => "DROP TABLE IF EXISTS customers; CREATE TABLE customers (\"Id\" SERIAL PRIMARY KEY, full_name VARCHAR(200) NOT NULL, \"Country\" VARCHAR(10) NOT NULL, \"Code\" VARCHAR(50), \"Computed\" VARCHAR(50) DEFAULT 'db');",
        "mysql" => "DROP TABLE IF EXISTS customers; CREATE TABLE customers (Id INT AUTO_INCREMENT PRIMARY KEY, full_name VARCHAR(200) NOT NULL, Country VARCHAR(10) NOT NULL, Code VARCHAR(50), Computed VARCHAR(50) DEFAULT 'db');",
        "sqlserver" => "IF OBJECT_ID('customers') IS NOT NULL DROP TABLE customers; CREATE TABLE customers (Id INT IDENTITY PRIMARY KEY, full_name NVARCHAR(200) NOT NULL, Country NVARCHAR(10) NOT NULL, Code NVARCHAR(50), Computed NVARCHAR(50) DEFAULT 'db');",
        _ => throw new NotSupportedException(kind),
    };
}
