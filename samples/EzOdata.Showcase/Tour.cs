using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace EzOdata.Showcase;

/// <summary>
/// A narrated walk through the whole API against the running server: every step shows the request, the
/// status, the time and the data, and checks the result, so the tour doubles as an acceptance test.
/// </summary>
public sealed class Tour(string baseUrl, Outbox outbox)
{
    private static readonly bool Color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
    private static readonly JsonSerializerOptions Exact = new();
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    private readonly List<string> _failures = [];
    private readonly Dictionary<string, List<double>> _writeTimes = [];
    private int _passed;
    private int _section;

    public async Task<bool> RunAsync()
    {
        var total = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine(Paint("  ez-odata + Dapper.SimpleCRUD · guided tour", "1;37"));
        Console.WriteLine(Paint($"  3 services, same schema and data: simplecrud (Dapper.SimpleCRUD) · efcore (EF Core) · stock (instant API)", "2"));

        await Guard("Discover", DiscoverAsync);
        await Guard("Query", QueryAsync);
        foreach (var engine in new[] { "simplecrud", "efcore" }) await Guard($"Write ({engine})", () => WritesAsync(engine));
        await Guard("Security", SecurityAsync);
        await Guard("Engines side by side", EnginesSideBySideAsync);

        Console.WriteLine();
        var ok = _failures.Count == 0;
        Console.WriteLine(Paint($"  {(ok ? "✓" : "✗")} {_passed} checks passed, {_failures.Count} failed · {total.Elapsed.TotalSeconds:F1} s", ok ? "1;32" : "1;31"));
        foreach (var failure in _failures) Console.WriteLine(Paint($"    ✗ {failure}", "31"));
        return ok;
    }

    // ---- 1 · Discover ------------------------------------------------------------------------------

    private async Task DiscoverAsync()
    {
        Section("Discover: what the API describes about itself");

        var ui = await Call("GET", "/swagger/index.html");
        Check("Swagger UI is served", ui, ui.Status == 200);

        var config = await Call("GET", "/swagger/index.js");
        var docs = CountOf(config.Text, "openapi.json");
        Check($"Swagger UI lists {docs} OpenAPI documents (3 services × OData + REST), found automatically", config, docs == 6);

        var root = await Call("GET", "/api/odata/simplecrud/");
        var sets = root.Json?.GetProperty("value").EnumerateArray().Select(v => v.GetProperty("name").GetString()!).ToList() ?? [];
        Check("Service document lists the tables", root, sets.Contains("customers") && sets.Contains("order_lines"), $"entity sets: {string.Join(", ", sets)}");

        var metadata = await Call("GET", "/api/odata/simplecrud/$metadata");
        Check("CSDL metadata ($metadata) for OData clients such as Excel and Power BI", metadata,
            metadata.Status == 200 && metadata.Text.Contains("EntityType Name=\"customers\""), $"{CountOf(metadata.Text, "<EntityType ")} entity types, {CountOf(metadata.Text, "<NavigationProperty ")} navigation properties");

        var openapi = await Call("GET", "/api/odata/efcore/openapi.json");
        var tags = openapi.Json?.GetProperty("tags").GetArrayLength() ?? 0;
        var paths = openapi.Json?.GetProperty("paths").EnumerateObject().Count() ?? 0;
        Check("OpenAPI 3.1 document, grouped by table", openapi, tags == 5 && paths >= 10, $"{tags} tables, {paths} paths");
    }

    // ---- 2 · Query ---------------------------------------------------------------------------------

    private async Task QueryAsync()
    {
        Section("Query: OData v4 against the live database");
        const string s = "/api/odata/simplecrud";

        var shaped = await Call("GET", $"{s}/customers?$filter=country eq 'US' and startswith(full_name,'A')&$orderby=full_name&$top=5&$select=id,full_name,email");
        Check("Filter, sort, page and shape in one request", shaped, Rows(shaped).Count is > 0 and <= 5 && Rows(shaped).All(r => r.GetProperty("full_name").GetString()!.StartsWith('A')));
        Table(shaped, "id", "full_name", "email");

        var count = await Call("GET", $"{s}/customers?$count=true&$top=0");
        var stock = await Call("GET", "/api/odata/stock/customers?$count=true&$top=0");
        Check("Counting ($count): the handler hides soft-deleted rows", count,
            Count(count) == Seed.Customers - Seed.Customers / 50 && Count(stock) == Seed.Customers,
            $"simplecrud: {Count(count)} customers visible · stock (no handler): {Count(stock)}");

        var page1 = await Call("GET", $"{s}/customers?$orderby=id&$select=id");
        var next = page1.Json?.TryGetProperty("@odata.nextLink", out var link) == true ? link.GetString() : null;
        var page2 = next is null ? page1 : await Call("GET", new Uri(next).PathAndQuery);
        Check("Server-driven paging follows @odata.nextLink", page2,
            Rows(page1).Count == 25 && next is not null && Rows(page2).First().GetProperty("id").GetInt32() > Rows(page1).Last().GetProperty("id").GetInt32(),
            $"page 1: ids {Rows(page1).First().GetProperty("id")}–{Rows(page1).Last().GetProperty("id")} · page 2 starts at {Rows(page2).FirstOrDefault().GetProperty("id")}");

        var functions = await Call("GET", $"{s}/customers?$filter=contains(full_name,'Lovelace') and length(email) gt 30&$select=full_name,email&$top=3");
        Check("String functions (contains, length)", functions, functions.Status == 200 && Rows(functions).All(r => r.GetProperty("full_name").GetString()!.Contains("Lovelace")));
        Table(functions, "full_name", "email");

        var inList = await Call("GET", $"{s}/customers?$filter=country in ('JP','BR')&$count=true&$top=3&$select=full_name,country");
        Check("The in operator", inList, Count(inList) > 0 && Rows(inList).All(r => r.GetProperty("country").GetString() is "JP" or "BR"), $"{Count(inList)} customers in JP or BR");

        var biggest = await Call("GET", $"{s}/orders?$orderby=total desc&$top=1&$select=id,customer_id,total");
        var orderId = Rows(biggest).First().GetProperty("id").GetInt32();
        var customerId = Rows(biggest).First().GetProperty("customer_id").GetInt32();

        var expand = await Call("GET", $"{s}/customers({customerId})?$select=id,full_name&$expand=orders($select=id,status,total;$orderby=total desc;$top=3)");
        var expanded = expand.Json?.GetProperty("orders").EnumerateArray().ToList() ?? [];
        Check("$expand with nested $select / $orderby / $top", expand, expanded.Count is > 0 and <= 3,
            $"{expand.Json?.GetProperty("full_name")}: {string.Join(", ", expanded.Select(o => $"#{o.GetProperty("id")} {o.GetProperty("status")} {o.GetProperty("total").GetDouble():N0}"))}");

        var lines = await Call("GET", $"{s}/orders({orderId})?$select=id,total&$expand=order_lines($expand=product($select=name,category))");
        var items = lines.Json?.GetProperty("order_lines").EnumerateArray().ToList() ?? [];
        var withProduct = items.Where(i => i.TryGetProperty("product", out _)).ToList();
        Check("Multi-level $expand (order → lines → product)", lines, items.Count > 0 && withProduct.Count == items.Count,
            string.Join(" · ", withProduct.Select(i => $"{i.GetProperty("qty")}× {i.GetProperty("product").GetProperty("name")}")));

        var navFilter = await Call("GET", $"{s}/orders?$filter=customer/country eq 'DE'&$orderby=total desc&$top=3&$select=id,total&$expand=customer($select=full_name,country)");
        Check("Filter across a relationship (customer/country)", navFilter,
            Rows(navFilter).Count > 0 && Rows(navFilter).All(r => r.GetProperty("customer").GetProperty("country").GetString() == "DE"),
            string.Join(" · ", Rows(navFilter).Select(r => $"#{r.GetProperty("id")} {r.GetProperty("total").GetDouble():N0} ({r.GetProperty("customer").GetProperty("full_name")})")));

        var any = await Call("GET", $"{s}/customers?$filter=orders/any(o: o/total gt 5000)&$count=true&$top=3&$select=full_name");
        Check("Lambda any(): customers with an order over 5,000", any, Count(any) > 0, $"{Count(any)} customers");

        var apply = await Call("GET", $"{s}/orders?$apply=groupby((status),aggregate(total with sum as revenue,total with average as average))");
        Check("Aggregation ($apply groupby + aggregate)", apply, apply.Status == 200 && Rows(apply).Count >= 3);
        Table(apply, "status", "revenue", "average");

        var rest = await Call("GET", "/api/rest/simplecrud/_table/products?filter=category = 'Laptops' and price > 300&order=price desc&fields=name,price&include_count=true");
        var restRows = rest.Json?.GetProperty("resource").EnumerateArray().ToList() ?? [];
        Check("The same data through the REST dialect", rest, restRows.Count > 0 && rest.Json?.GetProperty("meta").GetProperty("count").GetInt32() == restRows.Count,
            string.Join(" · ", restRows.Select(r => $"{r.GetProperty("name")} {r.GetProperty("price").GetDouble():N0}")));

        var composite = await Call("GET", $"{s}/order_lines(order_id={orderId},line_no=1)?$expand=product($select=name)");
        Check("Composite key lookup (order_id + line_no)", composite, composite.Status == 200,
            $"order {orderId}, line 1: {composite.Json?.GetProperty("qty")}× {composite.Json?.GetProperty("product").GetProperty("name")}");

        var bad = await Call("GET", $"{s}/customers?$filter=no_such_column eq 1");
        Check("Mistakes get a clear 400, not a 500", bad, bad.Status == 400, Error(bad));
    }

    // ---- 3 · Writes through each engine -------------------------------------------------------------

    private async Task WritesAsync(string engine)
    {
        Section($"Write: through the {(engine == "efcore" ? "EF Core" : "Dapper.SimpleCRUD")} engine, with the same handlers");
        var s = $"/api/odata/{engine}";
        var tag = engine;
        var email = $"grace.{engine}.{Guid.NewGuid():N}"[..28] + "@example.com";

        var created = await Timed(engine, () => Call("POST", $"{s}/customers", new { full_name = "Grace Hopper", email, country = " us ", owner_id = "rep-1" }));
        var id = created.Json?.GetProperty("id").GetInt32() ?? 0;
        Check($"[{tag}] Create: BeforeInsert stamps created_by and normalizes country", created,
            created.Status == 201 && created.Json?.GetProperty("country").GetString() == "US" && created.Json?.GetProperty("created_by").GetString() == "dev",
            $"id {id} · country \" us \" → {created.Json?.GetProperty("country")} · created_by {created.Json?.GetProperty("created_by")}");

        var audit = await Call("GET", $"{s}/audit_log?$filter=entity eq 'customer' and entity_id eq {id}&$select=action,actor");
        Check($"[{tag}] AfterInsert wrote an audit row in the same transaction", audit, Rows(audit).Any(r => r.GetProperty("action").GetString() == "insert"));

        var invalid = await Call("POST", $"{s}/customers", new { full_name = "  ", country = "US" });
        Check($"[{tag}] Validation: a hook's Reject() is a 400 and nothing is written", invalid, invalid.Status == 400, Error(invalid));

        var patch = await Timed(engine, () => Call("PATCH", $"{s}/customers({id})", new { email = "grace@navy.example" }));
        var reread = await Call("GET", $"{s}/customers({id})?$select=full_name,email,country");
        Check($"[{tag}] PATCH changes only the fields sent", patch,
            patch.Status == 200 && reread.Json?.GetProperty("email").GetString() == "grace@navy.example" && reread.Json?.GetProperty("full_name").GetString() == "Grace Hopper");

        var duplicate = await Call("POST", $"{s}/customers", new { full_name = "Copy", email = "grace@navy.example", country = "US" });
        Check($"[{tag}] A unique-constraint violation is a 409", duplicate, duplicate.Status == 409, Error(duplicate));

        var before = Count(await Call("GET", $"{s}/orders?$count=true&$top=0"));
        var tooBig = await Call("POST", $"{s}/orders", new { customer_id = id, status = "open", total = 25_000.0 });
        var after = Count(await Call("GET", $"{s}/orders?$count=true&$top=0"));
        Check($"[{tag}] A Reject() after the insert rolls back the order and its audit row", tooBig, tooBig.Status == 400 && before == after,
            $"{Error(tooBig)} · orders before/after: {before}/{after}");

        var order = await Timed(engine, () => Call("POST", $"{s}/orders", new { customer_id = id, status = "open", total = 1299.0 }));
        var newOrder = order.Json?.GetProperty("id").GetInt32() ?? 0;
        var line = await Call("POST", $"{s}/order_lines", new { order_id = newOrder, line_no = 1, product_id = 1, qty = 1, unit_price = 1299.0 });
        var zero = await Call("POST", $"{s}/order_lines", new { order_id = newOrder, line_no = 2, product_id = 1, qty = 0, unit_price = 1299.0 });
        Check($"[{tag}] Order + composite-key line created; a qty of 0 is rejected", line, order.Status == 201 && line.Status == 201 && zero.Status == 400,
            $"order {newOrder} · line (order_id={newOrder},line_no=1) · qty 0 → {zero.Status}");

        var delete = await Timed(engine, () => Call("DELETE", $"{s}/customers({id})"));
        var gone = await Call("GET", $"{s}/customers({id})");
        var trail = await Call("GET", $"{s}/audit_log?$filter=entity eq 'customer' and entity_id eq {id}&$orderby=id&$select=action");
        Check($"[{tag}] DELETE is a soft delete: hidden from the API, kept in the table, audited", delete,
            delete.Status == 204 && gone.Status == 404 && Rows(trail).Any(r => r.GetProperty("action").GetString() == "soft-delete"),
            $"audit trail: {string.Join(" → ", Rows(trail).Select(r => r.GetProperty("action").GetString()))}");

        var messages = outbox.Messages.Where(m => m.StartsWith(engine + ":", StringComparison.Ordinal)).ToList();
        Check($"[{tag}] OnCommitted ran once, for the committed insert only (not the rejected ones)", null, messages.Count == 1, messages.FirstOrDefault());
    }

    // ---- 4 · Security ------------------------------------------------------------------------------

    private async Task SecurityAsync()
    {
        Section("Security: row filters and field policies from ez-odata roles");
        const string s = "/api/odata/simplecrud";
        var rep = new Dictionary<string, string> { ["X-Demo-User"] = "rep-1" };
        var viewer = new Dictionary<string, string> { ["X-Demo-User"] = "val", ["X-Demo-Roles"] = "viewer" };

        var mine = await Call("GET", $"{s}/customers?$count=true&$top=3&$select=id,full_name,owner_id", headers: rep);
        Check("rep-1 sees only their own customers (row filter owner_id eq @identity.sub)", mine,
            Count(mine) == Seed.Customers / Seed.Reps && Rows(mine).All(r => r.GetProperty("owner_id").GetString() == "rep-1"), $"{Count(mine)} of {Seed.Customers}");

        var others = await Call("GET", $"{s}/customers(2)", headers: rep);
        Check("…another rep's customer is simply not there (404)", others, others.Status == 404);

        var hijack = await Call("PATCH", $"{s}/customers(2)", new { email = "stolen@example.com" }, rep);
        Check("…and cannot be changed (404; the write is gated by the same row filter)", hijack, hijack.Status == 404);

        var forged = await Call("POST", $"{s}/customers", new { full_name = "Forged", email = $"f{Guid.NewGuid():N}"[..12] + "@example.com", country = "US", owner_id = "rep-2" }, rep);
        Check("…creating a row outside the filter is refused and rolled back (403)", forged, forged.Status == 403);

        var move = await Call("PATCH", $"{s}/customers(1)", new { country = "FR" }, rep);
        Check("A handler rule: only admins can move a customer to another country (403)", move, move.Status == 403, Error(move));

        var masked = await Call("GET", $"{s}/customers?$top=3&$select=full_name,email", headers: viewer);
        Check("viewer sees emails masked by a field policy", masked, Rows(masked).All(r => r.GetProperty("email").GetString() == "***"));
        Table(masked, "full_name", "email");

        var readOnly = await Call("POST", $"{s}/products", new { name = "Nope", category = "X", price = 1.0, stock = 1 }, viewer);
        Check("…and is read-only (403)", readOnly, readOnly.Status == 403);
    }

    // ---- 5 · Engines side by side --------------------------------------------------------------------

    private async Task EnginesSideBySideAsync()
    {
        Section("Engines side by side (a quick sample; ./try.sh --benchmark runs the full benchmark)");
        Console.WriteLine(Paint("    Engines are interleaved request by request (no warm-up bias). Median / p95 of 200 requests each, localhost HTTP.", "2"));
        Console.WriteLine(Paint($"    {"operation",-20} {"stock",-18} {"simplecrud",-18} {"efcore",-18}", "2"));
        var engines = new[] { "stock", "simplecrud", "efcore" };
        var live = Enumerable.Range(1, 400).Where(id => id % 50 != 0).ToArray(); // every 50th customer is soft-deleted
        foreach (var (name, make) in new (string, Func<string, int, Task<Result>>)[]
        {
            ("GET by key", (svc, i) => Call("GET", $"/api/odata/{svc}/customers({live[i % live.Length]})")),
            ("GET filtered list", (svc, _) => Call("GET", $"/api/odata/{svc}/customers?$filter=country eq 'US'&$orderby=full_name&$top=20")),
            ("POST (insert)", (svc, i) => Call("POST", $"/api/odata/{svc}/orders", new { customer_id = live[i % live.Length], status = "open", ordered_at = "2026-09-30", total = 10.0 + i })),
            ("PATCH (update)", (svc, i) => Call("PATCH", $"/api/odata/{svc}/orders({i % 400 + 1})", new { status = i % 2 == 0 ? "paid" : "open" })),
        })
        {
            var samples = engines.ToDictionary(e => e, _ => new List<double>());
            for (var i = 0; i < 30; i++) foreach (var e in engines) await make(e, i); // warm-up
            for (var i = 0; i < 200; i++)
            {
                foreach (var e in engines)
                {
                    var r = await make(e, i);
                    if (r.Status >= 400) throw new InvalidOperationException($"{name} on {e} returned {r.Status}: {Truncate(r.Text, 200)}");
                    samples[e].Add(r.Ms);
                }
            }

            string Cell(string e) { var x = samples[e].Order().ToList(); return $"{x[x.Count / 2]:F2} / {x[(int)(x.Count * 0.95)]:F2} ms"; }
            Console.WriteLine($"    {name,-20} {Cell("stock"),-18} {Cell("simplecrud"),-18} {Cell("efcore"),-18}");
        }

        Console.WriteLine(Paint("    Reads take the same path on every service (ez-odata's compiled SQL); engines differ on writes.", "2"));
        Console.WriteLine(Paint("    Not like for like on writes: simplecrud and efcore also run OrderHandler (validation plus an audit row in the", "2"));
        Console.WriteLine(Paint("    same transaction); stock writes the row alone. The benchmark compares equal work.", "2"));
        _passed++;
    }

    // ---- plumbing ------------------------------------------------------------------------------------

    /// <summary>A surprise in one section is reported as a failure; the tour carries on.</summary>
    private async Task Guard(string section, Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            _failures.Add($"{section}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(Paint($"  ✗ {section} stopped early: {ex.GetType().Name}: {ex.Message}", "31"));
        }
    }

    private sealed record Result(string Method, string Url, int Status, string Text, JsonElement? Json, double Ms);

    private async Task<Result> Call(string method, string url, object? body = null, Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Exact);
        foreach (var (k, v) in headers ?? []) request.Headers.Add(k, v);
        var watch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        watch.Stop();
        JsonElement? json = null;
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json") == true && text.Length > 0)
        {
            try { json = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
        }

        return new Result(method, url, (int)response.StatusCode, text, json, watch.Elapsed.TotalMilliseconds);
    }

    private async Task<Result> Timed(string engine, Func<Task<Result>> call)
    {
        var result = await call();
        (_writeTimes.TryGetValue(engine, out var list) ? list : _writeTimes[engine] = []).Add(result.Ms);
        return result;
    }

    private void Section(string title)
    {
        _section++;
        Console.WriteLine();
        Console.WriteLine(Paint($"  {_section} · {title}", "1;36"));
    }

    private void Check(string title, Result? result, bool ok, string? detail = null)
    {
        if (ok) _passed++; else _failures.Add(title);
        var mark = ok ? Paint("✓", "32") : Paint("✗", "31");
        var meta = result is null ? "" : $"{result.Status} · {result.Ms:F0} ms";
        Console.WriteLine($"  {mark} {title}{new string(' ', Math.Max(1, 84 - title.Length))}{Paint(meta, ok ? "2" : "31")}");
        if (result is not null) Console.WriteLine(Paint($"      {result.Method} {WebUtility.UrlDecode(result.Url)}", "2"));
        if (!string.IsNullOrEmpty(detail)) Console.WriteLine($"      {detail}");
        if (!ok && result is not null) Console.WriteLine(Paint($"      response: {Truncate(result.Text, 300)}", "31"));
    }

    private static void Table(Result result, params string[] columns)
    {
        var rows = Rows(result).Take(5).ToList();
        if (rows.Count == 0) return;
        var cells = rows.Select(r => columns.Select(c => r.TryGetProperty(c, out var v) ? Format(v) : "").ToArray()).ToList();
        var widths = columns.Select((c, i) => Math.Min(40, Math.Max(c.Length, cells.Max(row => row[i].Length)))).ToArray();
        Console.WriteLine(Paint("      " + string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))), "2"));
        foreach (var row in cells) Console.WriteLine("      " + string.Join("  ", row.Select((v, i) => Truncate(v, widths[i]).PadRight(widths[i]))));
    }

    private static List<JsonElement> Rows(Result result) =>
        result.Json is { } j && j.TryGetProperty("value", out var v) ? v.EnumerateArray().ToList() : [];

    private static long Count(Result result) =>
        result.Json is { } j && j.TryGetProperty("@odata.count", out var c) ? c.GetInt64() : -1;

    private static string Error(Result result) =>
        result.Json is { } j && j.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? $"“{m.GetString()}”" : "";

    private static string Format(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out var l) => l.ToString(),
        JsonValueKind.Number => v.GetDouble().ToString("N2"),
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Null => "null",
        _ => v.GetRawText(),
    };

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Paint(string text, string code) => Color ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
