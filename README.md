# ez-odata-api + Dapper.SimpleCRUD (POC)

This POC adds Dapper.SimpleCRUD to [ez-odata-api](https://github.com/YOLOVibeCode/ez-odata-api)
without changing ez-odata-api at all. It has two parts:

- **`EzOdata.SimpleCrud`**: an instance-based facade over [Dapper.SimpleCRUD](https://www.nuget.org/packages/Dapper.SimpleCRUD).
  Each dialect gets its own isolated SimpleCRUD engine, created on first use, so several databases
  and your existing SimpleCRUD code can run in one process.
- **`EzOdata.SimpleCrud.AspNetCore`**: the DreamFactory model on top of ez-odata. You still get an
  instant API for every table. You can then take over individual tables with SimpleCRUD entities and
  typed hooks (validate, stamp, audit, soft delete, or replace an operation outright).

ez-odata-api is used unmodified. Its `AddEzOData` / `MapEzOData` calls stay exactly as documented.
Design and requirements (each mapped to a test) are in [`specification.md`](specification.md).

## Quick start

```csharp
// 1. Stock ez-odata: every table, instantly. This is the same code as the ez-odata README.
builder.Services.AddEzOData(ez =>
{
    ez.AddService("crm", s => s.UsePostgreSql(connection));
    ez.AddRole("rep", r => r.Allow("crm", "customers", Verb.All, rowFilter: "owner_id eq @identity.sub"));
    ez.UseHostRoles();
});

// 2. New: take over selected tables. Tables you don't list keep the stock instant API.
builder.Services.ExtendEzOData(x => x.Service("crm", crm => crm
    .Table<Customer, CustomerHandler>()                        // a handler class, created with DI
    .Table<Order>(t => t                                       // or inline hooks
        .BeforeInsert((o, ctx) => { if (o.Total <= 0) ctx.Reject("Total must be positive."); }))));

app.MapEzOData("/api/odata");                                  // unchanged
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
        ctx.Crud.InsertAsync(new AuditLog { Entity = "customer", EntityId = c.Id, Action = "insert" });

    public override Task<int> DeleteAsync(Customer c, EzHookContext ctx)          // soft delete
    {
        c.IsDeleted = true;
        return ctx.Session!.UpdateAsync(c);
    }
}
```

A runnable version is in [`samples/EzOdata.SimpleCrud.Sample`](samples/EzOdata.SimpleCrud.Sample/Program.cs).

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

### Handler members

| Member | When it runs | Default |
|---|---|---|
| `BeforeReadAsync(query, ctx)` | Before reads, `$count` and `$expand`, and before the existence check of an update or delete | Returns the query unchanged |
| `AfterReadAsync(rows, ctx)` | After reads (not `$apply`) | Nothing |
| `BeforeInsertAsync` / `InsertAsync` / `AfterInsertAsync` | POST | SimpleCRUD `InsertAsync` |
| `BeforeUpdateAsync(entity, original)` / `UpdateAsync` / `AfterUpdateAsync` | PATCH / PUT | SimpleCRUD `UpdateAsync` |
| `BeforeDeleteAsync` / `DeleteAsync` / `AfterDeleteAsync` | DELETE | SimpleCRUD `DeleteAsync` |

`EzHookContext` provides:
- `User` / `UserId`
- request-scoped `Services`
- `Crud`, which is SimpleCRUD bound to the write's own connection and transaction
- `Column(nameof(Prop))`, which maps an entity property to its API column name
- `Reject(msg)` (400) and `Forbid(msg)` (403); both roll back the write
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
  - **Writes** to entity tables: SimpleCRUD on the service's isolated engine, together with the
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

The packages depend on the published `EzOdata.*` packages on nuget.org (1.0.5), so a clone builds on
its own. To develop against a local ez-odata-api checkout instead:

```bash
dotnet build -p:EzOdataSource=project                                  # uses ../ez-odata-api
dotnet build -p:EzOdataSource=project -p:EzOdataRoot=/path/to/ez-odata-api/
```

## Packages and releases

| Trigger | What gets published |
|---|---|
| Push to `main` | `0.1.0-ci.N` prereleases to GitHub Packages |
| Tag `vX.Y.Z` or `vX.Y.Z-rc.N` | `EzOdata.SimpleCrud` and `EzOdata.SimpleCrud.AspNetCore` to **nuget.org**, through trusted publishing (no stored API key), plus GitHub Packages and a GitHub Release |

## Tests

```bash
dotnet test                      # everything; Docker databases start automatically
EZSC_SKIP_DOCKER=1 dotnet test   # SQLite only
```

| Suite | Covers |
|---|---|
| `EzOdata.SimpleCrud.Tests` (21) | Engines are lazy singletons, and simultaneous first use creates exactly one. A test reproduces the upstream `SetDialect` stale-cache bug. Each dialect emits its own SQL in one process, and the host's SimpleCRUD is untouched. Mapping is read from SimpleCRUD itself. Per-engine naming. Exceptions unwrapped. Delegates cached. Sessions and transactions. Keyed DI. Shared-mode guard. Overhead. **Real PostgreSQL, MySQL, SQL Server and SQLite, concurrently in one process.** |
| `EzOdata.SimpleCrud.AspNetCore.Tests` (19) | Through ez-odata's real HTTP pipeline: stock tables untouched; role row filters combined with handler filters; `$expand` and `AfterRead`; inserts through SimpleCRUD and hooks; rejection returns 400 with rollback; hook side-writes roll back with the API write; insert outside the row filter returns 403; unique violation returns 409; PATCH changes only the fields sent; handler Forbid; read-only columns; row filters protect update and delete; soft delete; startup schema validation; composite keys; property-name exposure, including row filters and `$expand` across renamed keys. **One API serving four services on four database engines at once.** |

The Docker tests use Testcontainers and find Colima's socket automatically. SQL Server 2022 images
are x86-only and crash under QEMU on Apple silicon, so on ARM hosts the tests use **Azure SQL Edge**
(the same SQL Server engine and T-SQL dialect). Override the image with `EZSC_MSSQL_IMAGE`.

## POC limits

- Deep insert (nested POST) is not supported on entity-mapped tables, and a `$batch` changeset
  cannot mix entity-mapped and plain tables.
- Key types are limited to SimpleCRUD's own: int, long, short, Guid and string.
- The facade reads SimpleCRUD 2.3.x private metadata methods (pinned; a test guards it).
  Isolated engines need `Dapper.SimpleCRUD.dll` on disk, so `PublishSingleFile` isn't supported.
- Hooks get the caller from `IHttpContextAccessor`, because ez-odata's connector interface
  doesn't carry the identity.
- After hooks can run again if a deadlock retry happens. Put external side effects (email,
  queues) after commit, not inside the transaction.

## Credits

- [**Dapper.SimpleCRUD**](https://github.com/ericdc1/Dapper.SimpleCRUD) by Eric Coffman
  ([@ericdc1](https://github.com/ericdc1)) does the real work: entity mapping and every write. It is used
  unmodified. A fix found while building this is offered upstream; see [`ERIC.md`](ERIC.md) and
  [`docs/upstream`](docs/upstream/README.md).
- [**Dapper**](https://github.com/DapperLib/Dapper) by the Stack Overflow team.
- [**ez-odata-api**](https://github.com/YOLOVibeCode/ez-odata-api) provides the protocol, policy and docs engine, also used unmodified.

Licensed under Apache-2.0, the same license as Dapper.SimpleCRUD.
