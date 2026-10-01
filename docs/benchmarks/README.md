# Benchmarks: Dapper, Dapper.SimpleCRUD and EF Core

**[View the report](https://yolovibecode.github.io/ez-odata-api.Dapper.SimpleCRUD/benchmarks/report.html)**
(charts, findings, method) · [`results.md`](results.md) (every table) · [`raw/`](raw/) (BenchmarkDotNet
output and HTTP timings per database)

Two layers, on SQLite, PostgreSQL 16, MySQL 8.4 and SQL Server (Azure SQL Edge on ARM):

1. **The libraries on their own.** Hand-written Dapper, Dapper.SimpleCRUD (through this repository's facade
   and through its own static API), and EF Core, tracked and `AsNoTracking`. Measured with BenchmarkDotNet.
2. **Through the instant API.** Stock ez-odata, the SimpleCRUD engine and the EF Core engine, request by
   request over an in-memory TestServer.

The results below are from an Apple M4 Max with .NET 10.0.1, Dapper 2.0.78, Dapper.SimpleCRUD 2.3.0,
EF Core 10.0.0 and ez-odata-api 1.0.7. A second, independent run on 2026-10-01 reproduced them: allocations
matched exactly, and the time ratios were within their error bars.

## Run it yourself

**You need** `git` and `curl`. The .NET 10 SDK is optional: `try.sh` downloads it into `./.dotnet` if it is
missing. **Docker** is optional too: with it you get PostgreSQL, MySQL and SQL Server; without it, SQLite only.

```bash
git clone https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD
cd ez-odata-api.Dapper.SimpleCRUD
./try.sh --benchmark             # Windows: try.cmd -Benchmark
```

It builds in Release, then for each database starts only that database's container, checks that every library
returns the same rows, runs the library benchmark and then the HTTP benchmark, and stops the container. At the
end it writes the report and opens it.

- **Time:** about 30 minutes with all four databases, plus the first build. SQLite takes about 20 of them,
  mostly in its 16-concurrent-writers scenario, which waits on SQLite's single write lock. PostgreSQL, MySQL
  and SQL Server take 2–4 minutes each.
- **Output:** `artifacts/compare/<timestamp>/report.html` and `report.md`, with one folder per database (the
  BenchmarkDotNet results and `bench/quick.json`) and one log per database.

Variations:

| Command | What it runs |
|---|---|
| `./try.sh --benchmark --sqlite-only` | SQLite only, no Docker |
| `./try.sh --benchmark --no-open` | Same, without opening the browser (for CI or SSH sessions) |
| `./compare.sh --data-access` | Also runs the test suite first; the report then includes test results |
| `./compare.sh --data-access --quick` | Shorter runs (BenchmarkDotNet ShortRun, 50 HTTP samples): a quick look, wider error bars |
| `./compare.sh` | The HTTP benchmark only (no library benchmark) |
| `dotnet run --project benchmarks/EzOdata.Entities.Benchmarks -c Release -- --all --only postgresql --out out/pg` | One database, by hand |
| `dotnet run --project benchmarks/EzOdata.Entities.Benchmarks -c Release -- report --runs out --out out` | Rebuild a report from earlier runs |

The SimpleCRUD reflection-fix comparison has its own script, which downloads both versions of SimpleCRUD from
GitHub: `benchmarks/SimpleCrud.CachingAB/run.sh` (SQLite and PostgreSQL), or `run.sh sqlite` for SQLite only.

**For trustworthy numbers:**
- Close other heavy work while it runs; the timings are microbenchmarks.
- **Docker memory:** SQL Server alone needs about 2 GB. On Colima, give the VM room:
  `colima stop && colima start --cpu 8 --memory 12`. Your containers survive the restart, but you have to
  start them again.
- **Colima socket:** the fixture finds it automatically. Other setups honour `DOCKER_HOST`.

Settings (environment variables):

| Variable | Effect |
|---|---|
| `EZSC_SKIP_DOCKER=1` | Never start containers (SQLite only) |
| `EZSC_DATABASES=postgresql,sqlserver` | Start only these database containers |
| `EZSC_MSSQL_IMAGE=…` | SQL Server image to use (default: SQL Server 2022 on x64, Azure SQL Edge on ARM) |

## What the numbers say

**On a real database server, the round trip decides; the library barely shows.** Get by id takes 324 µs on
PostgreSQL with hand-written Dapper, 342 µs with SimpleCRUD (1.06×) and 358 µs with EF Core (1.11×). On
SQL Server all three are within 5% of each other, inside the confidence intervals.

**Without a network, the library's own cost is visible.** On SQLite (in process), Get by id takes 5.1 µs with
Dapper, 12.0 µs with SimpleCRUD and 18.3 µs with EF Core. A 20-row filtered page takes 18.1, 26.0 and 41.8 µs.
Across the four databases (geometric mean), SimpleCRUD costs 1.04–1.24× Dapper per single-row operation, and
EF Core 1.15–1.60×.

**Allocations are where SimpleCRUD pays most.** It allocates 3.4× what Dapper does on Get by id and 2.7× on
Update. Most of that is reflection repeated on every call. See [the upstream fix](#what-we-changed-in-simplecrud).

**Bulk inserts: EF Core wins by an order of magnitude on networked servers.** 100 inserts in one transaction
take 2.6 ms with EF Core on PostgreSQL and 6.0 ms on SQL Server. Dapper's list form takes 31 ms and 64 ms, and
SimpleCRUD 33 ms and 68 ms. EF Core batches the rows into a few statements; the other two send one statement per
row. On SQLite, which has no round trip, EF Core is the slowest (1.10 ms against Dapper's 0.32 ms). If you bulk
insert with Dapper, use a multi-row `VALUES` statement or the driver's bulk API instead.

**`AsNoTracking` saves little on reads this small.** Get by id is the same either way. The 20-row page is 4%
faster (geometric mean), 11% at most, on SQLite.

**The facade is free.** Our facade runs SimpleCRUD in an isolated copy per dialect. On SQLite it differs from
SimpleCRUD's static API by −6% to +2%, and all six operations are inside the confidence intervals.

**Through the HTTP API, reads cost the same on every engine.** All three engines read with ez-odata's
compiled SQL: the medians are within 0.01 ms. Writes cost more because they run typed entities, hooks and a
transaction. The SimpleCRUD engine adds 0.3–0.6 ms to a write and the EF Core engine 0.4–1.4 ms, and EF Core
allocates about twice as much per write.

Two scenarios to read with care:
- SQLite's `concurrent-16` row (about 1.6 s) measures SQLite's single-writer lock retries, not the engines.
- EF Core × MySQL is not measured: there is no MySqlConnector-based EF Core 10 provider yet.

## What we had to fix to trust these numbers

The first runs looked plausible but were wrong in several ways. Each fix is in this branch.

| Problem | Symptom | Fix |
|---|---|---|
| **404s timed as successes.** ez-odata reads a database's schema once at startup and only logs a failure; that service then answers `404 Unknown service` forever. | EF Core × SQL Server POST showed as "NA"; its GET rows showed an impossible 12 µs (a 404, not a query). | Each session probes its service before use and retries startup (clearing SqlClient's pool). Every benchmark request checks its status. The host now logs to the console. |
| **Warm-up bias.** Engines ran one after another, so later ones met a warmer database, JIT and connection pool. | Whichever engine ran last looked fastest. | Engines take turns request by request after a shared warm-up. |
| **Wrong allocation numbers.** `GC.GetAllocatedBytesForCurrentThread()` read across an `await` misses everything after the thread switch. `GC.Collect()` ran before every sample. | Allocations too low and inconsistent. | Whole-process bytes per request; no forced collections. |
| **A starved Docker VM.** PostgreSQL, MySQL and SQL Server started together in a 2-CPU, 2 GB Colima VM, next to other containers. | SQL Server missed its startup connection (the 404 above), and timings were noisy. | Each database runs in its own process with only its own container. The VM was raised to 8 CPUs and 12 GB. |
| **MySQL could not create the per-engine databases.** The container's application user lacks `CREATE DATABASE`. | The MySQL HTTP phase crashed. | Create as root, then grant the database to the application user. |
| **The MySQL schema had no foreign key.** The other three dialects declared `orders.customer_id → customers.id`; MySQL's did not. | `$expand=orders` returned 400 on MySQL only. | Declared the foreign key. (ez-odata was right: without it, there is no relationship to expand.) |
| **Too few samples for writes.** ShortRun measures 3 iterations. | ±100% error bars on inserts and deletes. | 5 warm-up and 20 measured iterations of about 250 ms each. SQLite runs in WAL mode. |
| **No proof that libraries did the same work.** | Nothing stopped a faster but wrong query from winning. | A correctness gate: before timing, every library must read the same page and round-trip an insert, update and delete, on every database. |
| **Unequal write work in the showcase's quick table.** The handler-backed engines run validation and an audit insert; stock does not. | Stock looked faster at writes. | Labelled on screen. The benchmark gives every engine the same work. |

## What we changed in SimpleCRUD

The allocation gap pointed at SimpleCRUD itself, not at our facade: the facade compiles its delegates once, and
the static API allocates the same bytes. Reading SimpleCRUD's source showed why. On every call:

- `GetScaffoldableProperties<T>()` runs `typeof(T).GetProperties()` and then `GetCustomAttributes(true)` on each
  property, several times, to filter them. `GetCustomAttributes` creates new attribute objects each time.
- `GetIdProperties(type)` does the same to find `[Key]` or `Id`.
- `BuildSelect` has a cached result, but its cache key is a `string.Join` of every property's full name, built
  on every call just to look the cached string up.

None of this changes for a given type. So the fix computes both property lists once per type and gives
`BuildSelect` a per-type key, like SimpleCRUD's other SQL builders already have. It is 30 lines added and 19
removed, with no public API change. Because the lists do not depend on the dialect or the name resolvers,
nothing needs clearing when those change. That keeps the fix independent of
[#282](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/282) (stale dialect quoting); the two merge cleanly.

Both builds were compiled from source into one process and checked for identical results first.
SimpleCRUD's own suite passes: 78/78 with this change, and 79/79 with #282 as well.

| In-memory SQLite | master | with the change | time | allocated |
|---|---:|---:|---:|---:|
| `Get(id)` | 17.54 µs · 12.37 KB | 13.29 µs · 4.66 KB | −24% | −62% |
| `Update` | 19.03 µs · 12.94 KB | 14.96 µs · 6.36 KB | −21% | −51% |
| `GetListPaged` (20 rows) | 37.03 µs · 20.89 KB | 32.42 µs · 13.19 KB | −12% | −37% |
| `GetList(new { Score })` | 111.67 µs · 18.92 KB | 105.30 µs · 5.70 KB | −6% | −70% |

On PostgreSQL the round trip hides the time saved, but allocations still drop by 36–67% on reads and 48% on
Update. On a real server this saves CPU and garbage-collector work under load; it does not make individual
requests faster.

Submitted as [ericdc1/Dapper.SimpleCRUD#283](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/283). Full
tables: [`simplecrud-caching.md`](simplecrud-caching.md). Reproduce:
[`benchmarks/SimpleCrud.CachingAB/run.sh`](../../benchmarks/SimpleCrud.CachingAB/run.sh). It downloads both
versions from GitHub, so it needs nothing from this repository.

## Method

- **Library benchmarks:** BenchmarkDotNet 0.15.2, in process, with the memory diagnoser. Each (library,
  database) case starts from a freshly created table with 5,000 rows, and each operation opens a pooled
  connection, as a web request would.
- **Same work for everyone:** Dapper runs hand-written SQL and reads the new id back (`RETURNING`, `OUTPUT` or
  `LAST_INSERT_ID()`). SimpleCRUD uses `Get`, `GetListPaged`, `Insert`, `Update` and `Delete`. EF Core uses a
  pooled `DbContext` factory with one context per operation.
- **HTTP:** one TestServer per engine and database, 200 interleaved samples per scenario after 20 warm-up rounds;
  median, p95 and p99.
- **Code:** [`benchmarks/EzOdata.Entities.Benchmarks`](../../benchmarks/EzOdata.Entities.Benchmarks)
  (`DataAccessBenchmarks.cs`, `QuickHarness.cs`, `ReportBuilder.cs`).

These are microbenchmarks on one machine. Treat differences inside the confidence interval as equal, and rerun
on your own hardware.
