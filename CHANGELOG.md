# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [SemVer](https://semver.org/).

## [2.0.0] - 2026-09-30

### Added
- **`EzOdata.Entities.AspNetCore`**: engine-neutral table takeover (`ExtendEzOData`, `EzTableHandler<T>`,
  `ctx.Data`, `IEntityEngine` / `IEntityStore` / `EntityMap`).
- **`EzOdata.EntityFrameworkCore.AspNetCore`**: `.UseEfCore<TContext>()` — mapping from `DbContext.Model`,
  writes on ez-odata's connection and transaction, startup guards, `ctx.DbContext<T>()`.
- `.UseSimpleCrud()` on the service builder; `ctx.SimpleCrud()` / `ctx.Session()` as the SimpleCRUD escape hatch.
- The same end-to-end suite runs against both engines.
- `./compare.sh` (and `compare.command` / `compare.cmd`): tests, quick timings (or `--deep` BenchmarkDotNet),
  and a side-by-side `report.html`.
- `./demo-swagger.sh` / `demo-swagger.cmd`: look up Swagger, read two sample databases, time both.
- Depends on ez-odata-api **1.0.6** (REST OpenAPI `servers[0].url` includes the prefix and service, so
  Swagger UI "Try it out" hits the real paths).
- EF Core MySQL is not supported on ez-odata's MySqlConnector connection (Pomelo 9 crashes on EF Core 10;
  Oracle's provider requires `MySql.Data`). Use SimpleCRUD for MySQL, or pass a custom `UseEfCore` configure.

### Changed
- Handler defaults call `ctx.Data` instead of SimpleCRUD. `UpdateAsync` receives the original row so PATCH
  writes only changed columns.
- `ExtendEzOData`, `EzTableHandler<T>` and `EzHookContext` move to `EzOdata.Entities.AspNetCore`.

### Migration from 1.x
1. Add `.UseSimpleCrud()` on each extended service (or `.UseEfCore<TContext>()`).
2. `using EzOdata.Entities.AspNetCore` for `ExtendEzOData` / handlers / `EzFilter`.
3. Replace `ctx.Crud` / `ctx.Session` with `ctx.Data` (or `ctx.SimpleCrud()`).
4. If you overrode `UpdateAsync(entity, ctx)`, the signature is now `UpdateAsync(entity, original, ctx)`.

The `EzOdata.SimpleCrud` facade is unchanged.

## [1.0.0] - 2026-09-30

### Added
- `ctx.OnCommitted(...)`: side effects that run once, only after the write's transaction commits
  (never after a rollback, and never twice across a deadlock retry). Failures are logged.
- A startup compatibility check: an incompatible Dapper.SimpleCRUD fails when the engine is created,
  with a message naming the version and the missing members.
- The packages now include XML documentation, Source Link, `.snupkg` symbol packages, an icon and a
  package README. The engine's SimpleCRUD methods show SimpleCRUD's own documentation.

### Changed
- Tested against Dapper.SimpleCRUD 2.3.0 and 2.4.0-beta1 in CI. The packages depend on SimpleCRUD
  2.3.0 or later.
- CI installs and runs the exact packed `.nupkg` files (a smoke test) before publishing anything.

## [1.0.0-rc.1] - 2026-09-30

### Added
- **EzOdata.SimpleCrud:** an instance-based facade over Dapper.SimpleCRUD. Each dialect (plus naming
  convention) gets its own isolated engine, a lazy singleton in its own `AssemblyLoadContext`, so SQL
  Server, PostgreSQL, MySQL and SQLite run concurrently in one process. Your own SimpleCRUD code is
  untouched. Includes `SimpleCrud.For(...)`, sessions and transactions, keyed DI, and per-engine
  naming conventions.
- **EzOdata.SimpleCrud.AspNetCore:** `services.ExtendEzOData(...)` takes over ez-odata-api tables with
  plain SimpleCRUD entities and typed hooks (`EzTableHandler<T>` or inline). Writes go through SimpleCRUD
  in one transaction, with ez-odata's row filters and role rules still enforced. Also includes soft
  delete, composite keys, `UsePropertyNames()`, and startup validation of entities against the schema.
- Depends on the ez-odata-api 1.0.5 packages from nuget.org.

[2.0.0]: https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD/compare/v1.0.0-rc.1...v1.0.0
[1.0.0-rc.1]: https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD/releases/tag/v1.0.0-rc.1
