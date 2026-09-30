using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
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
    public static async Task<List<TimingRow>> RunAsync(IReadOnlyList<BenchSession> sessions, int iterations)
    {
        var rows = new List<TimingRow>();
        foreach (var session in sessions)
        {
            foreach (var scenario in HttpScenarios(session))
            {
                rows.Add(await MeasureAsync(session.Engine, session.Database.Kind, scenario.Name, "http", iterations, scenario.Run));
            }

            if (session.Engine != "stock")
            {
                rows.Add(await MeasureAsync(session.Engine, session.Database.Kind, "direct-insert", "direct", iterations,
                    () => DirectInsertAsync(session)));
            }
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
        yield return ("get-by-key", () => client.GetAsync($"{root}/customers(1)").ContinueWith(t => t.Result.EnsureSuccessStatusCode()));
        yield return ("list", () => client.GetAsync($"{root}/customers?$filter=country eq 'US'&$orderby=full_name&$top=10")
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode()));
        yield return ("expand", () => client.GetAsync($"{root}/customers?$filter=id eq 1&$expand=orders")
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode()));
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
        switch (session.Database.Kind)
        {
            case "postgresql": options.UseNpgsql(connection); break;
            case "mysql": options.UseMySql(connection, new MySqlServerVersion(new Version(8, 4, 0))); break;
            case "sqlserver": options.UseSqlServer(connection); break;
            default: options.UseSqlite(connection); break;
        }
        await using var context = new BenchDbContext(options.Options);
        context.Customers.Add(new Customer { Name = "direct", Country = "US" });
        await context.SaveChangesAsync();
    }

    private static async Task<TimingRow> MeasureAsync(string engine, string database, string scenario, string layer,
        int iterations, Func<Task> run)
    {
        for (var i = 0; i < Math.Min(20, iterations); i++) await run();

        var samples = new double[iterations];
        var alloc = new long[iterations];
        var sw = new Stopwatch();
        for (var i = 0; i < iterations; i++)
        {
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart();
            await run();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
            alloc[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Array.Sort(samples);
        var mean = samples.Average();
        return new TimingRow(engine, database, scenario, layer, iterations, mean,
            Percentile(samples, 0.50), Percentile(samples, 0.95), Percentile(samples, 0.99),
            mean <= 0 ? 0 : 1000.0 / mean, alloc.Average());
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Round((sorted.Length - 1) * p), 0, sorted.Length - 1);
        return sorted[index];
    }
}
