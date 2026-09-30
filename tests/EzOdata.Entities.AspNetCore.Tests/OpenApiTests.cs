using System.Net;
using System.Text.Json;
using EzOdata.Core.Policy;
using EzOdata.Entities.AspNetCore;
using EzOdata.SimpleCrud.AspNetCore;
using EzOdata.SimpleCrud.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EzOdata.Entities.AspNetCore.Tests;

/// <summary>
/// ez-odata-api 1.0.6: both OpenAPI documents root <c>servers[0].url</c> at the service
/// so Swagger UI "Try it out" does not 404.
/// </summary>
public sealed class OpenApiTests
{
    [Fact]
    public async Task Openapi_server_urls_point_at_the_service()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync(Schema.Crm("sqlite") + Schema.Seed);

        using var host = await TestApp.StartAsync(
            ez =>
            {
                ez.AddService("crm", s => s.UseSqlite(db.FilePath!));
                ez.AddRole("admin", r => r.Allow("crm", "*", Verb.All));
                ez.UseHostRoles();
            },
            s =>
            {
                s.AddSingleton<HookJournal>();
                s.ExtendEzOData(x => x.Service("crm", crm => crm.UseSimpleCrud().Table<Customer, CustomerHandler>().Table<Order>()));
            });

        var client = host.As("root", "admin");
        foreach (var (spec, path) in new[]
                 {
                     ("/api/odata/crm/openapi.json", "/customers"),
                     ("/api/rest/crm/openapi.json", "/_table/customers"),
                 })
        {
            var json = await client.GetStringAsync(spec);
            var doc = JsonDocument.Parse(json).RootElement;
            var server = doc.GetProperty("servers")[0].GetProperty("url").GetString()!;
            Assert.EndsWith(spec.Replace("/openapi.json", ""), server.TrimEnd('/'));
            Assert.True(doc.GetProperty("paths").TryGetProperty(path, out _), $"{spec} has no {path}");

            var response = await client.GetAsync(new Uri(server.TrimEnd('/') + path).PathAndQuery);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
