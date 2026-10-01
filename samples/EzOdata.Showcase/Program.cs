// ez-odata + Dapper.SimpleCRUD showcase. Run it with ./try.sh (or try.cmd on Windows), or directly:
//   dotnet run --project samples/EzOdata.Showcase -- --tour --open
// Three services over the same schema and data:
//   simplecrud  writes go through Dapper.SimpleCRUD (EzOdata.SimpleCrud.AspNetCore)
//   efcore      writes go through EF Core (EzOdata.EntityFrameworkCore.AspNetCore)
//   stock       ez-odata's instant API, no takeover
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;
using EzOdata.Core.Policy;
using EzOdata.Entities.AspNetCore;
using EzOdata.EntityFrameworkCore.AspNetCore;
using EzOdata.Showcase;
using EzOdata.SimpleCrud.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

var port = int.TryParse(Arg("--port"), out var p) ? p : 5199;
var baseUrl = $"http://localhost:{port}";
var dataDir = Arg("--data") ?? Path.Combine(Path.GetTempPath(), "ezodata-showcase");
Directory.CreateDirectory(dataDir);

var services = new[] { "simplecrud", "efcore", "stock" };
var seedWatch = Stopwatch.StartNew();
foreach (var service in services) Seed.Create(Path.Combine(dataDir, $"{service}.db"));
Console.WriteLine($"Seeded 3 identical databases in {seedWatch.ElapsedMilliseconds} ms ({dataDir})");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = Environments.Development });
builder.WebHost.UseUrls(baseUrl);
builder.Logging.ClearProviders().AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("EzOdata.Embedded.EzOdataBuilder", LogLevel.Error); // the dev no-auth banner is expected here

builder.Services.AddSingleton<Outbox>();
builder.Services.AddAuthorization();
builder.Services.AddAuthentication("Demo").AddScheme<AuthenticationSchemeOptions, DemoAuthHandler>("Demo", _ => { });

builder.Services.AddEzOData(ez =>
{
    foreach (var service in services)
    {
        ez.AddService(service, s => s.UseSqlite(Path.Combine(dataDir, $"{service}.db")).Options(o => o.DefaultPageSize(25).MaxPageSize(500)));
    }

    ez.AddRole("admin", r => r.Allow(null, "*", Verb.All));
    ez.AddRole("rep", r => r
        .Allow(null, "customers", Verb.All, rowFilter: "owner_id eq @identity.sub")
        .Allow(null, "orders", Verb.Get)
        .Allow(null, "order_lines", Verb.Get)
        .Allow(null, "products", Verb.Get));
    ez.AddRole("viewer", r => r.Allow(null, "*", Verb.Get, fieldRules: [new FieldRule("email", FieldAction.Mask, "***")]));
    ez.UseHostRoles();
    ez.AllowAnonymousInDevelopment(); // no X-Demo-User header → full dev access, so Swagger works immediately
});

builder.Services.ExtendEzOData(x =>
{
    x.Service("simplecrud", s => Tables(s.UseSimpleCrud()));
    x.Service("efcore", s => Tables(s.UseEfCore<ShopContext>()));
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapEzOData("/api/odata");
app.MapEzODataRest("/api/rest");
app.UseEzODataSwaggerUI(o => o.DocumentTitle = "ez-odata showcase");
app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/demo/outbox", (Outbox outbox) => outbox.Messages);

await app.StartAsync();
Console.WriteLine($"Listening on {baseUrl}  ·  Swagger UI: {baseUrl}/swagger");

if (Has("--open"))
{
    try { Process.Start(new ProcessStartInfo($"{baseUrl}/swagger") { UseShellExecute = true }); }
    catch { Console.WriteLine($"(Could not open a browser; visit {baseUrl}/swagger)"); }
}

var exitCode = 0;
if (Has("--tour"))
{
    exitCode = await new Tour(baseUrl, app.Services.GetRequiredService<Outbox>()).RunAsync() ? 0 : 1;
}

if (Has("--exit"))
{
    await app.StopAsync();
    return exitCode;
}

Console.WriteLine();
Console.WriteLine($"  Explore it: {baseUrl}/swagger   (Ctrl+C to stop)");
Console.WriteLine("  Try a role: add header  X-Demo-User: rep-1   (or X-Demo-Roles: viewer)");
await app.WaitForShutdownAsync();
return exitCode;

static EzServiceExtensionBuilder Tables(EzServiceExtensionBuilder s) => s
    .Table<Customer, CustomerHandler>()
    .Table<Order, OrderHandler>()
    .Table<OrderLine>(t => t.BeforeInsert((line, ctx) => { if (line.Qty <= 0) ctx.Reject("qty must be positive."); }));

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Has(string name) => args.Contains(name);

/// <summary>
/// Demo sign-in: <c>X-Demo-User</c> (and optional <c>X-Demo-Roles</c>, default <c>rep</c>) authenticates the request
/// with those roles. Without the header the request is anonymous and gets ez-odata's development bypass.
/// </summary>
internal sealed class DemoAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Demo-User"].ToString();
        if (string.IsNullOrWhiteSpace(user)) return Task.FromResult(AuthenticateResult.NoResult());

        var roles = Request.Headers["X-Demo-Roles"].ToString();
        var claims = new List<Claim> { new("sub", user) };
        claims.AddRange((string.IsNullOrWhiteSpace(roles) ? "rep" : roles)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => new Claim(ClaimTypes.Role, r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Demo")), "Demo")));
    }
}
