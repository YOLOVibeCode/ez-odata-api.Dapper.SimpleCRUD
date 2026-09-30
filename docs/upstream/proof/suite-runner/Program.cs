// Runs Dapper.SimpleCRUD's own test suite for the SQLite dialect exactly like its Program.RunTestsSqLite
// (every public Tests method, skipping "Schema" tests) — minus Console.ReadKey — and reports per test.
using System.Reflection;
using Dapper;

var asm = typeof(Dapper.SimpleCRUDTests.Tests).Assembly;
var program = asm.GetType("Dapper.SimpleCRUDTests.Program")!;
program.GetMethod("SetupSqLite", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

Dapper.SqlMapper.AddTypeHandler(new Dapper.SimpleCRUDTests.OfficeId.MapperTypeHandler()); // done in Program.Setup() before the SQLite run
var tester = new Dapper.SimpleCRUDTests.Tests(SimpleCRUD.Dialect.SQLite);
int pass = 0, fail = 0;
foreach (var method in typeof(Dapper.SimpleCRUDTests.Tests).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
{
    if (method.Name.Contains("Schema")) continue;
    try { method.Invoke(tester, null); pass++; }
    catch (TargetInvocationException e) { fail++; Console.WriteLine($"FAIL {method.Name}: {e.InnerException?.GetType().Name}: {e.InnerException?.Message.Split('\n')[0]}"); }
}
Console.WriteLine($"SimpleCRUD {typeof(SimpleCRUD).Assembly.GetName().Version} SQLite suite: {pass} passed, {fail} failed");
