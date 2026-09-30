#if NET
using System.Reflection;
using System.Runtime.Loader;
using Dapper;

namespace EzOdata.SimpleCrud;

/// <summary>
/// Loads a private copy of <c>Dapper.SimpleCRUD</c>; every other assembly (Dapper, System.Data,
/// your entity types, ADO.NET providers) resolves from the default context and is shared.
/// Never unloaded: engines are process-lifetime singletons.
/// </summary>
internal sealed class SimpleCrudLoadContext : AssemblyLoadContext
{
    private static readonly string SimpleCrudName = typeof(SimpleCRUD).Assembly.GetName().Name!;

    public SimpleCrudLoadContext(string name) : base(name, isCollectible: false) { }

    public Assembly LoadSimpleCrud()
    {
        var path = typeof(SimpleCRUD).Assembly.Location;
        if (string.IsNullOrEmpty(path))
        {
            throw new PlatformNotSupportedException(
                "Isolated SimpleCRUD engines load Dapper.SimpleCRUD.dll from disk, which single-file publishing does not provide. " +
                "Publish without PublishSingleFile, or use SimpleCrudEngines.Shared.");
        }

        var assembly = LoadFromAssemblyPath(path);
        if (ReferenceEquals(assembly, typeof(SimpleCRUD).Assembly))
        {
            throw new InvalidOperationException("SimpleCRUD isolation failed: the default copy was returned.");
        }

        return assembly;
    }

    protected override Assembly? Load(AssemblyName assemblyName) =>
        string.Equals(assemblyName.Name, SimpleCrudName, StringComparison.OrdinalIgnoreCase)
            ? LoadSimpleCrud()
            : null; // share everything else with the default context
}
#endif
