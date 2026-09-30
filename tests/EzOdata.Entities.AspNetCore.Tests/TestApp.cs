using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;
using EzOdata.Embedded;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EzOdata.Entities.AspNetCore.Tests;

/// <summary>
/// A host app exactly as the ez-odata README shows it (AddEzOData + MapEzOData), plus ExtendEzOData.
/// Identity comes from X-User / X-Roles headers.
/// </summary>
public static class TestApp
{
    public static async Task<IHost> StartAsync(Action<EzOdataBuilder> ez, Action<IServiceCollection>? services = null)
    {
        return await new HostBuilder()
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthorization();
                    s.AddAuthentication("Headers").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Headers", _ => { });
                    s.AddEzOData(ez);
                    services?.Invoke(s);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e =>
                    {
                        e.MapEzOData("/api/odata");
                        e.MapEzODataRest("/api/rest");
                    });
                });
            })
            .StartAsync();
    }

    public static HttpClient As(this IHost host, string user, params string[] roles)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-User", user);
        client.DefaultRequestHeaders.Add("X-Roles", string.Join(",", roles));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
        : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("sub", user.ToString()) };
            claims.AddRange(Request.Headers["X-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => new Claim(ClaimTypes.Role, r)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Headers"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Headers")));
        }
    }
}
