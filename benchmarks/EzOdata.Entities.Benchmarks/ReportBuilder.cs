using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace EzOdata.Entities.Benchmarks;

/// <summary>
/// Turns a compare run (TRX files, per-database BenchmarkDotNet JSON and quick-harness JSON) into one
/// self-contained report.html and a report.md for the docs.
/// </summary>
public static class ReportBuilder
{
    private static readonly string[] Operations =
        ["Get by id", "Filtered page (20 rows)", "Insert one", "Update one", "Insert + delete", "Insert 100 (one transaction)"];

    private static readonly string[] Libraries = ["Dapper", "SimpleCRUD", "SimpleCRUDStatic", "EFCore", "EFCoreNoTracking"];
    private static readonly string[] DatabaseOrder = ["sqlite", "postgresql", "mysql", "sqlserver"];
    private static readonly string[] Engines = ["stock", "simplecrud", "efcore"];

    private static readonly Dictionary<string, string> LibraryNames = new()
    {
        ["Dapper"] = "Dapper, hand-written SQL",
        ["SimpleCRUD"] = "Dapper.SimpleCRUD (EzOdata facade)",
        ["SimpleCRUDStatic"] = "Dapper.SimpleCRUD (static API)",
        ["EFCore"] = "EF Core",
        ["EFCoreNoTracking"] = "EF Core, AsNoTracking",
    };

    private static readonly Dictionary<string, string> DatabaseNames = new()
    {
        ["sqlite"] = "SQLite", ["postgresql"] = "PostgreSQL", ["mysql"] = "MySQL", ["sqlserver"] = "SQL Server",
    };

    public sealed record TestRow(string Engine, string Suite, string Name, string FullName, string Outcome, double DurationMs);

    public sealed record DataAccessRow(
        string Database, string Library, string Operation, double MeanNs, double MedianNs, double ErrorNs, double StdDevNs, long AllocatedBytes, int N);

    public sealed record Run(
        List<TestRow> Tests, List<DataAccessRow> DataAccess, List<TimingRow> Http, JsonElement? Host, object Environment, List<string> RawFiles);

    public static async Task WriteAsync(string testsDir, string? runsDir, string? legacyBenchJson, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var run = await LoadAsync(testsDir, runsDir, legacyBenchJson, outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), BuildMarkdown(run));
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.html"), BuildHtml(run));
    }

    // ---- loading -------------------------------------------------------------------------------------

    private static async Task<Run> LoadAsync(string testsDir, string? runsDir, string? legacyBenchJson, string outDir)
    {
        var http = new List<TimingRow>();
        var dataAccess = new List<DataAccessRow>();
        var raw = new List<string>();
        JsonElement? host = null;

        var quickFiles = new List<string>();
        var bdnFiles = new List<string>();
        if (runsDir is not null && Directory.Exists(runsDir))
        {
            quickFiles.AddRange(Directory.GetFiles(runsDir, "quick.json", SearchOption.AllDirectories));
            bdnFiles.AddRange(Directory.GetFiles(runsDir, "*-report-full.json", SearchOption.AllDirectories)
                .Where(f => f.Contains($"{Path.DirectorySeparatorChar}data-access{Path.DirectorySeparatorChar}")));
            raw.AddRange(Directory.GetFiles(runsDir, "*-report.html", SearchOption.AllDirectories));
            raw.AddRange(Directory.GetFiles(runsDir, "*.log", SearchOption.TopDirectoryOnly));
        }

        if (legacyBenchJson is not null && File.Exists(legacyBenchJson)) quickFiles.Add(legacyBenchJson);

        foreach (var file in quickFiles.Select(Path.GetFullPath).Distinct())
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(file));
            if (doc.TryGetProperty("rows", out var rows)) http.AddRange(rows.EnumerateArray().Select(ReadTiming));
            raw.Add(file);
        }

        foreach (var file in bdnFiles)
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(file));
            host ??= doc.TryGetProperty("HostEnvironmentInfo", out var h) ? h : null;
            foreach (var b in doc.GetProperty("Benchmarks").EnumerateArray())
            {
                if (!b.TryGetProperty("Statistics", out var stats) || stats.ValueKind != JsonValueKind.Object) continue;
                var parameters = (b.GetProperty("Parameters").GetString() ?? "").Split('&')
                    .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
                var title = (b.GetProperty("MethodTitle").GetString() ?? "").Trim('\'');
                var ci = stats.GetProperty("ConfidenceInterval");
                dataAccess.Add(new DataAccessRow(
                    parameters.GetValueOrDefault("Database", "?"), parameters.GetValueOrDefault("Library", "?"), title,
                    stats.GetProperty("Mean").GetDouble(), stats.GetProperty("Median").GetDouble(),
                    ci.GetProperty("Margin").GetDouble(), stats.GetProperty("StandardDeviation").GetDouble(),
                    b.TryGetProperty("Memory", out var mem) ? mem.GetProperty("BytesAllocatedPerOperation").GetInt64() : 0,
                    stats.GetProperty("N").GetInt32()));
            }
        }

        return new Run(ParseTrx(testsDir), dataAccess, http, host, EnvironmentBlock(),
            raw.Select(f => Path.GetRelativePath(outDir, f)).OrderBy(f => f).ToList());
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
        var suite = engine == "other" ? simple : simple[(simple.IndexOf('_') + 1)..];
        return (engine, suite, method);
    }

    private static double ParseDuration(string value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts) ? ts.TotalMilliseconds : 0;

    private static TimingRow ReadTiming(JsonElement e) => new(
        e.GetProperty("Engine").GetString() ?? "", e.GetProperty("Database").GetString() ?? "",
        e.GetProperty("Scenario").GetString() ?? "", e.GetProperty("Layer").GetString() ?? "",
        e.GetProperty("Iterations").GetInt32(), e.GetProperty("MeanMs").GetDouble(), e.GetProperty("P50Ms").GetDouble(),
        e.GetProperty("P95Ms").GetDouble(), e.GetProperty("P99Ms").GetDouble(), e.GetProperty("OpsPerSec").GetDouble(),
        e.GetProperty("AllocatedBytes").GetDouble());

    // ---- findings ------------------------------------------------------------------------------------

    /// <summary>Geometric mean over databases of (library mean / Dapper mean) for one operation.</summary>
    private static double? RatioToDapper(Run run, string library, string operation)
    {
        var logs = new List<double>();
        foreach (var db in DatabaseOrder)
        {
            var baseline = Find(run, db, "Dapper", operation);
            var other = Find(run, db, library, operation);
            if (baseline is { MeanNs: > 0 } && other is { MeanNs: > 0 }) logs.Add(Math.Log(other.MeanNs / baseline.MeanNs));
        }

        return logs.Count == 0 ? null : Math.Exp(logs.Average());
    }

    private static DataAccessRow? Find(Run run, string db, string library, string operation) =>
        run.DataAccess.FirstOrDefault(r => r.Database == db && r.Library == library && r.Operation == operation);

    private static List<string> Findings(Run run)
    {
        var notes = new List<string>();
        if (run.DataAccess.Count > 0)
        {
            foreach (var (library, label) in new[] { ("SimpleCRUD", "Dapper.SimpleCRUD"), ("EFCore", "EF Core") })
            {
                var parts = Operations.Select(op => (op, r: RatioToDapper(run, library, op)))
                    .Where(x => x.r is not null).Select(x => $"{x.op.ToLowerInvariant()} {x.r:F2}×").ToList();
                if (parts.Count > 0) notes.Add($"**{label}** against hand-written Dapper (geometric mean across databases): {string.Join(", ", parts)}.");
            }

            var facade = Operations.Select(op => (op, f: Find(run, "sqlite", "SimpleCRUD", op), s: Find(run, "sqlite", "SimpleCRUDStatic", op)))
                .Where(x => x.f is not null && x.s is not null).ToList();
            if (facade.Count > 0)
            {
                var within = facade.Count(x => Math.Abs(x.f!.MeanNs - x.s!.MeanNs) <= x.f.ErrorNs + x.s.ErrorNs);
                notes.Add("**The EzOdata facade** (SimpleCRUD in an isolated copy per dialect) against SimpleCRUD's static API on SQLite: " +
                          string.Join(", ", facade.Select(x => $"{x.op.ToLowerInvariant()} {(x.f!.MeanNs - x.s!.MeanNs) / x.s.MeanNs * 100:+0;-0}%")) +
                          $"; {within} of {facade.Count} are inside the confidence intervals.");
            }

            var tracking = Operations.Take(2).Select(op => (op, t: RatioToDapper(run, "EFCore", op), n: RatioToDapper(run, "EFCoreNoTracking", op)))
                .Where(x => x.t is not null && x.n is not null).ToList();
            if (tracking.Count > 0)
            {
                notes.Add("**EF Core change tracking** on reads, relative to Dapper: " + string.Join(", ", tracking.Select(x => $"{x.op.ToLowerInvariant()} {x.t:F2}× tracked vs {x.n:F2}× AsNoTracking")) + ".");
            }

            var alloc = Operations.Select(op =>
            {
                var ratios = DatabaseOrder.Select(db => (d: Find(run, db, "Dapper", op), s: Find(run, db, "SimpleCRUD", op), e: Find(run, db, "EFCore", op)))
                    .Where(x => x.d is { AllocatedBytes: > 0 } && x.s is not null && x.e is not null).ToList();
                return ratios.Count == 0 ? null : $"{op.ToLowerInvariant()} {ratios.Average(x => (double)x.s!.AllocatedBytes / x.d!.AllocatedBytes):F1}× / {ratios.Average(x => (double)x.e!.AllocatedBytes / x.d!.AllocatedBytes):F1}×";
            }).OfType<string>().ToList();
            if (alloc.Count > 0) notes.Add($"**Allocations** per operation, SimpleCRUD / EF Core relative to Dapper: {string.Join(", ", alloc)}.");
        }

        if (run.Http.Count > 0)
        {
            string? Delta(string scenario, string engine)
            {
                var diffs = DatabaseOrder.Select(db => (s: run.Http.FirstOrDefault(r => r.Database == db && r.Scenario == scenario && r.Engine == "stock"),
                        e: run.Http.FirstOrDefault(r => r.Database == db && r.Scenario == scenario && r.Engine == engine)))
                    .Where(x => x.s is not null && x.e is not null).Select(x => x.e!.P50Ms - x.s!.P50Ms).ToList();
                return diffs.Count == 0 ? null : $"{diffs.Average():+0.00;-0.00;0.00} ms";
            }

            var reads = new[] { "get-by-key", "list", "expand" }.Select(s => (s, sc: Delta(s, "simplecrud"), ef: Delta(s, "efcore"))).Where(x => x.sc is not null).ToList();
            var writes = new[] { "post", "patch", "put" }.Select(s => (s, sc: Delta(s, "simplecrud"), ef: Delta(s, "efcore"))).Where(x => x.sc is not null).ToList();
            if (reads.Count > 0) notes.Add("**Reads through the HTTP API** take the same path on every engine (ez-odata's compiled SQL); median difference from stock ez-odata, SimpleCRUD / EF Core engine: " + string.Join(", ", reads.Select(x => $"{x.s} {x.sc} / {x.ef}")) + ".");
            if (writes.Count > 0) notes.Add("**Writes through the HTTP API** (typed entity, hooks, one transaction), median difference from stock ez-odata, SimpleCRUD / EF Core engine: " + string.Join(", ", writes.Select(x => $"{x.s} {x.sc} / {x.ef}")) + ".");
        }

        if (run.Tests.Count > 0)
        {
            var passed = run.Tests.Count(t => t.Outcome == "Passed");
            var failed = run.Tests.Count(t => t.Outcome == "Failed");
            notes.Add($"**Tests**: {passed} passed, {failed} failed, {run.Tests.Count - passed - failed} skipped.");
        }

        return notes;
    }

    // ---- markdown ------------------------------------------------------------------------------------

    private static string BuildMarkdown(Run run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Dapper, Dapper.SimpleCRUD and EF Core: benchmark report").AppendLine();
        sb.AppendLine($"Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC. Lower is better. ± is the 99.9% confidence interval (BenchmarkDotNet).").AppendLine();
        sb.AppendLine("## Findings").AppendLine();
        foreach (var note in Findings(run)) sb.AppendLine($"- {note}");
        sb.AppendLine();

        if (run.DataAccess.Count > 0)
        {
            sb.AppendLine("## The libraries on their own").AppendLine();
            sb.AppendLine("A pooled connection per operation, as a web request would. Same table, same 5,000 seeded rows, same results (verified before timing). Each cell: mean ± error (ratio to Dapper), then bytes allocated.").AppendLine();
            foreach (var op in Operations.Where(op => run.DataAccess.Any(r => r.Operation == op)))
            {
                var libs = Libraries.Where(l => run.DataAccess.Any(r => r.Operation == op && r.Library == l)).ToList();
                sb.AppendLine($"### {op}").AppendLine();
                sb.AppendLine("| Database | " + string.Join(" | ", libs.Select(l => LibraryNames[l])) + " |");
                sb.AppendLine("|---|" + string.Concat(libs.Select(_ => "---:|")));
                foreach (var db in DatabaseOrder.Where(d => run.DataAccess.Any(r => r.Database == d && r.Operation == op)))
                {
                    var dapper = Find(run, db, "Dapper", op);
                    sb.AppendLine($"| {DatabaseNames[db]} | " + string.Join(" | ", libs.Select(l => Find(run, db, l, op) is { } r
                        ? $"{Time(r.MeanNs)} ± {Time(r.ErrorNs)}{(dapper is null || l == "Dapper" ? "" : $" ({r.MeanNs / dapper.MeanNs:F2}×)")}<br/>{Bytes(r.AllocatedBytes)}"
                        : "—")) + " |");
                }

                sb.AppendLine();
            }
        }

        if (run.Http.Count > 0)
        {
            sb.AppendLine("## Through the HTTP API").AppendLine();
            sb.AppendLine("In-memory TestServer. Engines interleaved request by request. Median / p95, then bytes allocated per request.").AppendLine();
            foreach (var db in DatabaseOrder.Where(d => run.Http.Any(r => r.Database == d)))
            {
                sb.AppendLine($"### {DatabaseNames[db]}").AppendLine();
                sb.AppendLine("| Scenario | stock ez-odata | SimpleCRUD engine | EF Core engine |");
                sb.AppendLine("|---|---:|---:|---:|");
                foreach (var scenario in run.Http.Where(r => r.Database == db).Select(r => r.Scenario).Distinct())
                {
                    sb.AppendLine($"| {scenario} | " + string.Join(" | ", Engines.Select(e => run.Http.FirstOrDefault(r => r.Database == db && r.Scenario == scenario && r.Engine == e) is { } r
                        ? $"{r.P50Ms:F2} / {r.P95Ms:F2} ms<br/>{Bytes((long)r.AllocatedBytes)}" : "—")) + " |");
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine("## How this was measured").AppendLine();
        foreach (var line in Method()) sb.AppendLine($"- {line}");
        sb.AppendLine().AppendLine("## Environment").AppendLine();
        var cpu = CpuName(run);
        if (cpu is not null) sb.AppendLine($"- CPU: {cpu}");
        foreach (var (key, value) in (Dictionary<string, string>)run.Environment) sb.AppendLine($"- {key}: {value}");
        return sb.ToString();
    }

    // ---- html ----------------------------------------------------------------------------------------

    private static string BuildHtml(Run run)
    {
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"/><meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>Data Access Benchmark</title>
<style>
:root{--bg:#fbfaf8;--panel:#fff;--ink:#1d1d1f;--muted:#6b6b70;--line:#e7e5e0;--accent:#3b5bdb;
--c-Dapper:#868e96;--c-SimpleCRUD:#2f9e44;--c-SimpleCRUDStatic:#8ce99a;--c-EFCore:#5f3dc4;--c-EFCoreNoTracking:#b197fc;
--c-stock:#868e96;--c-simplecrud:#2f9e44;--c-efcore:#5f3dc4;--pass:#2b8a3e;--fail:#c92a2a;--skip:#b7791f}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#141416;--panel:#1c1c1f;--ink:#ececef;--muted:#9a9aa2;--line:#2c2c31;--accent:#91a7ff;
--c-Dapper:#adb5bd;--c-SimpleCRUD:#51cf66;--c-SimpleCRUDStatic:#b2f2bb;--c-EFCore:#9775fa;--c-EFCoreNoTracking:#d0bfff;--c-stock:#adb5bd;--c-simplecrud:#51cf66;--c-efcore:#9775fa;--pass:#69db7c;--fail:#ff8787;--skip:#ffd43b}}
:root[data-theme="dark"]{--bg:#141416;--panel:#1c1c1f;--ink:#ececef;--muted:#9a9aa2;--line:#2c2c31;--accent:#91a7ff;
--c-Dapper:#adb5bd;--c-SimpleCRUD:#51cf66;--c-SimpleCRUDStatic:#b2f2bb;--c-EFCore:#9775fa;--c-EFCoreNoTracking:#d0bfff;--c-stock:#adb5bd;--c-simplecrud:#51cf66;--c-efcore:#9775fa;--pass:#69db7c;--fail:#ff8787;--skip:#ffd43b}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.55 ui-sans-serif,system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:1120px;margin:0 auto;padding:40px 16px 80px}
h1{font-size:30px;line-height:1.2;margin:0 0 6px;letter-spacing:-.01em}h2{font-size:21px;margin:48px 0 6px}h3{font-size:16px;margin:0 0 10px}
.sub{color:var(--muted);margin:0 0 18px;max-width:78ch}.chips{display:flex;flex-wrap:wrap;gap:6px}.chip{border:1px solid var(--line);background:var(--panel);border-radius:999px;padding:2px 10px;font-size:12.5px;color:var(--muted)}
.chip b{color:var(--ink);font-weight:600}
.findings{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:6px 22px;margin-top:26px}.findings li{margin:10px 0}
.legend{display:flex;flex-wrap:wrap;gap:14px;margin:8px 0 14px;font-size:13px;color:var(--muted)}.sw{display:inline-block;width:11px;height:11px;border-radius:3px;margin-right:6px;vertical-align:-1px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,460px),1fr));gap:14px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:16px 18px;min-width:0}
.db{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);margin:12px 0 4px}
.bar{display:grid;grid-template-columns:128px 1fr 112px;gap:8px;align-items:center;font-size:12.5px;margin:3px 0}
.bar .name{color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.track{height:12px;border-radius:4px;background:color-mix(in srgb,var(--line) 55%,transparent);position:relative}
.fill{display:block;height:100%;border-radius:4px}.err{position:absolute;top:-2px;height:16px;border-left:1px solid var(--ink);border-right:1px solid var(--ink);opacity:.35}
.val{font-variant-numeric:tabular-nums;text-align:right;white-space:nowrap;line-height:1.25}.val small{color:var(--muted)}
table{border-collapse:collapse;width:100%;font-size:13px;font-variant-numeric:tabular-nums}th,td{padding:6px 8px;border-bottom:1px solid var(--line);text-align:right;vertical-align:top}th:first-child,td:first-child{text-align:left}
th{color:var(--muted);font-weight:600;white-space:nowrap}.scroll{overflow-x:auto}.muted{color:var(--muted)}.pass{color:var(--pass)}.fail{color:var(--fail)}.skip{color:var(--skip)}
details{margin-top:10px}summary{cursor:pointer;color:var(--accent)}ul.method li{margin:6px 0}code{font-size:12.5px;background:color-mix(in srgb,var(--line) 60%,transparent);padding:1px 5px;border-radius:4px}
a{color:var(--accent)}
@media (max-width:520px){.bar{grid-template-columns:84px 1fr 88px}h1{font-size:24px}.card{padding:14px}}
</style></head><body><main>
""");
        sb.Append("<h1>Dapper · Dapper.SimpleCRUD · EF Core</h1>");
        sb.Append($"<p class=\"sub\">The same table and the same operations on every database: first the libraries on their own, then through an instant OData/REST API built on each. Lower is better. Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.</p><div class=\"chips\">");
        if (CpuName(run) is { } cpu) Chip(sb, "CPU", cpu);
        foreach (var (key, value) in (Dictionary<string, string>)run.Environment) Chip(sb, key, value);
        Chip(sb, "databases", string.Join(", ", DatabaseOrder.Where(d => run.DataAccess.Any(r => r.Database == d) || run.Http.Any(r => r.Database == d)).Select(d => DatabaseNames[d])));
        sb.Append("</div>");

        sb.Append("<section class=\"findings\"><ul>");
        foreach (var note in Findings(run)) sb.Append($"<li>{Bold(Enc(note))}</li>");
        sb.Append("</ul></section>");

        if (run.DataAccess.Count > 0)
        {
            sb.Append("<h2>The libraries on their own</h2><p class=\"sub\">No HTTP and no ez-odata: each library does the operation the way its documentation recommends, opening a pooled connection per operation as a web request would. Bars are the mean, whiskers the 99.9% confidence interval; on the right, the mean (with the ratio to Dapper) and the bytes allocated. Hover a bar for the median and sample count.</p>");
            sb.Append("<div class=\"legend\">");
            foreach (var l in Libraries.Where(l => run.DataAccess.Any(r => r.Library == l))) sb.Append($"<span><i class=\"sw\" style=\"background:var(--c-{l})\"></i>{Enc(LibraryNames[l])}</span>");
            sb.Append("</div><div class=\"grid\">");
            foreach (var op in Operations.Where(op => run.DataAccess.Any(r => r.Operation == op)))
            {
                sb.Append($"<div class=\"card\"><h3>{Enc(op)}</h3>");
                foreach (var db in DatabaseOrder.Where(d => run.DataAccess.Any(r => r.Database == d && r.Operation == op)))
                {
                    var rows = Libraries.Select(l => Find(run, db, l, op)).OfType<DataAccessRow>().ToList();
                    var max = rows.Max(r => r.MeanNs + r.ErrorNs);
                    var dapper = rows.FirstOrDefault(r => r.Library == "Dapper");
                    sb.Append($"<div class=\"db\">{DatabaseNames[db]}</div>");
                    foreach (var r in rows)
                    {
                        var w = r.MeanNs / max * 100;
                        var lo = Math.Max(0, r.MeanNs - r.ErrorNs) / max * 100;
                        var hi = (r.MeanNs + r.ErrorNs) / max * 100;
                        var ratio = dapper is not null && r.Library != "Dapper" ? $" <small>{r.MeanNs / dapper.MeanNs:F2}×</small>" : "";
                        sb.Append($"<div class=\"bar\" title=\"{Enc(LibraryNames[r.Library])}: mean {Time(r.MeanNs)} ± {Time(r.ErrorNs)}, median {Time(r.MedianNs)}, {Bytes(r.AllocatedBytes)} allocated, n={r.N}\">");
                        sb.Append($"<span class=\"name\">{Enc(Short(r.Library))}</span><span class=\"track\"><span class=\"fill\" style=\"width:{F(w)}%;background:var(--c-{r.Library})\"></span><span class=\"err\" style=\"left:{F(lo)}%;width:{F(Math.Max(hi - lo, 0.3))}%\"></span></span>");
                        sb.Append($"<span class=\"val\">{Time(r.MeanNs)}{ratio}<br/><small>{Bytes(r.AllocatedBytes)}</small></span></div>");
                    }
                }

                sb.Append("</div>");
            }

            sb.Append("</div>");
        }

        if (run.Http.Count > 0)
        {
            sb.Append("<h2>Through the HTTP API</h2><p class=\"sub\">An in-memory TestServer per engine: stock ez-odata (untyped), the SimpleCRUD engine and the EF Core engine (typed entities, hooks, a transaction per write). For each database and scenario the engines take turns request by request after a shared warm-up. Median / p95 in ms; bytes allocated per request across the whole process.</p>");
            sb.Append("<div class=\"legend\">");
            foreach (var e in Engines) sb.Append($"<span><i class=\"sw\" style=\"background:var(--c-{e})\"></i>{EngineName(e)}</span>");
            sb.Append("</div><div class=\"grid\">");
            foreach (var db in DatabaseOrder.Where(d => run.Http.Any(r => r.Database == d)))
            {
                sb.Append($"<div class=\"card\"><h3>{DatabaseNames[db]}</h3><div class=\"scroll\"><table><thead><tr><th>scenario</th>");
                foreach (var e in Engines) sb.Append($"<th><i class=\"sw\" style=\"background:var(--c-{e})\"></i>{e}</th>");
                sb.Append("</tr></thead><tbody>");
                foreach (var scenario in run.Http.Where(r => r.Database == db).Select(r => r.Scenario).Distinct())
                {
                    sb.Append($"<tr><td>{Enc(scenario)}</td>");
                    foreach (var e in Engines)
                    {
                        var r = run.Http.FirstOrDefault(x => x.Database == db && x.Scenario == scenario && x.Engine == e);
                        sb.Append(r is null ? "<td class=\"muted\">—</td>" : $"<td title=\"mean {r.MeanMs:F3} ms, p99 {r.P99Ms:F2} ms, n={r.Iterations}\">{r.P50Ms:F2}<span class=\"muted\"> / {r.P95Ms:F2}</span><br/><span class=\"muted\">{Bytes((long)r.AllocatedBytes)}</span></td>");
                    }

                    sb.Append("</tr>");
                }

                sb.Append("</tbody></table></div></div>");
            }

            sb.Append("</div>");
        }

        if (run.Tests.Count > 0)
        {
            sb.Append("<h2>Tests</h2><div class=\"card\"><div class=\"scroll\"><table><thead><tr><th>group</th><th>passed</th><th>failed</th><th>skipped</th><th>time</th></tr></thead><tbody>");
            foreach (var g in run.Tests.GroupBy(t => t.Engine).OrderBy(g => g.Key))
            {
                sb.Append($"<tr><td>{Enc(g.Key == "other" ? "facade, engines and API" : EngineName(g.Key) + " suites")}</td><td class=\"pass\">{g.Count(t => t.Outcome == "Passed")}</td><td class=\"fail\">{g.Count(t => t.Outcome == "Failed")}</td><td class=\"skip\">{g.Count(t => t.Outcome is not ("Passed" or "Failed"))}</td><td>{g.Sum(t => t.DurationMs) / 1000:F1} s</td></tr>");
            }

            sb.Append("</tbody></table></div><details><summary>Every test</summary><div class=\"scroll\"><table><tbody>");
            foreach (var t in run.Tests.OrderBy(t => t.FullName))
            {
                var cls = t.Outcome == "Passed" ? "pass" : t.Outcome == "Failed" ? "fail" : "skip";
                sb.Append($"<tr><td>{Enc(t.FullName)}</td><td class=\"{cls}\">{Enc(t.Outcome)}</td><td>{t.DurationMs:F0} ms</td></tr>");
            }

            sb.Append("</tbody></table></div></details></div>");
        }

        sb.Append("<h2>How this was measured</h2><div class=\"card\"><ul class=\"method\">");
        foreach (var line in Method()) sb.Append($"<li>{Bold(Enc(line))}</li>");
        sb.Append("</ul>");
        if (run.RawFiles.Count > 0)
        {
            sb.Append("<details><summary>Raw results</summary><ul>");
            foreach (var f in run.RawFiles) sb.Append($"<li><a href=\"{Enc(f.Replace('\\', '/'))}\">{Enc(f)}</a></li>");
            sb.Append("</ul></details>");
        }

        sb.Append("</div></main></body></html>");
        return sb.ToString();
    }

    public static IEnumerable<string> Method() =>
    [
        "**Correctness first.** Before any timing, every library reads the same 20-row page, then inserts, reads back, updates and deletes a row, on every database; the run stops on any difference.",
        "**One database at a time.** Each database runs in its own process with only its own container started (PostgreSQL 16, MySQL 8.4, SQL Server 2022, or Azure SQL Edge on ARM), so they do not compete for the Docker VM. SQLite is a local file in WAL mode.",
        "**Library benchmarks** use BenchmarkDotNet in process: 5 warm-up and 20 measured iterations of about 250 ms each, with the memory diagnoser. Each (library, database) case starts from a freshly created table with 5,000 rows.",
        "**Same work for everyone.** Dapper runs hand-written SQL and reads the new id back (RETURNING, OUTPUT or LAST_INSERT_ID). SimpleCRUD uses Get, GetListPaged, Insert, Update and Delete. EF Core uses a pooled DbContext factory with one context per operation, as ASP.NET Core does. `Insert 100` is one transaction: SimpleCRUD inserts row by row and returns every id, EF Core batches, and Dapper's list form runs row by row without returning ids.",
        "**SimpleCRUD twice.** The EzOdata facade runs SimpleCRUD in an isolated copy per dialect; SimpleCRUD's own static API is measured next to it on SQLite (its dialect is process-wide, so one database only) to show what the facade costs.",
        "**EF Core × MySQL** is not measured: there is no MySqlConnector-based EF Core 10 provider yet.",
        "**SQLite has one writer.** Its `concurrent-16` row measures SQLite's lock retries (about 1.6 s per batch of 16 inserts), not the engines.",
        "**HTTP numbers** come from an in-memory TestServer (no sockets), so they show the API layer plus the database, not the network. Engines are interleaved request by request so that none benefits from running later; allocations are whole-process bytes per request.",
        "Microbenchmarks on one machine: treat differences inside the confidence interval as equal, and rerun with `./try.sh --benchmark` on your own hardware.",
    ];

    // ---- helpers -------------------------------------------------------------------------------------

    private static string? CpuName(Run run) =>
        run.Host is { } h && h.TryGetProperty("CpuInfo", out var ci) && ci.ValueKind == JsonValueKind.Object && ci.TryGetProperty("ProcessorName", out var pn)
            ? pn.GetString()
            : null;

    private static void Chip(StringBuilder sb, string key, string value) => sb.Append($"<span class=\"chip\">{Enc(key)} <b>{Enc(value)}</b></span>");

    private static string Short(string library) => library switch
    {
        "Dapper" => "Dapper", "SimpleCRUD" => "SimpleCRUD", "SimpleCRUDStatic" => "SimpleCRUD static",
        "EFCore" => "EF Core", "EFCoreNoTracking" => "EF no-tracking", _ => library,
    };

    private static string EngineName(string engine) => engine switch
    {
        "stock" => "stock ez-odata", "simplecrud" => "SimpleCRUD engine", "efcore" => "EF Core engine", _ => engine,
    };

    private static string Time(double ns) => ns switch
    {
        < 1_000 => $"{ns:F0} ns",
        < 1_000_000 => $"{(ns / 1_000).ToString(ns < 100_000 ? "F1" : "F0", CultureInfo.InvariantCulture)} µs",
        _ => $"{(ns / 1_000_000).ToString("F2", CultureInfo.InvariantCulture)} ms",
    };

    private static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / 1048576.0).ToString("F2", CultureInfo.InvariantCulture)} MB",
    };

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string Enc(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static string Bold(string encoded)
    {
        var parts = encoded.Split("**");
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++) sb.Append(i % 2 == 1 ? $"<b>{parts[i]}</b>" : parts[i]);
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "`([^`]+)`", "<code>$1</code>");
    }

    /// <summary>The package version this process resolved (from its deps.json); assembly versions can be stale.</summary>
    private static string Package(string id, Type fallback)
    {
        try
        {
            var deps = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetEntryAssembly()?.GetName().Name}.deps.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(deps));
            foreach (var library in doc.RootElement.GetProperty("libraries").EnumerateObject())
            {
                var slash = library.Name.IndexOf('/');
                if (slash > 0 && library.Name[..slash].Equals(id, StringComparison.OrdinalIgnoreCase)) return library.Name[(slash + 1)..];
            }
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        var info = fallback.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (info ?? fallback.Assembly.GetName().Version?.ToString() ?? "?").Split('+')[0];
    }

    public static object EnvironmentBlock() => new Dictionary<string, string>
    {
        ["OS"] = RuntimeInformation.OSDescription,
        ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
        ["cores"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
        [".NET"] = RuntimeInformation.FrameworkDescription,
        ["Dapper"] = Package("Dapper", typeof(Dapper.SqlMapper)),
        ["Dapper.SimpleCRUD"] = Package("Dapper.SimpleCRUD", typeof(Dapper.SimpleCRUD)),
        ["EF Core"] = Package("Microsoft.EntityFrameworkCore", typeof(Microsoft.EntityFrameworkCore.DbContext)),
        ["ez-odata-api"] = Package("EzOdata.Core", typeof(EzOdata.Core.Policy.Verb)),
        ["EzOdata.SimpleCrud"] = typeof(EzOdata.SimpleCrud.SimpleCrud).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?",
        ["drivers"] = $"Npgsql {Package("Npgsql", typeof(Npgsql.NpgsqlConnection))}, MySqlConnector {Package("MySqlConnector", typeof(MySqlConnector.MySqlConnection))}, SqlClient {Package("Microsoft.Data.SqlClient", typeof(Microsoft.Data.SqlClient.SqlConnection))}, Microsoft.Data.Sqlite {Package("Microsoft.Data.Sqlite.Core", typeof(Microsoft.Data.Sqlite.SqliteConnection))}",
    };
}
