# EzOdata + Dapper.SimpleCRUD: Specification

**Status:** v1.0, describes release 1.0.0 · **Supersedes:** Draft v0.2 (POCO-only schema, one
dialect per process, upstream ez-odata changes) · **Owner:** Noctusoft, Inc. · **License:** Apache-2.0

| Building block | Role |
|---|---|
| [ez-odata-api](https://github.com/YOLOVibeCode/ez-odata-api) 1.0.x, **unmodified** | Instant OData v4 / REST API over a database, with RBAC, row filters, field policies and docs |
| [Dapper.SimpleCRUD](https://github.com/ericdc1/Dapper.SimpleCRUD) 2.3.x, **unmodified** | The entity model and the write engine for every table you choose to take over |

---

## 1. Definition

Two NuGet packages:

- **`EzOdata.SimpleCrud`** makes Dapper.SimpleCRUD instance-based. You get one isolated, lazily
  created SimpleCRUD engine per dialect, so any number of databases and dialects can run in one
  process, next to the application's own SimpleCRUD code.
- **`EzOdata.SimpleCrud.AspNetCore`** follows the DreamFactory model on ez-odata. Install it and the
  API is live for every table at once. Then take over any table with an existing SimpleCRUD entity and
  typed hooks to validate, stamp, audit, soft-delete, or replace an operation outright.

## 2. Problems solved

1. **SimpleCRUD speaks one dialect per process.** The dialect, identifier quoting and SQL caches are
   private statics. Users asked about this in #56, #218, #231 and #233 (2015–2021), and the answer
   was "not supported". Worse, `SetDialect` does not clear the caches, so switching after first use
   emits mixed SQL. That bug is verified; the fix is drafted in [`docs/upstream`](docs/upstream/README.md).
2. **Hand-written controllers per entity.** Filtering, paging, sorting and authorization get
   reinvented each time, and SimpleCRUD's raw `WHERE` strings invite SQL injection.
3. **Instant-API products stop at the schema.** They either give no place for domain logic, or they
   require a second model.

## 3. Goals and status

| # | Goal | Status |
|---|---|---|
| G1 | Existing SimpleCRUD POCOs work unchanged; no ez-specific attributes | ✅ Entities are plain SimpleCRUD classes |
| G2 | Several dialects in one process, with the app's own SimpleCRUD untouched | ✅ PostgreSQL, MySQL, SQL Server and SQLite run concurrently in tests |
| G3 | Mapping identical to SimpleCRUD's | ✅ Read from SimpleCRUD itself, per engine (§5.3) |
| G4 | Bolt-in: ≤ 1 extra call on top of ez-odata's embedded mode | ✅ `services.ExtendEzOData(...)` |
| G5 | Zero changes to ez-odata-api and to SimpleCRUD | ✅ Wraps ez's registrations; SimpleCRUD is loaded as-is |
| G6 | ez-odata's governance unchanged, and hooks can't widen it | ✅ Hooks run after the policy engine; row filters gate writes |
| G7 | Negligible overhead | ✅ 1.06× a direct SimpleCRUD call (5,000 `Get<T>`) |
| G8 | Mistakes fail at startup | ✅ Entity and table drift stops the host with a precise message |

## 4. Non-goals (v1)

Schema management and DDL; changing SimpleCRUD's global configuration; exposing arbitrary SQL; an
admin UI (configuration is code, as in ez-odata's embedded mode).

## 5. `EzOdata.SimpleCrud`: the engine facade

### 5.1 Engines

| ID | Requirement | Verified by |
|---|---|---|
| E-1 | `SimpleCrudEngines.For(dialect, naming?)` returns a process-lifetime singleton per (dialect, naming), created on first use exactly once, even under contention | `Engines_are_lazy_singletons_per_dialect`, `First_use_under_contention_creates_exactly_one_engine` |
| E-2 | An isolated engine loads its own copy of `Dapper.SimpleCRUD.dll` in a dedicated `AssemblyLoadContext`. Dapper, providers and entity types stay shared | `Isolated_engines_never_touch_the_process_wide_SimpleCRUD` |
| E-3 | Each engine emits its own dialect's SQL, in one process | `Each_engine_emits_its_own_dialect_in_one_process` (4 dialects) |
| E-4 | The process-wide SimpleCRUD is never modified, unless the host explicitly passes `Shared(dialect, claimProcessDialect: true)` | `Shared_engine_never_changes_global_state_implicitly` |
| E-6 | An incompatible SimpleCRUD fails when the engine is created, naming the version and the missing members. CI tests 2.3.0 and 2.4.0-beta1 | `An_incompatible_SimpleCRUD_is_rejected_with_a_clear_message` |
| E-5 | On .NET Framework / netstandard2.0, `For` falls back to the shared engine: one dialect, with conflicts rejected | Build: `netstandard2.0` target |

### 5.2 API

- **Engine:** mirrors every SimpleCRUD extension method (sync and async), with the connection first:
  `Get`, `GetList` ×3, `GetListPaged`, `Insert` ×2, `Update`, `Delete` ×2, `DeleteList` ×2,
  `RecordCount` ×2.
- **Client:** `SimpleCrud.For(dialect).WithConnection(...).WithNaming(...).Build()` returns an `ISimpleCrud`
  that opens a connection per call. `OpenSessionAsync(beginTransaction)` returns a `SimpleCrudSession`
  (unit of work) for several calls on one connection.
- **DI:** `services.AddSimpleCrud(name, dialect, factory)` registers keyed singletons, injected with
  `[FromKeyedServices]`.
- **Naming:** `SimpleCrudNaming` (for example `SnakeCase`) is per engine, not global. Resolvers are
  emitted into each engine's own load context.

### 5.3 Mapping fidelity

`engine.Describe<T>()` returns the table, key, and the selectable / insertable / updatable properties
**as that engine's SimpleCRUD computes them**. It calls SimpleCRUD's own helpers on that copy, so
quoting, resolvers and version-specific rules can't drift. Verified by
`Describe_reads_the_mapping_from_SimpleCRUD_itself`.

### 5.4 Performance and errors

Calls go through delegates compiled once per (operation, type), and exceptions surface unwrapped.
Verified by `Delegates_are_compiled_once_per_operation_and_type`, `Exceptions_surface_unwrapped` and
`Overhead_versus_calling_SimpleCRUD_directly`.

## 6. `EzOdata.SimpleCrud.AspNetCore`: the extension

### 6.1 Shape

```csharp
builder.Services.AddEzOData(ez => { ez.AddService("crm", s => s.UsePostgreSql(spec)); /* roles */ });  // stock
builder.Services.ExtendEzOData(x => x.Service("crm", crm => crm
    .Table<Customer, CustomerHandler>()                                   // handler class (DI)
    .Table<Order>(t => t.BeforeInsert((o, ctx) => { if (o.Total <= 0) ctx.Reject("..."); }))));
app.MapEzOData("/api/odata");                                             // stock
```

### 6.2 Levels

| Level | Configuration | Behavior |
|---|---|---|
| 0–1 | Stock ez-odata | Instant API for every table, with ez options |
| 2 | `.Table<T>()` | SimpleCRUD takes over the table's writes. Columns the entity cannot write become read-only in the API |
| 3 | Inline hooks | `BeforeRead`, `AfterRead`, `Before/After Insert/Update/Delete` |
| 4 | `.Table<T, THandler>()` | `EzTableHandler<T>` created from request services |
| 5 | Override `InsertAsync` / `UpdateAsync` / `DeleteAsync`, or `InsteadOf*` | Replace the operation (for example, soft delete) |
| 6 | Your own endpoints plus `ISimpleCrud` | Plain ASP.NET Core |

### 6.3 Requirements

| ID | Requirement | Verified by |
|---|---|---|
| X-1 | Tables without an entity keep the stock instant API, including writes | `Tables_without_an_entity_keep_the_stock_instant_api` |
| X-2 | Reads use ez-odata's compiled, parameterized SQL (full `$filter`, `$expand`, `$apply`, `$count`, paging). `BeforeRead` can only AND predicates, and `AfterRead` post-processes rows | `Reads_apply_role_row_filters_and_handler_filters_together`, `Expand_and_after_read_hooks_work_on_extended_tables` |
| X-3 | POST binds a new `T`, runs the hooks, calls SimpleCRUD `Insert`, and re-reads the row in the same transaction | `Insert_runs_through_SimpleCRUD_and_the_handler` |
| X-4 | PATCH runs `Get`, merges only the sent fields, then `Update`. PUT replaces | `Patch_changes_only_what_was_sent` |
| X-5 | Hooks and their side writes (`ctx.Crud`) share the API write's transaction, and any rejection rolls everything back | `Hook_rejection_is_a_400_and_nothing_is_written`, `Side_writes_in_hooks_roll_back_with_the_api_write` |
| X-6 | `Reject` returns 400 and `Forbid` returns 403. Provider errors map to ez's taxonomy (for example, unique → 409) | `Handlers_can_forbid_an_update_based_on_the_original_row`, `Database_constraint_violations_map_to_the_engines_error_codes` |
| X-7 | Role row filters gate update and delete. Inserted rows must satisfy them, or the write rolls back with 403 | `Row_filters_protect_updates_and_deletes`, `Insert_outside_the_callers_row_filter_is_403_and_rolled_back` |
| X-8 | Rows hidden by `BeforeRead` cannot be updated or deleted | `Delete_can_be_overridden_as_a_soft_delete` |
| X-9 | Values for restricted columns (`[IgnoreUpdate]`, `[IgnoreInsert]`, `[ReadOnly]`, unmapped) are rejected with 400, never silently dropped | `Columns_the_entity_does_not_write_are_read_only_in_the_api` |
| X-10 | Entity and table mismatches (table, columns, key, PostgreSQL case-sensitivity) fail at startup | `An_entity_that_does_not_match_the_database_stops_startup` |
| X-11 | Services on different dialects coexist in one host, each on its own engine | `Every_dialect_behind_one_api_in_one_process` |
| X-12 | Composite primary keys, as SimpleCRUD models them (`[Key, Required]` parts): create, read, update, delete and conflicts by full key | `Composite_keys_work_end_to_end_through_SimpleCRUD` |
| X-14 | `ctx.OnCommitted(...)` runs once after commit, never after rollback, and a failing callback doesn't undo a committed write | `OnCommitted_runs_once_after_commit_and_never_after_rollback` |
| X-13 | `UsePropertyNames()`: entity property names become the API contract (payloads, `$filter`, `$orderby`, `$metadata`, row filters), with foreign keys renamed consistently across tables | `Entity_property_names_become_the_api_contract`, `Row_filters_and_expand_follow_the_renamed_columns` |

### 6.4 Consistency

A write gated on visibility (a row filter, an ETag, or `BeforeRead`) runs the check and the write in
one transaction: `REPEATABLE READ` on PostgreSQL, where `SERIALIZABLE` would abort unrelated writes on
small tables, and `SERIALIZABLE` elsewhere. Deadlocks and serialization failures are retried up to
3 times.

## 7. Key design decisions

| # | Decision | Rationale (and how it differs from v0.2) |
|---|---|---|
| D1 | **Isolate SimpleCRUD per dialect instead of limiting it** | v0.2 accepted "one dialect per process". Load-context isolation removes the limit without forking SimpleCRUD |
| D2 | **Keep introspection, and let entities override tables** | v0.2 dropped introspection. Keeping it preserves the instant API for every table, and entities are opt-in per table |
| D3 | **Reads use ez-odata's compiler; writes use SimpleCRUD** | SimpleCRUD can't express `$expand`, `any`/`all` or `$apply`, or arbitrary skip/top. ez's compiler is parameterized and audited. Writes are where domain rules live |
| D4 | **Wrap DI registrations, don't change ez-odata** | v0.2 required four upstream changes. The POC shows they aren't needed |
| D5 | **Ask SimpleCRUD for its mapping; don't copy its rules** | SimpleCRUD's rules changed as recently as 2026-07. Reading them from the loaded copy cannot drift |

## 8. Supported matrix

| Dimension | v1 |
|---|---|
| Dialects | SQL Server, PostgreSQL, MySQL/MariaDB, SQLite. All four are exercised concurrently; the SQL Server tests use Azure SQL Edge on ARM |
| Hosts | ASP.NET Core on .NET 10 (extension). The facade targets .NET 8+ (isolated) and netstandard2.0 (shared) |
| Keys | int, long, short, Guid, string, including composite keys (`[Key, Required]` parts) |

## 9. Limits and roadmap

| Item | Plan |
|---|---|
| Deep insert on entity tables; `$batch` mixing entity and plain tables | v1.1 |
| .NET Framework 4.8 Web API host | Only after the ez-odata `EzOdata.WebApi` adapter is in use; the facade already targets netstandard2.0 |
| MCP | Not part of ez's embedded package |
| `PublishSingleFile` | Isolation loads the DLL from disk; embed it as a resource if needed |

## 10. Quality

- 22 facade tests and 20 end-to-end tests, run against SimpleCRUD 2.3.0 and 2.4.0-beta1, plus a smoke test of the packed packages. They run against real PostgreSQL 16, MySQL 8.4,
  SQL Server (Azure SQL Edge) and SQLite via Testcontainers, and the full suite passed twice in a row.
- Every requirement in §5–6 names its test.
- Sample app: `samples/EzOdata.SimpleCrud.Sample`, verified over HTTP.

## 11. Upstream

See [`docs/upstream/README.md`](docs/upstream/README.md):

1. A 13-line PR so that `SetDialect` and the resolver setters rebuild cached names. It is verified to
   fix the mixed-dialect SQL on 2.3.0, and includes a regression test in SimpleCRUD's own test style.
2. A comment for #218 pointing to the isolation approach.
