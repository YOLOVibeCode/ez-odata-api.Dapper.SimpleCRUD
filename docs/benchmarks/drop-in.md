# Drop-in analysis: adding the instant API to a new project

What does it cost to take a brand-new ASP.NET Core project that already has a database, add one package from
nuget.org, and get an API? This measures exactly that, for three choices:

1. **stock ez-odata** (`EzOdata.AspNetCore` 1.0.7): every table, untyped
2. **the SimpleCRUD engine** (`EzOdata.SimpleCrud.AspNetCore` 2.0.1): one table taken over with a typed entity and a validation hook
3. **the EF Core engine** (`EzOdata.EntityFrameworkCore.AspNetCore` 2.0.1): the same, on EF Core

The database is the showcase's shop database (SQLite: 500 customers, orders, order lines), used as it is.

Each project is created with `dotnet new web` outside this repository, so none of this repository's build settings
apply. It restores from nuget.org with an empty package cache and is published in Release. Each app runs from its
own folder, served by Kestrel and measured over real sockets.

Reproduce with [`benchmarks/DropIn/run.sh`](../../benchmarks/DropIn/run.sh) (about 15 minutes). Raw results:
[`raw/drop-in/`](raw/drop-in/). Measured 2026-10-01 on an Apple M4 Max, .NET 10.0.1.

## What you add

**The project file: one package reference.** Nothing else changes in `dotnet new web`'s project:

```xml
<ItemGroup>
  <PackageReference Include="EzOdata.SimpleCrud.AspNetCore" Version="2.0.1" />
</ItemGroup>
```

**The database: one connection string** in `appsettings.json` (for SQLite, the file path):

```json
"ConnectionStrings": { "shop": "/path/to/shop.db" }
```

**The code.** With the SimpleCRUD engine, `Program.cs`:

```csharp
using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;
using EzOdata.Entities.AspNetCore;
using EzOdata.SimpleCrud.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var shop = builder.Configuration.GetConnectionString("shop") ?? throw new InvalidOperationException("Set ConnectionStrings:shop.");

builder.Services.AddEzOData(ez =>
{
    ez.AddService("shop", s => s.UseSqlite(shop));
    ez.AllowAnonymousInDevelopment();
});
builder.Services.ExtendEzOData(x => x.Service("shop", s => s
    .UseSimpleCrud()
    .Table<Customer>(t => t.BeforeInsert((c, ctx) => { if (string.IsNullOrWhiteSpace(c.Name)) ctx.Reject("full_name is required."); }))));

var app = builder.Build();
app.MapEzOData("/api/odata");
app.MapEzODataRest("/api/rest");
app.UseEzODataSwaggerUI();
app.Run();
```

and the entity, an ordinary SimpleCRUD class that maps to the existing `customers` table:

```csharp
using Dapper;

[Table("customers")]
public class Customer
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("full_name")] public string Name { get; set; } = "";
    [Column("email")] public string? Email { get; set; }
    [Column("country")] public string Country { get; set; } = "";
    [Column("owner_id")] public string? OwnerId { get; set; }
    [Column("created_by")] public string? CreatedBy { get; set; }
    [Column("is_deleted")] public bool IsDeleted { get; set; }
}
```

Stock ez-odata is the same `Program.cs` without the `ExtendEzOData` block and without the entity. The EF Core
variant swaps `.UseSimpleCrud()` for `.UseEfCore<ShopDb>()`, maps the entity with EF Core's own `[Table]`/`[Column]`
attributes, and adds a four-line `DbContext`. EF Core needs no
`AddDbContext`: the engine creates the contexts. All three apps are in
[`benchmarks/DropIn/apps`](../../benchmarks/DropIn/apps).

## What it costs

| | Stock ez-odata | + SimpleCRUD engine | + EF Core engine |
|---|---:|---:|---:|
| Package you add | `EzOdata.AspNetCore` | `EzOdata.SimpleCrud.AspNetCore` | `EzOdata.EntityFrameworkCore.AspNetCore` |
| Lines you write | 12 | 26 | 30 |
| Packages restored (transitive) | 42 | 47 | 54 |
| Downloaded on a clean machine | 53.9 MB | 54.4 MB | 73.0 MB |
| Published app | 44.2 MB · 62 DLLs | 44.6 MB · 67 DLLs | 57.4 MB · 72 DLLs |
| Cold start to first query: fastest of 21 | 458 ms | 487 ms | 615 ms |
| … median of 21 | 869 ms | 663 ms | 1009 ms |
| Managed heap after first query / after load | 9 / 9.6 MB | 10 / 11 MB | 9.9 / 13.3 MB |
| Working set after first query / after load | 128 / 245 MB | 132 / 256 MB | 143 / 269 MB |
| Assemblies loaded (default context) | 126 | 138 | 143 |
| Extra load contexts | none | `SimpleCRUD[SQLite]#1` (Dapper.SimpleCRUD 2.1.0.0) | none |

- **The base is ez-odata, not the engines.** Stock ez-odata already brings drivers for all four databases
  (Microsoft.Data.SqlClient, Npgsql, MySqlConnector and Microsoft.Data.Sqlite), so 53.9 MB is the starting
  point whichever engine you pick.
- **The SimpleCRUD engine adds 0.5 MB and 5 packages:** Dapper, Dapper.SimpleCRUD, and this repository's two
  libraries. **The EF Core engine adds 19 MB and 12 packages:** EF Core with its SQLite, SQL Server and
  PostgreSQL providers.
- **Cold start:** the fastest of 21 runs is the cleanest measure, because noise only ever adds time to a cold
  start. Stock starts in 458 ms. The SimpleCRUD engine adds about 30 ms, which is loading SimpleCRUD's isolated
  copy. EF Core adds about 160 ms, which is building its model. The medians vary from run to run by more than
  these differences, even over 21 runs.
- **Memory:** the managed heap stays around 10 MB in all three. The working set grows to about 250 MB under load
  because ASP.NET Core uses the server garbage collector, which reserves heaps per core (16 here). Set
  `<ServerGarbageCollector>false</ServerGarbageCollector>` if a small footprint matters more than throughput.

## How it references SimpleCRUD at run time

The SimpleCRUD app's load contexts after the load test (from its diagnostics endpoint):

- **Default:** your code, ez-odata, Dapper, and `Dapper.SimpleCRUD`, loaded because your entity uses its `[Table]`,
  `[Key]` and `[Column]` attributes. Any SimpleCRUD code of your own keeps using this copy and its process-wide
  dialect, untouched.
- **`SimpleCRUD[SQLite]#1`:** a second, isolated copy of `Dapper.SimpleCRUD`, created on first use for the SQLite
  service. The engine's inserts, updates and deletes run here, with the SQLite dialect set only in this copy. A
  PostgreSQL service would get its own `SimpleCRUD[PostgreSQL]#…` copy, so several dialects can run in one process.

The assembly version reads 2.1.0.0: that is what the Dapper.SimpleCRUD 2.3.0 package carries.

## Through HTTP

Same requests to all three apps, interleaved: 6 rounds in rotating order after a warm-up, 6,000 requests per
scenario one at a time and 24,000 at 16 concurrent. Each cell shows the median latency with the range of the 6
round medians, then the throughput with its range. **Read a difference as real only when the ranges don't
overlap.**

**One request at a time**: median latency (range of the 6 round medians), throughput (range across rounds)

| Scenario | Stock ez-odata | + SimpleCRUD engine | + EF Core engine |
|---|---:|---:|---:|
| GET by key | 0.113 ms (0.072–0.288)<br/>6,089/s (2,635–13,002) | 0.091 ms (0.071–0.361)<br/>4,015/s (1,067–13,021) | 0.099 ms (0.063–0.277)<br/>5,967/s (2,155–14,435) |
| GET filtered list (20 rows) | 0.177 ms (0.165–0.229)<br/>4,321/s (2,539–5,679) | 0.171 ms (0.159–0.181)<br/>5,357/s (4,711–5,955) | 0.159 ms (0.149–0.189)<br/>4,896/s (2,643–6,342) |
| GET by key with `$expand=orders` | 0.101 ms (0.096–0.105)<br/>8,810/s (8,343–9,623) | 0.104 ms (0.094–0.131)<br/>8,387/s (6,522–9,361) | 0.099 ms (0.092–0.107)<br/>9,070/s (8,189–10,371) |
| POST (insert) | 0.121 ms (0.112–0.130)<br/>7,141/s (5,901–8,338) | 0.140 ms (0.118–0.200)<br/>5,218/s (3,320–7,770) | 0.202 ms (0.167–0.448)<br/>3,315/s (1,786–5,320) |
| PATCH (update) | 0.080 ms (0.075–0.089)<br/>10,547/s (8,557–11,967) | 0.151 ms (0.127–0.298)<br/>3,892/s (1,591–7,106) | 0.178 ms (0.147–0.230)<br/>4,892/s (3,590–6,199) |

**16 concurrent requests**: median latency (range of the 6 round medians), throughput (range across rounds)

| Scenario | Stock ez-odata | + SimpleCRUD engine | + EF Core engine |
|---|---:|---:|---:|
| GET by key | 0.243 ms (0.163–0.477)<br/>41,237/s (20,748–71,349) | 0.229 ms (0.170–0.482)<br/>28,556/s (8,554–71,849) | 0.245 ms (0.190–0.316)<br/>39,676/s (25,870–46,416) |
| GET filtered list (20 rows) | 0.719 ms (0.545–1.065)<br/>14,991/s (8,142–23,563) | 0.748 ms (0.501–1.583)<br/>12,804/s (5,157–24,718) | 0.609 ms (0.480–0.768)<br/>20,557/s (17,911–23,854) |
| GET by key with `$expand=orders` | 0.228 ms (0.167–0.274)<br/>52,345/s (42,193–69,898) | 0.228 ms (0.198–0.264)<br/>51,536/s (41,820–59,651) | 0.198 ms (0.160–0.223)<br/>57,936/s (46,776–72,767) |
| POST (insert) | 0.110 ms (0.102–0.123)<br/>3,175/s (2,404–3,790) | 0.128 ms (0.118–0.138)<br/>2,695/s (2,209–3,314) | 0.200 ms (0.185–0.247)<br/>2,115/s (1,647–2,647) |
| PATCH (update) | 0.098 ms (0.072–0.880)<br/>2,037/s (797–6,450) | 0.169 ms (0.122–1.765)<br/>1,264/s (490–3,311) | 0.241 ms (0.153–1.311)<br/>1,028/s (527–3,313) |

**What holds up:**
- **Reads cost the same with every engine.** All three read through ez-odata's compiled SQL. For example, `$expand`
  one at a time is 0.101, 0.104 and 0.099 ms, inside each other's ranges.
- **Writes cost more with a typed engine,** because it reads the row first, runs your hooks and wraps it all in a
  transaction:
  - **Update, one at a time:** 0.080 ms with stock, 0.151 ms with SimpleCRUD and 0.178 ms with EF Core.
  - **Insert, 16 at a time:** 0.110, 0.128 and 0.200 ms, with no overlap between the ranges. SimpleCRUD stays close
    to stock; EF Core costs more.
- **SQLite allows one writer.** Concurrent writes queue on its lock, which is why the 16-concurrent write
  throughput is far below the read throughput. On PostgreSQL or SQL Server, writes run in parallel.
- **Reads by key on a laptop vary a lot between rounds** (0.07–0.36 ms), more than any difference between
  engines. That's why the ranges are shown.

## A pitfall we hit: a missing connection string gives a running app that answers 404

The first measurement launched the published app as `dotnet /path/to/DropIn.dll` from another folder. ASP.NET Core
reads `appsettings.json` from the *current* folder, so the connection string was missing. ez-odata logged
`Embedded service 'shop' introspection failed ... SQLite requires a filePath`, started anyway, and answered every
request with 404. ez-odata reads a database's schema once, at startup, and doesn't retry.

So in your `Program.cs`, fail fast when the connection string is missing (`?? throw`, as above), and run the app
from its own folder (or set the content root). A failed schema read is easy to miss in the log.
