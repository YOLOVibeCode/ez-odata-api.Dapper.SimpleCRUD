// Proof for the SimpleCRUD PR: an app that uses SQL Server, then PostgreSQL, switching with SetDialect
// between them (the scenario in issues #56 / #218 / #233). Real servers, no mocks.
using Dapper;
using Microsoft.Data.SqlClient;
using Npgsql;

const string mssql = "Server=localhost,51433;User Id=sa;Password=Pr00f!Passw0rd;TrustServerCertificate=True";
const string pg = "Host=localhost;Port=55432;Username=postgres;Password=proof";

Console.WriteLine($"Dapper.SimpleCRUD {typeof(SimpleCRUD).Assembly.GetName().Version} from {Path.GetFileName(Path.GetDirectoryName(typeof(SimpleCRUD).Assembly.Location))}");
foreach (var (cs, isPg) in new[] { (mssql, false), (pg, true) })
{
    using var c = isPg ? (System.Data.IDbConnection)new NpgsqlConnection(cs) : new SqlConnection(cs);
    c.Execute(isPg ? "DROP TABLE IF EXISTS \"Widget\"; CREATE TABLE \"Widget\" (\"Id\" SERIAL PRIMARY KEY, \"Name\" TEXT)"
                   : "IF OBJECT_ID('Widget') IS NOT NULL DROP TABLE Widget; CREATE TABLE Widget (Id INT IDENTITY PRIMARY KEY, Name NVARCHAR(50))");
    c.Execute(isPg ? "INSERT INTO \"Widget\" (\"Name\") VALUES ('pg-widget')" : "INSERT INTO Widget (Name) VALUES ('mssql-widget')");
}

SimpleCRUD.SetDialect(SimpleCRUD.Dialect.SQLServer);
using (var c = new SqlConnection(mssql))
    Console.WriteLine($"1. SQL Server  Get<Widget>(1)      -> {c.Get<Widget>(1)?.Name}");

SimpleCRUD.SetDialect(SimpleCRUD.Dialect.PostgreSQL);
using (var c = new NpgsqlConnection(pg))
{
    Console.WriteLine($"   GetDialect() now = {SimpleCRUD.GetDialect()}");
    try { Console.WriteLine($"2. PostgreSQL  Get<Widget>(1)      -> {c.Get<Widget>(1)?.Name}"); }
    catch (PostgresException e) { Console.WriteLine($"2. PostgreSQL  Get<Widget>(1)      -> FAILED {e.SqlState}: {e.MessageText}"); }
    try { Console.WriteLine($"3. PostgreSQL  Insert(new Widget) -> id {c.Insert(new Widget { Name = "new" })}"); }
    catch (PostgresException e) { Console.WriteLine($"3. PostgreSQL  Insert(new Widget) -> FAILED {e.SqlState}: {e.MessageText}"); }
}

public class Widget { public int Id { get; set; } public string? Name { get; set; } }
