# EzOdata + Dapper.SimpleCRUD

Two packages built on [Dapper.SimpleCRUD](https://github.com/ericdc1/Dapper.SimpleCRUD), which they use unmodified.

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

## EzOdata.SimpleCrud.AspNetCore: instant OData/REST API with SimpleCRUD overrides

[ez-odata-api](https://github.com/YOLOVibeCode/ez-odata-api) serves every table of a database as a
governed OData v4 / REST API. This package lets you take over any table with an ordinary SimpleCRUD
entity and typed hooks for validation, auditing, soft delete, or replacing an operation outright.

```csharp
builder.Services.AddEzOData(ez => ez.AddService("crm", s => s.UsePostgreSql(connection)));  // stock ez-odata
builder.Services.ExtendEzOData(x => x.Service("crm", crm => crm
    .Table<Customer, CustomerHandler>()                                                        // handler class (DI)
    .Table<Order>(t => t.BeforeInsert((o, ctx) => { if (o.Total <= 0) ctx.Reject("Total must be positive."); }))));
app.MapEzOData("/api/odata");
```

- Writes go through SimpleCRUD in one transaction, and hooks share it.
- ez-odata's role rules and row filters still apply, and hooks can't widen them.
- Composite keys are supported, and `UsePropertyNames()` exposes C# property names in the API.
- `ctx.OnCommitted(...)` runs side effects only after a successful commit.

**Swagger UI:** every service gets an OpenAPI 3.1 document at `/api/odata/{service}/openapi.json`.
Point `Swashbuckle.AspNetCore.SwaggerUI` at it to browse and query the database from the browser. See
[Browse and query your database in Swagger UI](https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD#browse-and-query-your-database-in-swagger-ui).

**Docs, tests and samples:** https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD · Apache-2.0
