using System.Net.Http.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
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
    public Task GetByKey() => _session!.Client.GetAsync($"{_session.Root}/customers(1)");

    [Benchmark]
    public Task List() => _session!.Client.GetAsync($"{_session.Root}/customers?$filter=country eq 'US'&$orderby=full_name&$top=10");

    [Benchmark]
    public Task Expand() => _session!.Client.GetAsync($"{_session.Root}/customers?$filter=id eq 1&$expand=orders");

    public static int Run(string artifacts)
    {
        var dir = Path.Combine(artifacts, "bdn");
        Directory.CreateDirectory(dir);
        var config = ManualConfig.CreateEmpty()
            .AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance).WithId("inproc"))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumnProvider(DefaultColumnProviders.Instance)
            .AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default)
            .AddExporter(MarkdownExporter.GitHub)
            .AddExporter(HtmlExporter.Default)
            .AddExporter(JsonExporter.Full)
            .WithArtifactsPath(dir)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator | ConfigOptions.KeepBenchmarkFiles);
        BenchmarkRunner.Run<EngineBenchmarks>(config);
        return 0;
    }
}
