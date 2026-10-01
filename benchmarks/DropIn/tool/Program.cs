using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

// Measures the drop-in apps from outside, over real sockets.
//   startup --dll app.dll --url http://127.0.0.1:5701 --runs 7 [--env K=V ...]
//   startup-all --app stock=app.dll=http://127.0.0.1:5701 --app ... [--runs 9]   (interleaved, rotating order)
//   load --target stock=http://127.0.0.1:5701 --target simplecrud=... [--requests 6000] [--requests16 24000] [--rounds 6] --out results.json
return args.FirstOrDefault() switch
{
    "startup" => await Startup(args),
    "startup-all" => await StartupAll(args),
    "load" => await Load(args),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: startup --dll <app.dll> --url <base> [--runs N] [--env K=V]... | load --target name=url... [--requests N] [--rounds N] --out file");
    return 2;
}

static List<string> All(string[] args, string name) =>
    args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToList();

static string? One(string[] args, string name) => All(args, name).LastOrDefault();

// ---- startup: process start → first 200 from a real query ------------------------------------------

static async Task<int> Startup(string[] args)
{
    var dll = One(args, "--dll")!;
    var url = One(args, "--url")!;
    var runs = int.Parse(One(args, "--runs") ?? "7");
    var env = All(args, "--env").Select(e => e.Split('=', 2)).ToList();
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var times = new List<double>();
    object? diag = null;
    for (var run = 0; run < runs; run++)
    {
        // Started from its own folder, as a deployment would: ASP.NET Core reads appsettings.json from the current folder.
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls {url}")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(dll))!,
        };
        foreach (var kv in env) psi.Environment[kv[0]] = kv[1];
        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
        while (true)
        {
            if (process.HasExited) throw new InvalidOperationException($"{dll} exited with {process.ExitCode}");
            if (sw.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException($"{dll} did not answer in 60 s");
            try
            {
                using var response = await http.GetAsync($"{url}/api/odata/shop/customers?$top=1");
                if (response.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(5);
        }

        times.Add(sw.Elapsed.TotalMilliseconds);
        if (run == runs - 1) diag = await http.GetFromJsonAsync<JsonElement>($"{url}/_diag");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true); // never leave the app holding its port
            await process.WaitForExitAsync();
        }
    }

    times.Sort();
    Console.WriteLine(JsonSerializer.Serialize(new { runs = times, medianMs = times[times.Count / 2], afterFirstQuery = diag }));
    return 0;
}

// ---- startup-all: the same, interleaved: each round starts every app once, in rotating order -----------

static async Task<int> StartupAll(string[] args)
{
    var apps = All(args, "--app").Select(a => a.Split('=', 3)).Select(a => (Name: a[0], Dll: a[1], Url: a[2])).ToList();
    var runs = int.Parse(One(args, "--runs") ?? "9");
    var env = All(args, "--env");
    var times = apps.ToDictionary(a => a.Name, _ => new List<double>());
    var diags = new Dictionary<string, object?>();
    for (var round = 0; round < runs; round++)
    {
        foreach (var app in apps.Skip(round % apps.Count).Concat(apps.Take(round % apps.Count)))
        {
            var one = new List<string> { "startup", "--dll", app.Dll, "--url", app.Url, "--runs", "1" };
            foreach (var e in env) one.AddRange(["--env", e]);
            var output = new StringWriter();
            var stdout = Console.Out;
            Console.SetOut(output);
            try { await Startup(one.ToArray()); } finally { Console.SetOut(stdout); }
            var result = JsonSerializer.Deserialize<JsonElement>(output.ToString());
            times[app.Name].Add(result.GetProperty("medianMs").GetDouble());
            diags[app.Name] = result.GetProperty("afterFirstQuery");
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(apps.ToDictionary(a => a.Name, a =>
    {
        var t = times[a.Name].Order().ToList();
        return (object)new { runs = t, medianMs = t[t.Count / 2], afterFirstQuery = diags[a.Name] };
    })));
    return 0;
}

// ---- load: interleaved, every target gets the same requests in rotating order -----------------------

static async Task<int> Load(string[] args)
{
    var targets = All(args, "--target").Select(t => t.Split('=', 2)).Select(t => (Name: t[0], Url: t[1].TrimEnd('/'))).ToList();
    var requests = int.Parse(One(args, "--requests") ?? "6000");      // one at a time
    var requests16 = int.Parse(One(args, "--requests16") ?? "24000"); // 16 at a time: long batches for stable throughput
    var rounds = int.Parse(One(args, "--rounds") ?? "6");
    var outFile = One(args, "--out") ?? "load.json";
    var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 64, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
    using var http = new HttpClient(handler);
    var counter = 0;
    int Next() => Interlocked.Increment(ref counter);

    var scenarios = new (string Name, Func<string, HttpRequestMessage> Make)[]
    {
        ("get-by-key", u => new(HttpMethod.Get, $"{u}/api/odata/shop/customers({Next() % 480 + 1})")),
        ("list", u => new(HttpMethod.Get, $"{u}/api/odata/shop/customers?$filter=country eq 'US'&$orderby=full_name&$top=20")),
        ("expand", u => new(HttpMethod.Get, $"{u}/api/odata/shop/customers({Next() % 480 + 1})?$expand=orders")),
        ("post", u => { var i = Next(); return new(HttpMethod.Post, $"{u}/api/odata/shop/customers") { Content = JsonContent.Create(new { full_name = $"Drop In {i}", email = $"dropin{i}.{Guid.NewGuid():N}@example.com", country = "NZ" }) }; }),
        ("patch", u => { var i = Next(); return new(HttpMethod.Patch, $"{u}/api/odata/shop/customers({i % 480 + 1})") { Content = JsonContent.Create(new { owner_id = $"rep-{i % 5 + 1}" }) }; }),
    };

    var results = new List<object>();
    foreach (var concurrency in new[] { 1, 16 })
    {
        foreach (var (scenario, make) in scenarios)
        {
            var samples = targets.ToDictionary(t => t.Name, _ => new List<double>());
            var wall = targets.ToDictionary(t => t.Name, _ => 0.0);
            var roundP50 = targets.ToDictionary(t => t.Name, _ => new List<double>());
            var roundRps = targets.ToDictionary(t => t.Name, _ => new List<double>());
            foreach (var t in targets) await Batch(http, t.Url, make, 200, concurrency, null); // warm-up
            for (var round = 0; round < rounds; round++)
            {
                foreach (var t in targets.Skip(round % targets.Count).Concat(targets.Take(round % targets.Count)))
                {
                    var sw = Stopwatch.StartNew();
                    var batch = new List<double>();
                    await Batch(http, t.Url, make, (concurrency == 1 ? requests : requests16) / rounds, concurrency, batch);
                    var seconds = sw.Elapsed.TotalSeconds;
                    wall[t.Name] += seconds;
                    samples[t.Name].AddRange(batch);
                    roundP50[t.Name].Add(batch.Order().ElementAt(batch.Count / 2));
                    roundRps[t.Name].Add(batch.Count / seconds);
                }
            }

            foreach (var t in targets)
            {
                var s = samples[t.Name].Order().ToArray();
                results.Add(new
                {
                    target = t.Name, scenario, concurrency, requests = s.Length,
                    p50Ms = s[s.Length / 2], p95Ms = s[(int)(s.Length * 0.95)], p99Ms = s[(int)(s.Length * 0.99)],
                    meanMs = s.Average(), requestsPerSecond = s.Length / wall[t.Name],
                    // Round-to-round spread: the honest resolution (pooled percentiles hide drift between rounds).
                    roundP50Ms = roundP50[t.Name].Order().ToArray(), roundRequestsPerSecond = roundRps[t.Name].Order().ToArray(),
                });
                var rp = roundP50[t.Name];
                var rr = roundRps[t.Name];
                Console.WriteLine($"  c={concurrency,-2} {scenario,-11} {t.Name,-11} p50 {s[s.Length / 2]:F3} ms (rounds {rp.Min():F3}–{rp.Max():F3})  {s.Length / wall[t.Name],8:F0} req/s (rounds {rr.Min():F0}–{rr.Max():F0})");
            }
        }
    }

    await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(new { requests, requests16, rounds, results }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

static async Task Batch(HttpClient http, string url, Func<string, HttpRequestMessage> make, int count, int concurrency, List<double>? samples)
{
    var remaining = count;
    var bag = new System.Collections.Concurrent.ConcurrentBag<double>();
    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
    {
        while (Interlocked.Decrement(ref remaining) >= 0)
        {
            using var request = make(url);
            var sw = Stopwatch.StartNew();
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            sw.Stop();
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{request.Method} {request.RequestUri} → {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
            bag.Add(sw.Elapsed.TotalMilliseconds);
        }
    }));
    samples?.AddRange(bag);
}
