using System.Collections.Concurrent;
using Dapper;

namespace EzOdata.SimpleCrud;

/// <summary>
/// Process-wide registry of <see cref="SimpleCrudEngine"/>s: a lazy singleton per (dialect, naming).
/// </summary>
/// <remarks>
/// <para>
/// SimpleCRUD keeps its dialect, identifier quoting and SQL caches in private static fields, so one
/// copy of the assembly can only ever speak one dialect. An <b>isolated</b> engine loads its own copy
/// of <c>Dapper.SimpleCRUD.dll</c> into a dedicated <c>AssemblyLoadContext</c>, giving it private
/// statics. Dapper itself stays shared, so your type handlers apply everywhere, and the process-wide
/// SimpleCRUD your own code calls is never touched.
/// </para>
/// <para>
/// Isolation needs .NET 5+. On .NET Framework / netstandard2.0, <see cref="For"/> falls back to the
/// <see cref="Shared"/> engine, which allows exactly one dialect per process.
/// </para>
/// </remarks>
public static class SimpleCrudEngines
{
    private static readonly ConcurrentDictionary<EngineKey, Lazy<SimpleCrudEngine>> Engines = new();
    private static readonly object SharedGate = new();
    private static SimpleCrudEngine? _shared;
#if NET
    private static int _sequence;
#endif

    /// <summary>True when this runtime can load isolated SimpleCRUD copies.</summary>
    public static bool IsolationSupported =>
#if NET
        true;
#else
        false;
#endif

    /// <summary>Every engine created so far in this process (diagnostics).</summary>
    public static IReadOnlyList<SimpleCrudEngine> All =>
        Engines.Values.Where(l => l.IsValueCreated).Select(l => l.Value).ToList();

    /// <summary>
    /// The engine for <paramref name="dialect"/>: isolated where supported, otherwise the shared one.
    /// Repeated calls with the same arguments return the same instance.
    /// </summary>
    public static SimpleCrudEngine For(SimpleCRUD.Dialect dialect, SimpleCrudNaming? naming = null) =>
        IsolationSupported ? Isolated(dialect, naming) : Shared(dialect);

    /// <summary>An engine on its own copy of SimpleCRUD, created on first use (thread-safe, exactly once).</summary>
    public static SimpleCrudEngine Isolated(SimpleCRUD.Dialect dialect, SimpleCrudNaming? naming = null)
    {
        if (!IsolationSupported)
        {
            throw new PlatformNotSupportedException(
                "Isolated SimpleCRUD engines need .NET 5 or later (AssemblyLoadContext). Use SimpleCrudEngines.Shared on this runtime.");
        }

        return Engines.GetOrAdd(new EngineKey(dialect, naming),
            key => new Lazy<SimpleCrudEngine>(() => CreateIsolated(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// The engine backed by the process-wide SimpleCRUD (the one your own <c>connection.Get&lt;T&gt;()</c>
    /// calls use). Only one dialect can ever be served this way.
    /// </summary>
    /// <param name="dialect">The dialect the process-wide SimpleCRUD must be on.</param>
    /// <param name="claimProcessDialect">
    /// When false (default) this never changes global state: it fails if SimpleCRUD is on another dialect.
    /// When true it calls <c>SimpleCRUD.SetDialect</c> once — only do this if you own the process.
    /// </param>
    public static SimpleCrudEngine Shared(SimpleCRUD.Dialect dialect, bool claimProcessDialect = false)
    {
        lock (SharedGate)
        {
            if (_shared is not null)
            {
                return _shared.Dialect == dialect
                    ? _shared
                    : throw new InvalidOperationException(
                        $"The process-wide SimpleCRUD already serves {_shared.Dialect}; it cannot also serve {dialect}. " +
                        "SimpleCRUD keeps its dialect in static state. Use SimpleCrudEngines.Isolated (.NET 5+) for additional dialects.");
            }

            var current = SimpleCRUD.GetDialect();
            if (!string.Equals(current, dialect.ToString(), StringComparison.Ordinal))
            {
                if (!claimProcessDialect)
                {
                    throw new InvalidOperationException(
                        $"The process-wide SimpleCRUD dialect is {current}, not {dialect}. Pass claimProcessDialect: true to switch it " +
                        "(affects all SimpleCRUD calls in the process), or use an isolated engine.");
                }

                SimpleCRUD.SetDialect(dialect);
            }

            return _shared = new SimpleCrudEngine(typeof(SimpleCRUD), dialect, isolated: false, $"SimpleCRUD[{dialect}, shared]", naming: null);
        }
    }

    private static SimpleCrudEngine CreateIsolated(EngineKey key)
    {
#if NET
        var name = key.Naming is null
            ? $"SimpleCRUD[{key.Dialect}]"
            : $"SimpleCRUD[{key.Dialect}, {key.Naming.Name}]";
        var context = new SimpleCrudLoadContext($"{name}#{Interlocked.Increment(ref _sequence)}");
        var crud = context.LoadSimpleCrud().GetType("Dapper.SimpleCRUD", throwOnError: true)!;

        var dialectType = crud.GetNestedType("Dialect")!;
        crud.GetMethod("SetDialect")!.Invoke(null, [Enum.Parse(dialectType, key.Dialect.ToString())]);

        if (key.Naming is { } naming)
        {
            NameResolverProxy.Install(crud, key.Dialect, naming, context);
        }

        return new SimpleCrudEngine(crud, key.Dialect, isolated: true, name, key.Naming);
#else
        _ = key;
        throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>Naming participates by reference: reuse one <see cref="SimpleCrudNaming"/> instance per convention.</summary>
    private readonly record struct EngineKey(SimpleCRUD.Dialect Dialect, SimpleCrudNaming? Naming);
}
