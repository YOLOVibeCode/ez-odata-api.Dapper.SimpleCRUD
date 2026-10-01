using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Policy;
using EzOdata.Embedded;
using EzOdata.Entities.AspNetCore;
using EzOdata.EntityFrameworkCore.AspNetCore;
using EzOdata.SimpleCrud.AspNetCore;
using EzOdata.SimpleCrud.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EzOdata.Entities.Benchmarks;

/// <summary>One TestServer + database for a (engine, dialect) pair.</summary>
public sealed class BenchSession : IAsyncDisposable
{
    private BenchSession(string engine, TestDatabase db, IHost host)
    {
        Engine = engine;
        Database = db;
        Host = host;
        Client = host.GetTestClient();
        Client.DefaultRequestHeaders.Add("X-User", "bench");
        Client.DefaultRequestHeaders.Add("X-Roles", "admin");
        Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Engine { get; }
    public TestDatabase Database { get; }
    public IHost Host { get; }
    public HttpClient Client { get; }
    public string Root => $"/api/odata/{Database.Kind}";

    public static async Task<BenchSession> StartAsync(string engine, TestDatabase db)
    {
        await db.ExecuteAsync(BenchSchema.Create(db.Kind) + BenchSchema.Seed);
        var host = await new HostBuilder()
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole(o => o.SingleLine = true))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthorization();
                    s.AddAuthentication("Headers").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Headers", _ => { });
                    s.AddEzOData(ez =>
                    {
                        ez.AddService(db.Kind, svc => ConfigureConnector(svc, db));
                        ez.AddRole("admin", r => r.Allow(db.Kind, "*", Verb.All));
                        ez.UseHostRoles();
                    });
                    if (engine != "stock")
                    {
                        EzODataEntityServiceCollectionExtensions.ExtendEzOData(s, x => x.Service(db.Kind, svc =>
                        {
                            if (engine == "simplecrud") svc.UseSimpleCrud();
                            else if (engine == "efcore") svc.UseEfCore<BenchDbContext>();
                            else throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown engine.");
                            svc.Table<Customer>()
                               .Table<Order>(t => t.AfterInsert(async (o, ctx) =>
                                   await ctx.Data.InsertAsync(new AuditLog { Entity = "order", EntityId = o.Id, Action = "insert" })));
                        }));
                    }
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapEzOData("/api/odata"));
                });
            })
            .StartAsync();
        var session = new BenchSession(engine, db, host);

        // ez-odata reads the schema once at startup and only logs a failure; the service would then answer
        // 404 "Unknown service" forever and every benchmark row would be timing an error. Fail loudly instead.
        var probe = await session.Client.GetAsync($"{session.Root}/customers?$top=1");
        if (!probe.IsSuccessStatusCode)
        {
            var body = await probe.Content.ReadAsStringAsync();
            await session.DisposeAsync();
            throw new InvalidOperationException($"{engine}/{db.Kind} is not serving: {(int)probe.StatusCode} {body}");
        }

        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        Client.Dispose();
        if (Database.FilePath is { } path && File.Exists(path))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static void ConfigureConnector(EmbeddedServiceBuilder svc, TestDatabase db)
    {
        switch (db.Kind)
        {
            case "postgresql":
                svc.UsePostgreSql(Spec(db, "disable"));
                break;
            case "mysql":
                svc.UseMySql(Spec(db, "prefer"));
                break;
            case "sqlserver":
                svc.UseSqlServer(Spec(db, "require") with { Tls = new TlsSpec { Mode = "require", AllowInvalid = true } });
                break;
            default:
                svc.UseSqlite(db.FilePath!);
                break;
        }
    }

    private static ConnectionSpec Spec(TestDatabase db, string tls) => new()
    {
        Host = db.Host,
        Port = db.Port,
        Database = db.Database,
        Username = db.Username,
        Password = db.Password,
        Tls = new TlsSpec { Mode = tls },
    };

    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
        : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new("sub", Request.Headers["X-User"].ToString()) };
            claims.AddRange(Request.Headers["X-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => new Claim(ClaimTypes.Role, r)));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Headers")), "Headers")));
        }
    }
}
