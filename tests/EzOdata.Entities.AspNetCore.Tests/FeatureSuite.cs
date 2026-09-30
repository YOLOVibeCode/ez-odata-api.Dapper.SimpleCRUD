using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using EzOdata.Core.Policy;
using EzOdata.Entities.AspNetCore;
using EzOdata.SimpleCrud.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EzOdata.Entities.AspNetCore.Tests;

public abstract class FeatureSuite : IAsyncLifetime
{
    private readonly TestDatabase _db = TestDatabase.NewSqlite();
    protected IHost Host = null!;

    protected abstract IEngineUnderTest Engine { get; }
    protected abstract bool PropertyNames { get; }

    public async Task InitializeAsync()
    {
        await _db.ExecuteAsync(Schema.Crm("sqlite") + Schema.Seed + Schema.OrderLines);

        var owner = PropertyNames ? "OwnerId" : "owner_id";
        Host = await TestApp.StartAsync(
            ez =>
            {
                ez.AddService("crm", s => s.UseSqlite(_db.FilePath!));
                ez.AddRole("admin", r => r.Allow("crm", "*", Verb.All));
                ez.AddRole("rep", r => r.Allow("crm", "customers", Verb.All, rowFilter: $"{owner} eq @identity.sub"));
                ez.UseHostRoles();
            },
            services =>
            {
                services.AddSingleton<HookJournal>();
                services.ExtendEzOData(x => x.Service("crm", crm =>
                {
                    Engine.Configure(crm);
                    crm.Table<Customer, CustomerHandler>()
                       .Table<OrderLine>(t => t.BeforeInsert((line, ctx) => { if (line.Qty <= 0) ctx.Reject("Quantity must be positive."); }));
                    if (PropertyNames) crm.UsePropertyNames();
                }));
            });
    }

    public async Task DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_db.FilePath!);
    }

    protected HttpClient Admin => Host.As("root", "admin");

    protected async Task<T> Scalar<T>(string sql)
    {
        await using var connection = _db.Connect();
        return (await connection.ExecuteScalarAsync<T>(sql))!;
    }

    protected static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    protected static readonly JsonSerializerOptions Exact = new();

    protected static HttpRequestMessage Patch(string url, object body) => new(HttpMethod.Patch, url) { Content = JsonContent.Create(body, options: Exact) };
}

public abstract class CompositeKeySuite : FeatureSuite
{
    protected override bool PropertyNames => false;

    [Fact]
    public async Task Composite_keys_work_end_to_end()
    {
        var created = await Json(await Admin.PostAsJsonAsync("/api/odata/crm/order_lines", new { order_id = 1, line_no = 2, product = "Gadget", qty = 3 }));
        Assert.Equal(2, created.GetProperty("line_no").GetInt32());

        var line = await Json(await Admin.GetAsync("/api/odata/crm/order_lines(order_id=1,line_no=2)"));
        Assert.Equal("Gadget", line.GetProperty("product").GetString());

        await Json(await Admin.SendAsync(Patch("/api/odata/crm/order_lines(order_id=1,line_no=2)", new { qty = 7 })));
        Assert.Equal(7L, await Scalar<long>("SELECT qty FROM order_lines WHERE order_id = 1 AND line_no = 2"));
        Assert.Equal(2L, await Scalar<long>("SELECT qty FROM order_lines WHERE order_id = 1 AND line_no = 1"));

        var duplicate = await Admin.PostAsJsonAsync("/api/odata/crm/order_lines", new { order_id = 1, line_no = 2, product = "Again", qty = 1 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var rejected = await Admin.PostAsJsonAsync("/api/odata/crm/order_lines", new { order_id = 1, line_no = 3, product = "Zero", qty = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync("/api/odata/crm/order_lines(order_id=1,line_no=2)")).StatusCode);
        Assert.Equal(1L, await Scalar<long>("SELECT COUNT(*) FROM order_lines"));
    }
}

public abstract class PropertyNameSuite : FeatureSuite
{
    protected override bool PropertyNames => true;

    [Fact]
    public async Task Entity_property_names_become_the_api_contract()
    {
        var page = await Json(await Admin.GetAsync("/api/odata/crm/customers?$filter=Name eq 'Ada'&$orderby=Name desc"));
        var ada = page.GetProperty("value")[0];
        Assert.Equal("Ada", ada.GetProperty("Name").GetString());
        Assert.Equal("ada@example.com", ada.GetProperty("Email").GetString());
        Assert.True(ada.TryGetProperty("IsDeleted", out _));
        Assert.True(ada.TryGetProperty("notes", out _));
        Assert.False(ada.TryGetProperty("full_name", out _));

        var metadata = await Admin.GetStringAsync("/api/odata/crm/$metadata");
        Assert.Contains("Property Name=\"Name\"", metadata);
        Assert.DoesNotContain("Property Name=\"full_name\"", metadata);

        var created = await Json(await Admin.PostAsJsonAsync("/api/odata/crm/customers", new { Name = "Cy", Email = "cy@example.com", Country = "us" }, Exact));
        Assert.Equal("US", created.GetProperty("Country").GetString());
        Assert.Equal("root", created.GetProperty("CreatedBy").GetString());

        await Json(await Admin.SendAsync(Patch("/api/odata/crm/customers(1)", new { Email = "ada@new.example" })));
        Assert.Equal("ada@new.example", await Scalar<string>("SELECT email FROM customers WHERE id = 1"));

        var line = await Json(await Admin.GetAsync("/api/odata/crm/order_lines(OrderId=1,LineNo=1)"));
        Assert.Equal("Widget", line.GetProperty("Product").GetString());
    }

    [Fact]
    public async Task Row_filters_and_expand_follow_the_renamed_columns()
    {
        var rep = await Json(await Host.As("u1", "rep").GetAsync("/api/odata/crm/customers?$count=true"));
        Assert.Equal(1, rep.GetProperty("@odata.count").GetInt32());

        var expanded = await Json(await Admin.GetAsync("/api/odata/crm/customers?$filter=Id eq 1&$expand=orders"));
        Assert.Equal(1, expanded.GetProperty("value")[0].GetProperty("orders").GetArrayLength());
    }
}

public sealed class SimpleCrud_CompositeKeyTests : CompositeKeySuite
{
    protected override IEngineUnderTest Engine => SimpleCrudEngineUnderTest.Instance;
}

public sealed class EfCore_CompositeKeyTests : CompositeKeySuite
{
    protected override IEngineUnderTest Engine => EfCoreEngineUnderTest.Instance;
}

public sealed class SimpleCrud_PropertyNameTests : PropertyNameSuite
{
    protected override IEngineUnderTest Engine => SimpleCrudEngineUnderTest.Instance;
}

public sealed class EfCore_PropertyNameTests : PropertyNameSuite
{
    protected override IEngineUnderTest Engine => EfCoreEngineUnderTest.Instance;
}
