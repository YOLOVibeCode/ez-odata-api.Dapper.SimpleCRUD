using System.Net.Http.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace EzOdata.Entities.Benchmarks;

/// <summary>In-process BenchmarkDotNet jobs that reuse the shared TestServer sessions.</summary>
[MemoryDiagnoser]
public class EngineBenchmarks
{
    public static IEnumerable<string> Engines => new[] { "stock", "simplecrud", "efcore" };
    public static IEnumerable<string> Databases => BenchWorld.DatabaseKinds;

    [ParamsSource(nameof(Engines))]
    public string Engine { get; set; } = "stock";

    [ParamsSource(nameof(Databases))]
    public string Database { get; set; } = "sqlite";

    private BenchSession? _session;
    private int _n;

    [GlobalSetup]
    public void Setup()
    {
        _session = BenchWorld.Require(Engine, Database);
        _n = 0;
    }

    [Benchmark]
    public async Task Post()
    {
        var i = Interlocked.Increment(ref _n);
        (await _session!.Client.PostAsJsonAsync($"{_session.Root}/customers",
            new { full_name = $"bdn{i}", email = $"bdn{i}@b.example", country = "US" })).EnsureSuccessStatusCode();
    }

    [Benchmark]
    public async Task GetByKey() => (await _session!.Client.GetAsync($"{_session.Root}/customers(1)")).EnsureSuccessStatusCode();

    [Benchmark]
    public async Task List() => (await _session!.Client.GetAsync($"{_session.Root}/customers?$filter=country eq 'US'&$orderby=full_name&$top=10")).EnsureSuccessStatusCode();

    [Benchmark]
    public async Task Expand() => (await _session!.Client.GetAsync($"{_session.Root}/customers?$filter=id eq 1&$expand=orders")).EnsureSuccessStatusCode();

    public static int Run(string artifacts)
    {
        BenchmarkRunner.Run<EngineBenchmarks>(Config(Path.Combine(artifacts, "bdn")));
        return 0;
    }

    /// <summary>ShortRun, in process (the sessions and containers live in this process), EF Core × MySQL filtered out.</summary>
    public static IConfig Config(string dir, bool thorough = false)
    {
        Directory.CreateDirectory(dir);
        // Thorough: 20 measured iterations of ~250 ms each (writes against a real database are noisy; ShortRun's
        // 3 iterations are not enough to separate libraries).
        var job = thorough
            ? Job.Default.WithWarmupCount(5).WithIterationCount(20).WithIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(250))
            : Job.ShortRun;
        return ManualConfig.CreateEmpty()
            .AddJob(job.WithToolchain(InProcessEmitToolchain.Instance).WithId(thorough ? "inproc-20" : "inproc"))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumnProvider(DefaultColumnProviders.Instance)
            .AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default)
            .AddExporter(MarkdownExporter.GitHub)
            .AddExporter(HtmlExporter.Default)
            .AddExporter(JsonExporter.Full)
            .AddFilter(new SimpleFilter(b => !Unsupported(b)))
            .WithArtifactsPath(dir)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator | ConfigOptions.KeepBenchmarkFiles | ConfigOptions.JoinSummary);
    }

    private static bool Unsupported(BenchmarkCase b)
    {
        var db = b.Parameters.Items.FirstOrDefault(p => p.Name == "Database")?.Value as string;
        var who = b.Parameters.Items.FirstOrDefault(p => p.Name is "Engine" or "Library")?.Value as string;
        if (who == "SimpleCRUDStatic") return db != "sqlite"; // process-wide dialect: one database only
        return db == "mysql" && who is not null && who.StartsWith("efcore", StringComparison.OrdinalIgnoreCase);
    }
}
