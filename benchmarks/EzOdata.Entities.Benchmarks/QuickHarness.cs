using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using EzOdata.Core.Services;
using EzOdata.EntityFrameworkCore.AspNetCore;
using EzOdata.SimpleCrud;
using EzOdata.SimpleCrud.Testing;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Entities.Benchmarks;

public sealed record TimingRow(
    string Engine, string Database, string Scenario, string Layer,
    int Iterations, double MeanMs, double P50Ms, double P95Ms, double P99Ms,
    double OpsPerSec, double AllocatedBytes);

public sealed class QuickHarness
{
    /// <summary>
    /// For each database and scenario, the engines take turns request by request (after a shared warm-up), so
    /// none of them benefits from running later against a warmer database, JIT or connection pool.
    /// </summary>
    public static async Task<List<TimingRow>> RunAsync(IReadOnlyList<BenchSession> sessions, int iterations)
    {
        var rows = new List<TimingRow>();
        foreach (var group in sessions.GroupBy(s => s.Database.Kind))
        {
            var runners = group.Select(s => (Session: s, Scenarios: HttpScenarios(s).ToList())).ToList();
            var names = runners.SelectMany(r => r.Scenarios.Select(x => x.Name)).Distinct().ToList();
            foreach (var name in names)
            {
                var cases = runners
                    .Where(r => r.Scenarios.Any(x => x.Name == name))
                    .Select(r => (r.Session.Engine, Layer: "http", Run: r.Scenarios.First(x => x.Name == name).Run))
                    .ToList();
                rows.AddRange(await MeasureInterleavedAsync(group.Key, name, iterations, cases));
            }

            var direct = runners.Where(r => r.Session.Engine != "stock")
                .Select(r => (r.Session.Engine, Layer: "direct", Run: (Func<Task>)(() => DirectInsertAsync(r.Session))))
                .ToList();
            rows.AddRange(await MeasureInterleavedAsync(group.Key, "direct-insert", iterations, direct));
        }

        return rows;
    }

    public static async Task WriteJsonAsync(string path, IReadOnlyList<TimingRow> rows, object environment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { environment, rows },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<(string Name, Func<Task> Run)> HttpScenarios(BenchSession session)
    {
        var client = session.Client;
        var root = session.Root;
        var n = 0;
        yield return ("post", async () =>
        {
            var i = Interlocked.Increment(ref n);
            var response = await client.PostAsJsonAsync($"{root}/customers",
                new { full_name = $"n{i}", email = $"n{i}@b.example", country = "US" });
            response.EnsureSuccessStatusCode();
        });
        yield return ("get-by-key", () => GetOk(client, $"{root}/customers(1)"));
        yield return ("list", () => GetOk(client, $"{root}/customers?$filter=country eq 'US'&$orderby=full_name&$top=10"));
        yield return ("expand", () => GetOk(client, $"{root}/customers?$filter=id eq 1&$expand=orders"));
        yield return ("patch", async () =>
        {
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"{root}/customers(1)")
                { Content = JsonContent.Create(new { email = $"p{Interlocked.Increment(ref n)}@b.example" }) });
            response.EnsureSuccessStatusCode();
        });
        yield return ("put", async () =>
        {
            var response = await client.PutAsJsonAsync($"{root}/customers(2)",
                new { full_name = "Bob", email = "bob@example.com", country = "EU" });
            response.EnsureSuccessStatusCode();
        });
        yield return ("delete-post", async () =>
        {
            var created = await client.PostAsJsonAsync($"{root}/customers",
                new { full_name = $"d{Interlocked.Increment(ref n)}", country = "US" });
            created.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("id").GetInt32();
            (await client.DeleteAsync($"{root}/customers({id})")).EnsureSuccessStatusCode();
        });
        if (session.Engine != "stock")
        {
            yield return ("hook-audit", async () =>
            {
                var response = await client.PostAsJsonAsync($"{root}/orders",
                    new { customer_id = 1, total = 1.0 + Interlocked.Increment(ref n) });
                response.EnsureSuccessStatusCode();
            });
        }

        yield return ("concurrent-16", async () =>
        {
            await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
            {
                var i = Interlocked.Increment(ref n);
                var response = await client.PostAsJsonAsync($"{root}/customers",
                    new { full_name = $"c{i}", email = $"c{i}@b.example", country = "US" });
                response.EnsureSuccessStatusCode();
            }));
        });
    }

    private static async Task GetOk(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {url} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    private static async Task DirectInsertAsync(BenchSession session)
    {
        await using var connection = session.Database.Connect();
        await connection.OpenAsync();
        if (session.Engine == "simplecrud")
        {
            var engine = SimpleCrudEngines.For(session.Database.Dialect);
            await engine.InsertAsync(connection, new Customer { Name = "direct", Country = "US" });
            return;
        }

        var options = new DbContextOptionsBuilder<BenchDbContext>();
        var connector = session.Database.Kind switch
        {
            "postgresql" => ConnectorTypes.PostgreSql,
            "mysql" => ConnectorTypes.MySql,
            "sqlserver" => ConnectorTypes.SqlServer,
            _ => ConnectorTypes.Sqlite,
        };
        EfCoreExtensions.DefaultProvider(options, connector, connection);
        await using var context = new BenchDbContext(options.Options);
        context.Customers.Add(new Customer { Name = "direct", Country = "US" });
        await context.SaveChangesAsync();
    }

    private static async Task<List<TimingRow>> MeasureInterleavedAsync(string database, string scenario, int iterations,
        IReadOnlyList<(string Engine, string Layer, Func<Task> Run)> cases)
    {
        foreach (var c in cases) Console.WriteLine($"  {database,-10} {scenario,-14} {c.Engine}");
        for (var i = 0; i < Math.Min(20, iterations); i++)
        {
            foreach (var c in cases) await c.Run();
        }

        var samples = cases.Select(_ => new double[iterations]).ToArray();
        var alloc = new long[cases.Count];
        var sw = new Stopwatch();
        for (var i = 0; i < iterations; i++)
        {
            for (var k = 0; k < cases.Count; k++)
            {
                // Whole-process bytes: the in-memory TestServer and the thread pool continuations are part of
                // the request. (Per-thread counters miss everything after the first await.)
                var before = GC.GetTotalAllocatedBytes(precise: false);
                sw.Restart();
                await cases[k].Run();
                sw.Stop();
                samples[k][i] = sw.Elapsed.TotalMilliseconds;
                alloc[k] += GC.GetTotalAllocatedBytes(precise: false) - before;
            }
        }

        return cases.Select((c, k) =>
        {
            var sorted = samples[k].Order().ToArray();
            var mean = sorted.Average();
            return new TimingRow(c.Engine, database, scenario, c.Layer, iterations, mean,
                Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99),
                mean <= 0 ? 0 : 1000.0 / mean, (double)alloc[k] / iterations);
        }).ToList();
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Round((sorted.Length - 1) * p), 0, sorted.Length - 1);
        return sorted[index];
    }
}
