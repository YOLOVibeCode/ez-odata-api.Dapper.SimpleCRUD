# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [SemVer](https://semver.org/).

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

[1.0.0]: https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD/compare/v1.0.0-rc.1...v1.0.0
[1.0.0-rc.1]: https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD/releases/tag/v1.0.0-rc.1
