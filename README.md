# ez-odata-api + Dapper.SimpleCRUD

[![CI](https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD/actions/workflows/ci.yml/badge.svg)](https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD/actions/workflows/ci.yml)
[![EzOdata.SimpleCrud](https://img.shields.io/nuget/v/EzOdata.SimpleCrud?label=EzOdata.SimpleCrud)](https://www.nuget.org/packages/EzOdata.SimpleCrud)
[![EzOdata.SimpleCrud.AspNetCore](https://img.shields.io/nuget/v/EzOdata.SimpleCrud.AspNetCore?label=EzOdata.SimpleCrud.AspNetCore)](https://www.nuget.org/packages/EzOdata.SimpleCrud.AspNetCore)
[![EzOdata.Entities.AspNetCore](https://img.shields.io/nuget/v/EzOdata.Entities.AspNetCore?label=EzOdata.Entities.AspNetCore)](https://www.nuget.org/packages/EzOdata.Entities.AspNetCore)
[![EzOdata.EntityFrameworkCore.AspNetCore](https://img.shields.io/nuget/v/EzOdata.EntityFrameworkCore.AspNetCore?label=EzOdata.EntityFrameworkCore.AspNetCore)](https://www.nuget.org/packages/EzOdata.EntityFrameworkCore.AspNetCore)

```bash
dotnet add package EzOdata.SimpleCrud                        # multi-dialect SimpleCRUD
dotnet add package EzOdata.SimpleCrud.AspNetCore             # instant API + SimpleCRUD engine
dotnet add package EzOdata.EntityFrameworkCore.AspNetCore    # same API, EF Core engine
```

This project adds interchangeable write engines to [ez-odata-api](https://github.com/YOLOVibeCode/ez-odata-api)
without changing ez-odata-api at all:

- **`EzOdata.SimpleCrud`**: an instance-based facade over [Dapper.SimpleCRUD](https://www.nuget.org/packages/Dapper.SimpleCRUD).
  Each dialect gets its own isolated SimpleCRUD engine, created on first use, so several databases
  and your existing SimpleCRUD code can run in one process.
- **`EzOdata.Entities.AspNetCore`**: the DreamFactory model on top of ez-odata. Instant API for every
  table; take over individual tables with entities, typed hooks, and `ctx.Data`.
- **`EzOdata.SimpleCrud.AspNetCore`** / **`EzOdata.EntityFrameworkCore.AspNetCore`**: pick the write
  engine with one line per service (`.UseSimpleCrud()` or `.UseEfCore<TContext>()`).

ez-odata-api is used unmodified. Its `AddEzOData` / `MapEzOData` calls stay exactly as documented.
Design and requirements (each mapped to a test) are in [`specification.md`](specification.md).

## Try it in one command

```bash
git clone https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD && cd ez-odata-api.Dapper.SimpleCRUD
./try.sh          # Windows: try.cmd
```

You need nothing but `git` and `curl`. If the .NET 10 SDK is missing, `try.sh` downloads it into `./.dotnet`
(no admin rights, nothing installed system-wide); packages come from nuget.org. It then starts
[the showcase](samples/EzOdata.Showcase): a shop database (500 customers, orders, composite-key order
lines) served three ways on one port, by stock ez-odata, by the SimpleCRUD engine and by the EF Core
engine. Swagger UI opens at `http://localhost:5199/swagger`, and a narrated tour runs every feature
against the live API and checks the result:

| Tour section | What it proves |
|---|---|
| Discover | Swagger UI, one OpenAPI document per service and dialect, the service document, CSDL `$metadata` |
| Query | `$filter` / `$orderby` / `$top` / `$select`, `$count`, server paging, string functions, `in`, `$expand` with nested options, navigation filters, `any()`, `$apply` aggregation, the REST dialect, composite keys, a clean 400 on a bad property |
| Writes (on both engines) | validation (400), a unique violation (409), typed hooks, an audit row in the same transaction, `Reject()` rolling back a whole insert, soft delete, `OnCommitted` firing once, composite-key inserts |
| Security | a row filter per sales rep, 404 for rows outside it, 403 for forged owners, a handler rule, a masked field for viewers, read-only roles |
| Engines side by side | the three services timed on the same requests, interleaved |

```bash
./try.sh --exit        # run the tour and exit with its result (what CI runs on Linux, macOS and Windows)
./try.sh --port 8080   # another port; --no-open to skip the browser
./try.sh --benchmark   # the full benchmark below, then an HTML report
```

Swagger UI calls run as the anonymous developer (full access, Development only). To see the security
rules, send the showcase's demo headers:

```bash
curl -H 'X-Demo-User: rep-1' 'http://localhost:5199/api/odata/simplecrud/customers?$count=true&$top=3'   # 100 of 500: own rows only
curl -H 'X-Demo-User: v' -H 'X-Demo-Roles: viewer' 'http://localhost:5199/api/odata/efcore/customers?$top=3'  # emails masked
```

## Benchmarks: Dapper, Dapper.SimpleCRUD and EF Core

`./try.sh --benchmark` (or `./compare.sh --data-access`) measures two layers on SQLite, PostgreSQL, MySQL
and SQL Server (each in Docker, one at a time; SQLite only without Docker):

- **The libraries on their own:** hand-written Dapper, Dapper.SimpleCRUD (through this facade and through
  its own static API), EF Core tracked and `AsNoTracking`. Get by id, a filtered page, insert, update,
  insert + delete, and 100 inserts in a transaction, with BenchmarkDotNet. Every library must return the
  same rows before anything is timed.
- **Through the instant API:** stock ez-odata against the SimpleCRUD and EF Core engines, request by
  request over HTTP.

**[View the benchmark report](https://yolovibecode.github.io/ez-odata-api.Dapper.SimpleCRUD/benchmarks/report.html)**. How to run it, the method and the caveats are in [`docs/benchmarks`](docs/benchmarks/README.md). In short:

- On a database server the round trip decides: Get by id on PostgreSQL is 324 µs with hand-written Dapper,
  342 µs with SimpleCRUD and 358 µs with EF Core. In process (SQLite) the library shows: 5.1, 12.0 and 18.3 µs.
- EF Core batches bulk inserts: 100 rows in 2.6 ms on PostgreSQL, against 31–33 ms row by row.
- Running SimpleCRUD through this facade costs nothing measurable (−6% to +2%, inside the confidence intervals).
- Through the API, reads cost the same on every engine; writes add 0.3–0.6 ms with SimpleCRUD and 0.4–1.4 ms
  with EF Core.
- Dropped into a new project, the SimpleCRUD engine adds 0.5 MB and about 30 ms of cold start to stock ez-odata;
  the EF Core engine adds 19 MB and about 160 ms ([drop-in analysis](docs/benchmarks/drop-in.md)).
- The benchmark led to an upstream enhancement: caching SimpleCRUD's per-type property lists cuts `Get` by 24% and
  its allocations by 62% ([ericdc1/Dapper.SimpleCRUD#283](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/283)).

## Quick start

```csharp
// 1. Stock ez-odata: every table, instantly. This is the same code as the ez-odata README.
builder.Services.AddEzOData(ez =>
{
    ez.AddService("crm", s => s.UsePostgreSql(connection));
    ez.AddRole("rep", r => r.Allow("crm", "customers", Verb.All, rowFilter: "owner_id eq @identity.sub"));
    ez.UseHostRoles();
});

// 2. Take over selected tables. One line picks the write engine.
builder.Services.ExtendEzOData(x => x.Service("crm", crm => crm
    .UseSimpleCrud()                                           // or .UseEfCore<CrmDbContext>()
    .Table<Customer, CustomerHandler>()                        // a handler class, created with DI
    .Table<Order>(t => t                                       // or inline hooks
        .BeforeInsert((o, ctx) => { if (o.Total <= 0) ctx.Reject("Total must be positive."); }))));

app.MapEzOData("/api/odata");                                  // unchanged
app.MapEzODataRest("/api/rest");                               // ez-odata 1.0.6: OpenAPI servers[0] includes prefix + service
```

The entity is an ordinary SimpleCRUD class. Existing classes work as they are:

```csharp
[Table("customers")]
public class Customer
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("full_name")] public string Name { get; set; } = "";
    [Column("created_by"), IgnoreUpdate] public string? CreatedBy { get; set; }
    [Column("is_deleted")] public bool IsDeleted { get; set; }
}

public sealed class CustomerHandler(IClock clock) : EzTableHandler<Customer>
{
    public override Task<QueryRequest> BeforeReadAsync(QueryRequest q, EzHookContext ctx) =>
        Task.FromResult(q.Where(EzFilter.Eq(ctx.Column(nameof(Customer.IsDeleted)), false)));

    public override Task BeforeInsertAsync(Customer c, EzHookContext ctx)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) ctx.Reject("Name is required.");   // 400, rolled back
        c.CreatedBy = ctx.UserId;
        return Task.CompletedTask;
    }

    public override Task AfterInsertAsync(Customer c, EzHookContext ctx) =>        // same transaction
        ctx.Data.InsertAsync(new AuditLog { Entity = "customer", EntityId = c.Id, Action = "insert" });

    public override Task<int> DeleteAsync(Customer c, EzHookContext ctx)          // soft delete
    {
        c.IsDeleted = true;
        return ctx.Data.UpdateAsync(c);
    }
}
```

A runnable version is in [`samples/EzOdata.SimpleCrud.Sample`](samples/EzOdata.SimpleCrud.Sample/Program.cs).

## Browse and query your database in Swagger UI

One line gives you a Swagger UI over every service:

```csharp
app.MapEzOData("/api/odata");
app.MapEzODataRest("/api/rest");   // optional REST dialect
app.UseEzODataSwaggerUI();         // Swagger UI at /swagger
```

Run your app and open **`https://localhost:<port>/swagger`**:

![ez-odata Swagger UI: one group per table, a query guide built from your columns](docs/images/swagger-ui.png)

1. **Pick a service and API** (top right). Every service you declared with `ez.AddService(...)` is listed,
   on every API you mapped (OData v4 and REST), with nothing to configure.
2. **Pick a table.** Operations are grouped by table, and the header shows query examples built from your
   real columns.
3. **Fill in the query options and click Execute.** "Try it out" is already on.

   | Option | Example |
   |---|---|
   | `$filter` | `contains(full_name,'a') and country eq 'US'` |
   | `$select` | `id,full_name` |
   | `$orderby` | `full_name desc` |
   | `$top` | `20` |
   | `$count` | `true` |
   | `$expand` | a related table, listed on the operation |

   You get the request URL, a reusable `curl` command, and the rows from the database.

`POST`, `PATCH` and `DELETE` work from the same page. On tables you've taken over, they go through your
write engine and hooks, so a hook's `Reject(...)` shows up as the `400` response.

**Options:**

```csharp
app.UseEzODataSwaggerUI(o =>
{
    o.RoutePrefix = "api-docs";                      // → /api-docs
    o.DocumentTitle = "CRM API";
    o.Services.Add("crm");                           // only these services (default: all)
    o.ConfigureSwaggerUI = ui => ui.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
});
```

**Authentication.** The page shows what the caller's role allows, because the OpenAPI documents are
authorized like the data.
- **Local development:** add `ez.AllowAnonymousInDevelopment()`.
- **Cookie authentication** works as-is.
- **Bearer tokens:** the browser's request for each document needs the token too. Add a request interceptor:

  ```csharp
  app.UseEzODataSwaggerUI(o => o.ConfigureSwaggerUI = ui => ui.UseRequestInterceptor(
      "(req) => { const t = sessionStorage.getItem('apiToken'); if (t) req.headers['Authorization'] = 'Bearer ' + t; return req; }"));
  ```

  Then run `sessionStorage.setItem('apiToken', '<token>')` in the browser console and reload.

Consider enabling it only outside production (`if (app.Environment.IsDevelopment()) app.UseEzODataSwaggerUI();`),
or behind your own authorization policy. The sample serves it: `dotnet run --project samples/EzOdata.SimpleCrud.Sample`,
then open http://localhost:5199/swagger.

## Extension levels

Every level is opt-in. With none of them configured, you have exactly stock ez-odata.

| Level | What you write | Effect |
|---|---|---|
| 0 | `AddEzOData(...)` | Instant OData/REST for every table (stock) |
| 1 | ez options (`ReadOnly`, `ExcludeTables`, page sizes) | Stock |
| 2 | `.Table<T>()` | A SimpleCRUD entity takes over the table's writes. Columns the entity doesn't write become read-only in the API |
| 3 | `.Table<T>(t => t.BeforeInsert(...))` | Inline pre/post hooks (DreamFactory's `pre_process` / `post_process`) |
| 4 | `.Table<T, THandler>()` | A handler class with constructor injection, resolved per request |
| 5 | Override `InsertAsync` / `UpdateAsync` / `DeleteAsync`, or `InsteadOf*` | Replace the operation itself (for example, soft delete) |
| 6 | Your own endpoints plus `ISimpleCrud` | Plain ASP.NET Core next to it |

Composite keys work the way SimpleCRUD models them (every part `[Key, Required]`), for example
`GET /order_lines(order_id=1,line_no=2)`. By default the API uses column names (`full_name`). To make
entity property names the API contract instead (`Name`), add `.UsePropertyNames()`:

```csharp
builder.Services.ExtendEzOData(x => x.Service("crm", crm => crm
    .UsePropertyNames()                  // GET /customers?$filter=Name eq 'Ada' → { "Name": "Ada", ... }
    .Table<Customer, CustomerHandler>()));
```

SQL keeps using column names. Row filters and field rules use the API names, and foreign keys across
tables are renamed consistently, so `$expand` keeps working.

## Write engines

Reads for taken-over tables stay on ez-odata's compiled SQL. Writes go through `IEntityEngine`:

```csharp
builder.Services.ExtendEzOData(x => x.Service("crm", crm => crm
    .UseEfCore<CrmDbContext>()          // or .UseSimpleCrud()
    .Table<Customer, CustomerHandler>()
    .Table<Order>()));
```

| | SimpleCRUD | EF Core |
|---|---|---|
| Package | `EzOdata.SimpleCrud.AspNetCore` | `EzOdata.EntityFrameworkCore.AspNetCore` |
| Registration | `.UseSimpleCrud()` | `.UseEfCore<TContext>()` |
| Mapping | `engine.Describe` (attributes / naming) | `DbContext.Model` |
| Writes | isolated `SimpleCrudSession` | `SaveChanges` on the shared connection + transaction |
| Escape hatch | `ctx.SimpleCrud()` / `ctx.Session()` | `ctx.DbContext<TContext>()` |
| Neutral store | `ctx.Data` | `ctx.Data` |

EF Core startup refuses a retrying execution strategy (it conflicts with the extension's transactions),
owned types, TPH/TPT/TPC, table splitting, and key types outside int/long/short/Guid/string. Value
converters log a warning: they apply on writes; reads still use raw SQL.

Migrating from 1.x: add `.UseSimpleCrud()`, `using EzOdata.Entities.AspNetCore`, and replace
`ctx.Crud` / `ctx.Session` with `ctx.Data` (or `ctx.SimpleCrud()`).

Compare both engines (tests + timings + a side-by-side HTML report):

```bash
./compare.sh                  # or double-click compare.command / compare.cmd
./compare.sh --data-access    # also Dapper vs SimpleCRUD vs EF Core on their own (BenchmarkDotNet)
./compare.sh --sqlite-only    # no Docker
./compare.sh --quick          # shorter runs
./demo-swagger.sh             # or demo-swagger.cmd: Swagger + read shop + warehouse + timings
```

Output lands in `artifacts/compare/<timestamp>/`: `tests/`, one folder and log per database, and
`report.html` / `report.md`.

`demo-swagger.sh` starts the sample (two SQLite databases), prints each OpenAPI `servers[0].url`,
reads `products` from shop and warehouse, then times both.

### Handler members

| Member | When it runs | Default |
|---|---|---|
| `BeforeReadAsync(query, ctx)` | Before reads, `$count` and `$expand`, and before the existence check of an update or delete | Returns the query unchanged |
| `AfterReadAsync(rows, ctx)` | After reads (not `$apply`) | Nothing |
| `BeforeInsertAsync` / `InsertAsync` / `AfterInsertAsync` | POST | `ctx.Data.InsertAsync` |
| `BeforeUpdateAsync(entity, original)` / `UpdateAsync` / `AfterUpdateAsync` | PATCH / PUT | `ctx.Data.UpdateAsync` (PATCH writes only changed columns) |
| `BeforeDeleteAsync` / `DeleteAsync` / `AfterDeleteAsync` | DELETE | `ctx.Data.DeleteAsync` |

`EzHookContext` provides:
- `User` / `UserId`
- request-scoped `Services`
- `Data`, the engine-neutral store on the write's own connection and transaction
- `Column(nameof(Prop))`, which maps an entity property to its API column name
- `Reject(msg)` (400) and `Forbid(msg)` (403); both roll back the write
- `OnCommitted(...)`, for side effects that run once after the write commits
- `Items`, shared between the Before and After hooks of one operation

## How it works

### SimpleCRUD's global state, and the fix

SimpleCRUD keeps its dialect, identifier quoting and SQL caches in **private static fields**, and
`SetDialect` never clears the caches. After first use, switching dialects emits mixed SQL, for example
SQL Server `[brackets]` inside PostgreSQL `LIMIT` paging. A 13-line upstream fix is drafted and
verified in [`docs/upstream`](docs/upstream/README.md). One copy of the assembly can therefore only ever speak one
dialect. `SimpleCrudEngines` gives each dialect (plus naming convention) its own copy of
`Dapper.SimpleCRUD.dll` in a dedicated `AssemblyLoadContext`, as a lazy, thread-safe singleton:

```
Default load context: your app, Dapper (shared, so type handlers apply), providers,
                      and the process-wide SimpleCRUD (your own code, never touched)
SimpleCRUD[PostgreSQL]  own statics, own caches ─┐
SimpleCRUD[SQLServer]   own statics, own caches ─┼─ created on first use, then reused
SimpleCRUD[SQLite]      own statics, own caches ─┘
```

This works without forking SimpleCRUD because SimpleCRUD recognizes `[Table]`, `[Key]`, `[Column]`
and its other attributes by **type name**, so your entity classes work with every copy.
Calls go through delegates compiled once per (operation, type). The measured overhead is
**1.06×** compared with calling SimpleCRUD directly (5,000 `Get<T>` calls), and exceptions surface
unwrapped.

```csharp
var pg = SimpleCrud.For(SimpleCRUD.Dialect.PostgreSQL).WithConnection(() => new NpgsqlConnection(cs)).Build();
await pg.InsertAsync(customer);

services.AddSimpleCrud("warehouse", SimpleCRUD.Dialect.SQLServer, _ => new SqlConnection(cs)); // keyed DI
services.AddSimpleCrud("local", SimpleCRUD.Dialect.SQLite, _ => new SqliteConnection(cs));
```

Per-engine naming conventions are supported, for example `SimpleCrudNaming.SnakeCase`. They replace
SimpleCRUD's global resolvers for that engine only.

On .NET Framework (netstandard2.0), isolation is unavailable. There, `SimpleCrudEngines.For` falls
back to the single process-wide copy. `Shared(...)` never changes the global dialect unless you pass
`claimProcessDialect: true`.

### How the extension plugs into ez-odata

`ExtendEzOData` **wraps two of ez-odata's existing service registrations** and changes nothing else:

- The **runtime resolver** gives an extended service two things: a schema where columns the entity
  can't write are marked computed (read-only), and a connector type specific to that service.
- The **connector registry** resolves that connector type to the stock connector wrapped by:
  - **Reads**: ez-odata's own compiled SQL (full `$filter`, `$expand`, `$apply`, `$count`, keyset
    paging), plus `BeforeRead` / `AfterRead`.
  - **Writes** to entity tables: the configured engine (SimpleCRUD or EF Core) together with the
    handler, in one transaction. Tables without an entity use the stock writer.

Hooks run **after** ez-odata's policy engine has applied role rules, row filters and field policies,
so a hook cannot widen access.

Update and delete honor the row filter by checking and writing inside a transaction that no
concurrent change can split: `REPEATABLE READ` on PostgreSQL and `SERIALIZABLE` elsewhere.
Deadlocks and serialization failures are retried up to 3 times. Rows hidden by `BeforeRead` (for
example, soft-deleted rows) cannot be updated or deleted either.

At startup, every entity is checked against the introspected schema: the table, each column, the
key, and PostgreSQL case-sensitivity. Any mismatch stops the app with a precise message.

## Using ez-odata-api

The packages depend on the published `EzOdata.*` packages on nuget.org (1.0.6), so a clone builds on
its own. To develop against a local ez-odata-api checkout instead:

```bash
dotnet build -p:EzOdataSource=project                                  # ../ez-odata-api-1.0.6 if present, else ../ez-odata-api
dotnet build -p:EzOdataSource=project -p:EzOdataRoot=/path/to/ez-odata-api/
```

## Packages and releases

| Trigger | What gets published |
|---|---|
| Push to `main` | `0.1.0-ci.N` prereleases to GitHub Packages |
| Tag `vX.Y.Z` or `vX.Y.Z-rc.N` | `EzOdata.SimpleCrud`, `EzOdata.Entities.AspNetCore`, `EzOdata.SimpleCrud.AspNetCore` and `EzOdata.EntityFrameworkCore.AspNetCore` to **nuget.org**, through trusted publishing (no stored API key), plus GitHub Packages and a GitHub Release |

## Tests

```bash
dotnet test                      # everything; Docker databases start automatically
EZSC_SKIP_DOCKER=1 dotnet test   # SQLite only
./compare.sh --sqlite-only       # tests + timings + report.html
./demo-swagger.sh                # Swagger lookup, read two DBs, time both
```

| Suite | Covers |
|---|---|
| `EzOdata.SimpleCrud.Tests` | Isolated SimpleCRUD engines, naming, sessions, multi-database facade (unchanged in 2.0) |
| `EzOdata.Entities.AspNetCore.Tests` | The same HTTP end-to-end, feature and multi-dialect suites run once for SimpleCRUD and once for EF Core. Engine-specific tests cover SimpleCRUD `[Required]` composite keys and naming, EF startup guards (retry / owned / TPH / key types / value-converter warning), and one escape hatch per engine. |

CI runs both suites against Dapper.SimpleCRUD 2.3.0 and 2.4.0-beta1. Before publishing, it installs the
exact packed `.nupkg` files into [`tests/Smoke`](tests/Smoke/Program.cs) and runs them.

The Docker tests use Testcontainers and find Colima's socket automatically. SQL Server 2022 images
are x86-only and crash under QEMU on Apple silicon, so on ARM hosts the tests use **Azure SQL Edge**
(the same SQL Server engine and T-SQL dialect). Override the image with `EZSC_MSSQL_IMAGE`.

## Known limits

- Deep insert (nested POST) is not supported on entity-mapped tables, and a `$batch` changeset
  cannot mix entity-mapped and plain tables.
- EF Core MySQL is not supported on ez-odata's MySqlConnector connection. Pomelo 9 is not ABI-compatible
  with EF Core 10, and Oracle's provider requires `MySql.Data`. Use `.UseSimpleCrud()` for MySQL, or pass
  a custom `UseEfCore` configure.
- Key types are limited to SimpleCRUD's own: int, long, short, Guid and string, including composite keys.
- The facade reads a few of SimpleCRUD's private metadata methods. CI tests 2.3.0 and 2.4.0-beta1,
  and an incompatible version fails at startup with a clear message. Isolated engines need
  `Dapper.SimpleCRUD.dll` on disk, so `PublishSingleFile` isn't supported.
- Hooks get the caller from `IHttpContextAccessor`, because ez-odata's connector interface doesn't carry
  the identity.
- Hook code inside the transaction can run again if a deadlock retry happens. Put external side effects
  (email, queues) in `ctx.OnCommitted(...)`, which runs once after commit.

See [CHANGELOG.md](CHANGELOG.md) for release history.

## Credits

- [**Dapper.SimpleCRUD**](https://github.com/ericdc1/Dapper.SimpleCRUD) by Eric Coffman
  ([@ericdc1](https://github.com/ericdc1)) does the real work: entity mapping and every write. It is used
  unmodified. Building this produced two upstream contributions: a fix for stale dialect quoting
  ([#282](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/282)) and an enhancement that caches per-type property
  lists ([#283](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/283)). See [`ERIC.md`](ERIC.md), [`TEAM.md`](TEAM.md)
  and [`docs/upstream`](docs/upstream/README.md).
- [**Dapper**](https://github.com/DapperLib/Dapper) by the Stack Overflow team.
- [**ez-odata-api**](https://github.com/YOLOVibeCode/ez-odata-api) provides the protocol, policy and docs engine, also used unmodified.

Licensed under Apache-2.0, the same license as Dapper.SimpleCRUD.
