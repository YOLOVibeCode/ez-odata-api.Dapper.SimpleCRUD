using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace EzOdata.Entities.Benchmarks;

public static class ReportBuilder
{
    public static async Task WriteAsync(string testsDir, string? benchJson, string outDir, object environment)
    {
        Directory.CreateDirectory(outDir);
        var tests = ParseTrx(testsDir);
        var timings = benchJson is { } path && File.Exists(path)
            ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(path))
            : default;
        var md = BuildMarkdown(tests, timings, environment);
        var html = BuildHtml(tests, timings, environment);
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), md);
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.html"), html);
    }

    public static List<TestRow> ParseTrx(string testsDir)
    {
        var rows = new List<TestRow>();
        if (!Directory.Exists(testsDir)) return rows;
        foreach (var file in Directory.GetFiles(testsDir, "*.trx"))
        {
            var doc = XDocument.Load(file);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            foreach (var result in doc.Descendants(ns + "UnitTestResult"))
            {
                var full = result.Attribute("testName")?.Value ?? "";
                var outcome = result.Attribute("outcome")?.Value ?? "Unknown";
                var duration = result.Attribute("duration")?.Value ?? "00:00:00";
                var (engine, suite, name) = SplitName(full);
                rows.Add(new TestRow(engine, suite, name, full, outcome, ParseDuration(duration)));
            }
        }

        return rows;
    }

    public static (string Engine, string Suite, string Name) SplitName(string full)
    {
        var method = full.Contains('.') ? full[(full.LastIndexOf('.') + 1)..] : full;
        var type = full.Contains('.') ? full[..full.LastIndexOf('.')] : full;
        var simple = type.Contains('.') ? type[(type.LastIndexOf('.') + 1)..] : type;
        var engine = simple.StartsWith("SimpleCrud_", StringComparison.Ordinal) ? "simplecrud"
            : simple.StartsWith("EfCore_", StringComparison.Ordinal) ? "efcore"
            : "other";
        var suite = engine == "other" ? simple
            : simple[(simple.IndexOf('_') + 1)..];
        return (engine, suite, method);
    }

    private static double ParseDuration(string value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts) ? ts.TotalMilliseconds : 0;

    private static string BuildMarkdown(List<TestRow> tests, JsonElement timings, object environment)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SimpleCRUD vs EF Core — comparison report");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(JsonSerializer.Serialize(environment, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Tests");
        sb.AppendLine();
        sb.AppendLine("| Suite | Test | SimpleCRUD | EF Core |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var group in tests.Where(t => t.Engine is "simplecrud" or "efcore")
                     .GroupBy(t => (t.Suite, t.Name)).OrderBy(g => g.Key.Suite).ThenBy(g => g.Key.Name))
        {
            var sc = group.FirstOrDefault(t => t.Engine == "simplecrud");
            var ef = group.FirstOrDefault(t => t.Engine == "efcore");
            sb.AppendLine($"| {group.Key.Suite} | {group.Key.Name} | {Cell(sc)} | {Cell(ef)} |");
        }

        var other = tests.Where(t => t.Engine == "other").ToList();
        if (other.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Engine-specific / facade");
            sb.AppendLine();
            sb.AppendLine("| Test | Outcome | ms |");
            sb.AppendLine("|---|---|---|");
            foreach (var row in other.OrderBy(t => t.FullName))
            {
                sb.AppendLine($"| {row.FullName} | {row.Outcome} | {row.DurationMs:F1} |");
            }
        }

        AppendTimingMarkdown(sb, timings);
        return sb.ToString();
    }

    private static void AppendTimingMarkdown(StringBuilder sb, JsonElement timings)
    {
        if (timings.ValueKind != JsonValueKind.Object || !timings.TryGetProperty("rows", out var rows)) return;
        sb.AppendLine();
        sb.AppendLine("## Timings");
        sb.AppendLine();
        sb.AppendLine("| Database | Scenario | Layer | stock mean | simplecrud mean | efcore mean | EF / SimpleCRUD |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var group in rows.EnumerateArray()
                     .Select(ReadTiming)
                     .GroupBy(r => (r.Database, r.Scenario, r.Layer))
                     .OrderBy(g => g.Key.Database).ThenBy(g => g.Key.Layer).ThenBy(g => g.Key.Scenario))
        {
            double? Mean(string engine) => group.FirstOrDefault(r => r.Engine == engine)?.MeanMs;
            var stock = Mean("stock");
            var sc = Mean("simplecrud");
            var ef = Mean("efcore");
            var ratio = sc is > 0 && ef is not null ? (ef.Value / sc.Value).ToString("F2", CultureInfo.InvariantCulture) : "—";
            sb.AppendLine($"| {group.Key.Database} | {group.Key.Scenario} | {group.Key.Layer} | {Fmt(stock)} | {Fmt(sc)} | {Fmt(ef)} | {ratio} |");
        }
    }

    private static string BuildHtml(List<TestRow> tests, JsonElement timings, object environment)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/><title>SimpleCRUD vs EF Core</title>");
        sb.AppendLine("<style>body{font-family:ui-sans-serif,system-ui,sans-serif;margin:2rem;color:#1a1a1a}table{border-collapse:collapse;width:100%;margin:1rem 0}th,td{border:1px solid #ccc;padding:.4rem .6rem;text-align:left}th{background:#f4f4f4}.pass{color:#0a7} .fail{color:#c00} .skip{color:#a60} pre{background:#f7f7f7;padding:1rem;overflow:auto}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>SimpleCRUD vs EF Core</h1>");
        sb.AppendLine("<h2>Environment</h2><pre>");
        sb.AppendLine(System.Net.WebUtility.HtmlEncode(JsonSerializer.Serialize(environment, new JsonSerializerOptions { WriteIndented = true })));
        sb.AppendLine("</pre><h2>Tests</h2>");
        sb.AppendLine("<table><thead><tr><th>Suite</th><th>Test</th><th>SimpleCRUD</th><th>EF Core</th></tr></thead><tbody>");
        foreach (var group in tests.Where(t => t.Engine is "simplecrud" or "efcore")
                     .GroupBy(t => (t.Suite, t.Name)).OrderBy(g => g.Key.Suite).ThenBy(g => g.Key.Name))
        {
            var sc = group.FirstOrDefault(t => t.Engine == "simplecrud");
            var ef = group.FirstOrDefault(t => t.Engine == "efcore");
            sb.AppendLine($"<tr><td>{Enc(group.Key.Suite)}</td><td>{Enc(group.Key.Name)}</td><td>{HtmlCell(sc)}</td><td>{HtmlCell(ef)}</td></tr>");
        }

        sb.AppendLine("</tbody></table>");
        if (timings.ValueKind == JsonValueKind.Object && timings.TryGetProperty("rows", out var rows))
        {
            sb.AppendLine("<h2>Timings</h2><table><thead><tr><th>Database</th><th>Scenario</th><th>Layer</th><th>stock</th><th>simplecrud</th><th>efcore</th><th>EF / SC</th></tr></thead><tbody>");
            foreach (var group in rows.EnumerateArray().Select(ReadTiming)
                         .GroupBy(r => (r.Database, r.Scenario, r.Layer))
                         .OrderBy(g => g.Key.Database).ThenBy(g => g.Key.Scenario))
            {
                double? Mean(string engine) => group.FirstOrDefault(r => r.Engine == engine)?.MeanMs;
                var sc = Mean("simplecrud");
                var ef = Mean("efcore");
                var ratio = sc is > 0 && ef is not null ? (ef.Value / sc.Value).ToString("F2", CultureInfo.InvariantCulture) : "—";
                sb.AppendLine($"<tr><td>{Enc(group.Key.Database)}</td><td>{Enc(group.Key.Scenario)}</td><td>{Enc(group.Key.Layer)}</td><td>{Enc(Fmt(Mean("stock")))}</td><td>{Enc(Fmt(sc))}</td><td>{Enc(Fmt(ef))}</td><td>{ratio}</td></tr>");
            }

            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static TimingRow ReadTiming(JsonElement e) => new(
        e.GetProperty("Engine").GetString() ?? "",
        e.GetProperty("Database").GetString() ?? "",
        e.GetProperty("Scenario").GetString() ?? "",
        e.GetProperty("Layer").GetString() ?? "",
        e.GetProperty("Iterations").GetInt32(),
        e.GetProperty("MeanMs").GetDouble(),
        e.GetProperty("P50Ms").GetDouble(),
        e.GetProperty("P95Ms").GetDouble(),
        e.GetProperty("P99Ms").GetDouble(),
        e.GetProperty("OpsPerSec").GetDouble(),
        e.GetProperty("AllocatedBytes").GetDouble());

    private static string Cell(TestRow? row) => row is null ? "—" : $"{row.Outcome} ({row.DurationMs:F1} ms)";
    private static string HtmlCell(TestRow? row)
    {
        if (row is null) return "—";
        var cls = row.Outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase) ? "pass"
            : row.Outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? "fail" : "skip";
        return $"<span class=\"{cls}\">{Enc(row.Outcome)}</span> ({row.DurationMs:F1} ms)";
    }

    private static string Fmt(double? value) => value is null ? "—" : value.Value.ToString("F2", CultureInfo.InvariantCulture) + " ms";
    private static string Enc(string value) => System.Net.WebUtility.HtmlEncode(value);

    public static object EnvironmentBlock() => new
    {
        os = RuntimeInformation.OSDescription,
        arch = RuntimeInformation.OSArchitecture.ToString(),
        cpu = Environment.ProcessorCount,
        framework = RuntimeInformation.FrameworkDescription,
        ezSc = typeof(EzOdata.SimpleCrud.SimpleCrud).Assembly.GetName().Version?.ToString(),
        efCore = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetName().Version?.ToString(),
        machine = Environment.MachineName,
    };

    public sealed record TestRow(string Engine, string Suite, string Name, string FullName, string Outcome, double DurationMs);
}
