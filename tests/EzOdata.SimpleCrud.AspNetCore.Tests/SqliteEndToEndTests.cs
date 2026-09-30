using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using EzOdata.Core.Policy;
using EzOdata.SimpleCrud.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EzOdata.SimpleCrud.AspNetCore.Tests;

/// <summary>
/// End to end through ez-odata's real HTTP pipeline (AddEzOData + MapEzOData, unmodified) with
/// ExtendEzOData layered on. Every test gets a fresh SQLite database and host.
/// </summary>
public sealed class SqliteEndToEndTests : IAsyncLifetime
{
    private readonly TestDatabase _db = TestDatabase.NewSqlite();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await _db.ExecuteAsync(Schema.Crm("sqlite") + Schema.Seed);
        _host = await TestApp.StartAsync(
            ez =>
            {
                ez.AddService("crm", s => s.UseSqlite(_db.FilePath!));
                ez.AddRole("admin", r => r.Allow("crm", "*", Verb.All));
                ez.AddRole("rep", r => r
                    .Allow("crm", "customers", Verb.All, rowFilter: "owner_id eq @identity.sub")
                    .Allow("crm", "orders", Verb.Get | Verb.Post)
                    .Allow("crm", "products", Verb.Get));
                ez.UseHostRoles();
            },
            services =>
            {
                services.AddSingleton<HookJournal>();
                services.ExtendEzOData(x => x.Service("crm", crm => crm
                    .Table<Customer, CustomerHandler>()
                    .Table<Order>(t => t
                        .BeforeInsert((o, ctx) => { if (o.Total <= 0) ctx.Reject("Order total must be positive."); })
                        .AfterInsert(async (o, ctx) =>
                        {
                            await ctx.Crud.InsertAsync(new AuditLog { Entity = "order", EntityId = o.Id, Action = "insert", Actor = ctx.UserId });
                            var journal = ctx.Services.GetRequiredService<HookJournal>();
                            ctx.OnCommitted(() => { lock (journal) journal.Calls.Add($"committed:order:{o.Total}"); });
                            if (o.Total == 77) ctx.OnCommitted(() => throw new InvalidOperationException("mail server down"));
                            if (o.Total > 1000) ctx.Reject("Orders over 1000 need approval.");
                        })
                        .AfterRead((row, _) => { if (row["total"] is double d) row.Set("total", Math.Round(d, 1)); }))));
            });
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_db.FilePath!);
    }

    private HttpClient Admin => _host.As("root", "admin");
    private HttpClient Rep(string user = "u1") => _host.As(user, "rep");

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = _db.Connect();
        return (await connection.ExecuteScalarAsync<T>(sql))!;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static HttpRequestMessage Patch(string url, object body) =>
        new(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };

    // ---- instant API is untouched --------------------------------------------------------------

    [Fact]
    public async Task Tables_without_an_entity_keep_the_stock_instant_api()
    {
        var list = await Json(await Admin.GetAsync("/api/odata/crm/products"));
        Assert.Equal("Widget", list.GetProperty("value")[0].GetProperty("name").GetString());

        var created = await Admin.PostAsJsonAsync("/api/odata/crm/products", new { name = "Gizmo" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var metadata = await Admin.GetStringAsync("/api/odata/crm/$metadata");
        Assert.Contains("EntityType Name=\"customers\"", metadata);
        Assert.Contains("EntityType Name=\"products\"", metadata);
    }

    // ---- reads ---------------------------------------------------------------------------------

    [Fact]
    public async Task Reads_apply_role_row_filters_and_handler_filters_together()
    {
        var rep = await Json(await Rep().GetAsync("/api/odata/crm/customers?$count=true"));
        Assert.Equal(1, rep.GetProperty("@odata.count").GetInt32());
        Assert.Equal("Ada", rep.GetProperty("value")[0].GetProperty("full_name").GetString());

        var admin = await Json(await Admin.GetAsync("/api/odata/crm/customers?$count=true"));
        Assert.Equal(2, admin.GetProperty("@odata.count").GetInt32());
    }

    [Fact]
    public async Task Expand_and_after_read_hooks_work_on_extended_tables()
    {
        var body = await Json(await Admin.GetAsync("/api/odata/crm/customers?$filter=id eq 1&$expand=orders"));
        var orders = body.GetProperty("value")[0].GetProperty("orders");
        Assert.Equal(10.3, orders[0].GetProperty("total").GetDouble()); // rounded by the Order AfterRead hook
    }

    // ---- inserts -------------------------------------------------------------------------------

    [Fact]
    public async Task Insert_runs_through_SimpleCRUD_and_the_handler()
    {
        var response = await Rep().PostAsJsonAsync("/api/odata/crm/customers",
            new { full_name = "Cy", email = "cy@example.com", country = "us", owner_id = "u1" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal("u1", body.GetProperty("created_by").GetString());   // stamped by BeforeInsert
        Assert.Equal("US", body.GetProperty("country").GetString());      // normalized by BeforeInsert
        Assert.Equal(1L, await Scalar<long>($"SELECT COUNT(*) FROM audit_log WHERE entity_id = {id} AND action = 'insert'"));
        Assert.Contains("crm:BeforeInsert:Cy", _host.Services.GetRequiredService<HookJournal>().Calls);
    }

    [Fact]
    public async Task Hook_rejection_is_a_400_and_nothing_is_written()
    {
        var response = await Admin.PostAsJsonAsync("/api/odata/crm/customers", new { full_name = " ", country = "US" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Customer name is required.", await response.Content.ReadAsStringAsync());
        Assert.Equal(2L, await Scalar<long>("SELECT COUNT(*) FROM customers"));
        Assert.Equal(0L, await Scalar<long>("SELECT COUNT(*) FROM audit_log"));
    }

    [Fact]
    public async Task Side_writes_in_hooks_roll_back_with_the_api_write()
    {
        var response = await Admin.PostAsJsonAsync("/api/odata/crm/orders", new { customer_id = 1, total = 5000.0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("need approval", await response.Content.ReadAsStringAsync());
        Assert.Equal(1L, await Scalar<long>("SELECT COUNT(*) FROM orders"));     // order rolled back
        Assert.Equal(0L, await Scalar<long>("SELECT COUNT(*) FROM audit_log"));  // audit written in AfterInsert rolled back too

        var ok = await Admin.PostAsJsonAsync("/api/odata/crm/orders", new { customer_id = 1, total = 99.0 });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(1L, await Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity = 'order'"));
    }

    [Fact]
    public async Task OnCommitted_runs_once_after_commit_and_never_after_rollback()
    {
        var journal = _host.Services.GetRequiredService<HookJournal>();

        var rejected = await Admin.PostAsJsonAsync("/api/odata/crm/orders", new { customer_id = 1, total = 5000.0 });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.DoesNotContain("committed:order:5000", journal.Calls);  // registered, then rolled back: never runs

        var ok = await Admin.PostAsJsonAsync("/api/odata/crm/orders", new { customer_id = 1, total = 42.0 });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Single(journal.Calls, c => c == "committed:order:42");

        // A failing callback is logged; the committed write and its response stand.
        var flaky = await Admin.PostAsJsonAsync("/api/odata/crm/orders", new { customer_id = 1, total = 77.0 });
        Assert.Equal(HttpStatusCode.Created, flaky.StatusCode);
        Assert.Equal(1L, await Scalar<long>("SELECT COUNT(*) FROM orders WHERE total = 77"));
        Assert.Contains("committed:order:77", journal.Calls);
    }

    [Fact]
    public async Task Insert_outside_the_callers_row_filter_is_403_and_rolled_back()
    {
        var response = await Rep("u1").PostAsJsonAsync("/api/odata/crm/customers",
            new { full_name = "Sneaky", email = "s@example.com", country = "US", owner_id = "u2" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await Scalar<long>("SELECT COUNT(*) FROM customers WHERE full_name = 'Sneaky'"));
        Assert.Equal(0L, await Scalar<long>("SELECT COUNT(*) FROM audit_log"));
    }

    [Fact]
    public async Task Database_constraint_violations_map_to_the_engines_error_codes()
    {
        var response = await Admin.PostAsJsonAsync("/api/odata/crm/customers",
            new { full_name = "Ada 2", email = "ada@example.com", country = "US" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Conflict.UniqueViolation", await response.Content.ReadAsStringAsync());
    }

    // ---- updates -------------------------------------------------------------------------------

    [Fact]
    public async Task Patch_changes_only_what_was_sent()
    {
        var response = await Admin.SendAsync(Patch("/api/odata/crm/customers(1)", new { email = "ada@new.example" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ada@new.example", await Scalar<string>("SELECT email FROM customers WHERE id = 1"));
        Assert.Equal("Ada", await Scalar<string>("SELECT full_name FROM customers WHERE id = 1"));
        Assert.Equal("US", await Scalar<string>("SELECT country FROM customers WHERE id = 1"));
    }

    [Fact]
    public async Task Handlers_can_forbid_an_update_based_on_the_original_row()
    {
        var rep = await Rep("u1").SendAsync(Patch("/api/odata/crm/customers(1)", new { country = "EU" }));
        Assert.Equal(HttpStatusCode.Forbidden, rep.StatusCode);
        Assert.Contains("Only admins can move", await rep.Content.ReadAsStringAsync());
        Assert.Equal("US", await Scalar<string>("SELECT country FROM customers WHERE id = 1"));

        var admin = await Admin.SendAsync(Patch("/api/odata/crm/customers(1)", new { country = "EU" }));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task Columns_the_entity_does_not_write_are_read_only_in_the_api()
    {
        var notes = await Admin.PostAsJsonAsync("/api/odata/crm/customers", new { full_name = "N", country = "US", notes = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, notes.StatusCode); // unmapped column

        var createdBy = await Admin.SendAsync(Patch("/api/odata/crm/customers(1)", new { created_by = "mallory" }));
        Assert.Equal(HttpStatusCode.BadRequest, createdBy.StatusCode); // [IgnoreUpdate]
        Assert.Contains("cannot be changed", await createdBy.Content.ReadAsStringAsync());

        // PUT: omitted [IgnoreUpdate] columns are fine, but a supplied value is rejected, never silently dropped.
        var put = new { full_name = "Ada", email = "ada@example.com", country = "US", owner_id = "u1", is_deleted = 0 };
        Assert.Equal(HttpStatusCode.OK, (await Admin.PutAsJsonAsync("/api/odata/crm/customers(1)", put)).StatusCode);
        var putCreatedBy = await Admin.PutAsJsonAsync("/api/odata/crm/customers(1)", new
            { full_name = "Ada", email = "ada@example.com", country = "US", owner_id = "u1", is_deleted = 0, created_by = "mallory" });
        Assert.Equal(HttpStatusCode.BadRequest, putCreatedBy.StatusCode);
        Assert.Null(await Scalar<string?>("SELECT created_by FROM customers WHERE id = 1"));
    }

    [Fact]
    public async Task Row_filters_protect_updates_and_deletes()
    {
        var patch = await Rep("u1").SendAsync(Patch("/api/odata/crm/customers(2)", new { email = "hijack@example.com" }));
        var delete = await Rep("u1").DeleteAsync("/api/odata/crm/customers(2)");

        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("bob@example.com", await Scalar<string>("SELECT email FROM customers WHERE id = 2"));
        Assert.Equal(0L, await Scalar<long>("SELECT is_deleted FROM customers WHERE id = 2"));
    }

    // ---- delete override -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_can_be_overridden_as_a_soft_delete()
    {
        var delete = await Admin.DeleteAsync("/api/odata/crm/customers(2)");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(1L, await Scalar<long>("SELECT is_deleted FROM customers WHERE id = 2"));   // row kept
        Assert.Equal(1L, await Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'soft-delete' AND entity_id = 2"));

        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync("/api/odata/crm/customers(2)")).StatusCode);
        var list = await Json(await Admin.GetAsync("/api/odata/crm/customers?$count=true"));
        Assert.Equal(1, list.GetProperty("@odata.count").GetInt32());

        // Invisible rows are not writable either.
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.SendAsync(Patch("/api/odata/crm/customers(2)", new { email = "z@example.com" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync("/api/odata/crm/customers(2)")).StatusCode);
    }
}

/// <summary>Configuration errors surface at startup, with the exact problem.</summary>
public sealed class StartupValidationTests
{
    [Dapper.Table("customers")]
    public class DriftedCustomer
    {
        [Dapper.Key, Dapper.Column("id")] public int Id { get; set; }
        [Dapper.Column("nickname")] public string? Nickname { get; set; }
    }

    [Dapper.Table("no_such_table")]
    public class Ghost
    {
        public int Id { get; set; }
    }

    [Fact]
    public async Task An_entity_that_does_not_match_the_database_stops_startup()
    {
        var db = TestDatabase.NewSqlite();
        await db.ExecuteAsync(Schema.Crm("sqlite"));

        var ex = await Assert.ThrowsAsync<EzExtensionConfigurationException>(() => TestApp.StartAsync(
            ez => ez.AddService("crm", s => s.UseSqlite(db.FilePath!)),
            s => s.ExtendEzOData(x => x.Service("crm", crm => crm.Table<DriftedCustomer>().Table<Ghost>()))));

        Assert.Contains(ex.Problems, p => p.Contains("\"nickname\"", StringComparison.Ordinal));
        Assert.Contains(ex.Problems, p => p.Contains("\"no_such_table\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtendEzOData_requires_AddEzOData_first()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().ExtendEzOData(x => x.Service("crm", crm => crm.Table<Customer>())));
        Assert.Contains("AddEzOData", ex.Message);
    }
}
