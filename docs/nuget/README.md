# EzOdata + SimpleCRUD / EF Core

Packages that take over [ez-odata-api](https://github.com/YOLOVibeCode/ez-odata-api) tables with
[Dapper.SimpleCRUD](https://github.com/ericdc1/Dapper.SimpleCRUD) or EF Core, both used unmodified.

## EzOdata.SimpleCrud: several SimpleCRUD dialects in one process

SimpleCRUD keeps its dialect in static state, so one process normally speaks one dialect. This package
gives each dialect its own isolated SimpleCRUD engine, created on first use. SQL Server, PostgreSQL,
MySQL and SQLite can then run side by side, next to your existing SimpleCRUD code, which it never touches.

```csharp
var pg  = SimpleCrud.For(SimpleCRUD.Dialect.PostgreSQL).WithConnection(() => new NpgsqlConnection(pgCs)).Build();
var sql = SimpleCrud.For(SimpleCRUD.Dialect.SQLServer).WithConnection(() => new SqlConnection(sqlCs)).Build();

await pg.InsertAsync(customer);
var order = await sql.GetAsync<Order>(42);

// or with keyed DI
services.AddSimpleCrud("warehouse", SimpleCRUD.Dialect.SQLServer, _ => new SqlConnection(sqlCs));
```

Your existing SimpleCRUD POCOs and attributes work unchanged. The overhead is about 1.06× a direct
SimpleCRUD call. Supports SimpleCRUD 2.3.x and 2.4.x on .NET 8+; on netstandard2.0 it falls back to
one dialect per process.

## Instant OData/REST API with a SimpleCRUD or EF Core write engine

[ez-odata-api](https://github.com/YOLOVibeCode/ez-odata-api) serves every table of a database as a
governed OData v4 / REST API. `EzOdata.Entities.AspNetCore` lets you take over any table with an
entity and typed hooks. Pick the write engine per service:

```csharp
builder.Services.AddEzOData(ez => ez.AddService("crm", s => s.UsePostgreSql(connection)));
builder.Services.ExtendEzOData(x => x.Service("crm", crm => crm
    .UseSimpleCrud()                       // or .UseEfCore<CrmDbContext>()
    .Table<Customer, CustomerHandler>()
    .Table<Order>(t => t.BeforeInsert((o, ctx) => { if (o.Total <= 0) ctx.Reject("Total must be positive."); }))));
app.MapEzOData("/api/odata");
```

- Writes go through the chosen engine in one transaction; hooks share it via `ctx.Data`.
- ez-odata's role rules and row filters still apply, and hooks can't widen them.
- Composite keys are supported, and `UsePropertyNames()` exposes C# property names in the API.
- `ctx.OnCommitted(...)` runs side effects only after a successful commit.

**Docs, tests and samples:** https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD · Apache-2.0
