using System.Diagnostics;
using System.Runtime.Loader;

// Measurement only (not part of a real app): which assemblies are loaded, and into which load context.
// This is how the drop-in shows how it references Dapper.SimpleCRUD at run time.
public static class DropInDiagnostics
{
    public static void Map(WebApplication app) => app.MapGet("/_diag", () =>
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return new
        {
            workingSetMb = Math.Round(process.WorkingSet64 / 1048576.0, 1),
            managedHeapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            loadContexts = AssemblyLoadContext.All.Select(c => new
            {
                name = c.Name ?? "(unnamed)",
                assemblies = c.Assemblies.Select(a => $"{a.GetName().Name} {a.GetName().Version}").Order().ToArray(),
            }).Where(c => c.assemblies.Length > 0).OrderBy(c => c.name).ToArray(),
        };
    });
}
