# Dapper, Dapper.SimpleCRUD and EF Core: benchmark report

Generated 2026-09-30 21:50 UTC. Lower is better. ± is the 99.9% confidence interval (BenchmarkDotNet).

## Findings

- **Dapper.SimpleCRUD** against hand-written Dapper (geometric mean across databases): get by id 1.24×, filtered page (20 rows) 1.10×, insert one 1.04×, update one 1.10×, insert + delete 1.05×, insert 100 (one transaction) 1.38×.
- **EF Core** against hand-written Dapper (geometric mean across databases): get by id 1.60×, filtered page (20 rows) 1.41×, insert one 1.21×, update one 1.20×, insert + delete 1.15×, insert 100 (one transaction) 0.30×.
- **The EzOdata facade** (SimpleCRUD in an isolated copy per dialect) against SimpleCRUD's static API on SQLite: get by id +1%, filtered page (20 rows) +1%, insert one +2%, update one -6%, insert + delete -2%, insert 100 (one transaction) +0%; 6 of 6 are inside the confidence intervals.
- **EF Core change tracking** on reads, relative to Dapper: get by id 1.60× tracked vs 1.61× AsNoTracking, filtered page (20 rows) 1.41× tracked vs 1.35× AsNoTracking.
- **Allocations** per operation, SimpleCRUD / EF Core relative to Dapper: get by id 3.4× / 2.8×, filtered page (20 rows) 2.2× / 3.4×, insert one 1.7× / 3.1×, update one 2.7× / 3.4×, insert + delete 1.8× / 3.6×, insert 100 (one transaction) 3.2× / 3.6×.
- **Reads through the HTTP API** take the same path on every engine (ez-odata's compiled SQL); median difference from stock ez-odata, SimpleCRUD / EF Core engine: get-by-key +0.01 ms / +0.01 ms, list +0.01 ms / 0.00 ms, expand 0.00 ms / 0.00 ms.
- **Writes through the HTTP API** (typed entity, hooks, one transaction), median difference from stock ez-odata, SimpleCRUD / EF Core engine: post +0.30 ms / +1.04 ms, patch +0.63 ms / +1.44 ms, put +0.58 ms / +0.36 ms.
- **Tests**: 71 passed, 0 failed, 0 skipped.

## The libraries on their own

A pooled connection per operation, as a web request would. Same table, same 5,000 seeded rows, same results (verified before timing). Each cell: mean ± error (ratio to Dapper), then bytes allocated.

### Get by id

| Database | Dapper, hand-written SQL | Dapper.SimpleCRUD (EzOdata facade) | Dapper.SimpleCRUD (static API) | EF Core | EF Core, AsNoTracking |
|---|---:|---:|---:|---:|---:|
| SQLite | 5.1 µs ± 45 ns<br/>3.1 KB | 12.0 µs ± 100 ns (2.35×)<br/>12.9 KB | 11.9 µs ± 63 ns (2.32×)<br/>12.9 KB | 18.3 µs ± 298 ns (3.59×)<br/>12.1 KB | 18.6 µs ± 342 ns (3.64×)<br/>12.6 KB |
| PostgreSQL | 324 µs ± 25.8 µs<br/>3.5 KB | 342 µs ± 25.8 µs (1.06×)<br/>13.5 KB | — | 358 µs ± 28.9 µs (1.11×)<br/>9.3 KB | 349 µs ± 23.5 µs (1.08×)<br/>9.8 KB |
| MySQL | 606 µs ± 72.0 µs<br/>8.0 KB | 584 µs ± 47.9 µs (0.96×)<br/>17.5 KB | — | — | — |
| SQL Server | 703 µs ± 35.8 µs<br/>8.0 KB | 704 µs ± 35.2 µs (1.00×)<br/>17.9 KB | — | 724 µs ± 45.5 µs (1.03×)<br/>14.1 KB | 741 µs ± 54.1 µs (1.05×)<br/>14.6 KB |

### Filtered page (20 rows)

| Database | Dapper, hand-written SQL | Dapper.SimpleCRUD (EzOdata facade) | Dapper.SimpleCRUD (static API) | EF Core | EF Core, AsNoTracking |
|---|---:|---:|---:|---:|---:|
| SQLite | 18.1 µs ± 82 ns<br/>8.1 KB | 26.0 µs ± 142 ns (1.44×)<br/>19.2 KB | 25.7 µs ± 185 ns (1.43×)<br/>19.2 KB | 41.8 µs ± 458 ns (2.32×)<br/>32.0 KB | 37.0 µs ± 293 ns (2.05×)<br/>23.7 KB |
| PostgreSQL | 418 µs ± 34.4 µs<br/>8.5 KB | 413 µs ± 18.8 µs (0.99×)<br/>19.7 KB | — | 478 µs ± 21.5 µs (1.14×)<br/>29.3 KB | 455 µs ± 17.2 µs (1.09×)<br/>21.0 KB |
| MySQL | 676 µs ± 49.4 µs<br/>13.0 KB | 671 µs ± 61.0 µs (0.99×)<br/>23.4 KB | — | — | — |
| SQL Server | 750 µs ± 39.3 µs<br/>13.2 KB | 788 µs ± 45.8 µs (1.05×)<br/>25.7 KB | — | 793 µs ± 20.3 µs (1.06×)<br/>35.1 KB | 832 µs ± 86.9 µs (1.11×)<br/>26.8 KB |

### Insert one

| Database | Dapper, hand-written SQL | Dapper.SimpleCRUD (EzOdata facade) | Dapper.SimpleCRUD (static API) | EF Core |
|---|---:|---:|---:|---:|
| SQLite | 44.5 µs ± 1.1 µs<br/>4.9 KB | 50.5 µs ± 1.5 µs (1.13×)<br/>8.4 KB | 49.5 µs ± 1.9 µs (1.11×)<br/>8.4 KB | 62.5 µs ± 1.4 µs (1.41×)<br/>16.1 KB |
| PostgreSQL | 335 µs ± 20.2 µs<br/>4.0 KB | 344 µs ± 40.4 µs (1.03×)<br/>7.2 KB | — | 414 µs ± 71.9 µs (1.23×)<br/>14.1 KB |
| MySQL | 1.12 ms ± 103 µs<br/>6.6 KB | 1.08 ms ± 52.5 µs (0.97×)<br/>10.2 KB | — | — |
| SQL Server | 1.12 ms ± 121 µs<br/>8.4 KB | 1.16 ms ± 28.5 µs (1.03×)<br/>11.9 KB | — | 1.14 ms ± 43.0 µs (1.02×)<br/>19.6 KB |

### Update one

| Database | Dapper, hand-written SQL | Dapper.SimpleCRUD (EzOdata facade) | Dapper.SimpleCRUD (static API) | EF Core |
|---|---:|---:|---:|---:|
| SQLite | 37.1 µs ± 1.2 µs<br/>3.7 KB | 44.8 µs ± 1.2 µs (1.21×)<br/>11.6 KB | 47.5 µs ± 2.2 µs (1.28×)<br/>11.6 KB | 55.0 µs ± 1.5 µs (1.48×)<br/>16.0 KB |
| PostgreSQL | 352 µs ± 23.7 µs<br/>4.4 KB | 386 µs ± 42.6 µs (1.10×)<br/>12.4 KB | — | 385 µs ± 50.2 µs (1.09×)<br/>14.0 KB |
| MySQL | 1.07 ms ± 45.1 µs<br/>6.4 KB | 1.11 ms ± 53.2 µs (1.04×)<br/>14.1 KB | — | — |
| SQL Server | 1.08 ms ± 114 µs<br/>7.3 KB | 1.16 ms ± 46.1 µs (1.08×)<br/>15.3 KB | — | 1.14 ms ± 67.5 µs (1.06×)<br/>19.3 KB |

### Insert + delete

| Database | Dapper, hand-written SQL | Dapper.SimpleCRUD (EzOdata facade) | Dapper.SimpleCRUD (static API) | EF Core |
|---|---:|---:|---:|---:|
| SQLite | 82.4 µs ± 2.0 µs<br/>6.7 KB | 86.9 µs ± 3.7 µs (1.05×)<br/>12.9 KB | 89.0 µs ± 3.5 µs (1.08×)<br/>12.9 KB | 109 µs ± 3.9 µs (1.32×)<br/>29.4 KB |
| PostgreSQL | 669 µs ± 60.0 µs<br/>6.3 KB | 713 µs ± 68.4 µs (1.07×)<br/>12.2 KB | — | 721 µs ± 70.7 µs (1.08×)<br/>24.6 KB |
| MySQL | 2.06 ms ± 64.9 µs<br/>11.7 KB | 2.07 ms ± 29.4 µs (1.00×)<br/>18.0 KB | — | — |
| SQL Server | 2.13 ms ± 128 µs<br/>13.5 KB | 2.32 ms ± 101 µs (1.09×)<br/>19.5 KB | — | 2.28 ms ± 180 µs (1.07×)<br/>35.0 KB |

### Insert 100 (one transaction)

| Database | Dapper, hand-written SQL | Dapper.SimpleCRUD (EzOdata facade) | Dapper.SimpleCRUD (static API) | EF Core |
|---|---:|---:|---:|---:|
| SQLite | 318 µs ± 4.0 µs<br/>185.7 KB | 979 µs ± 20.0 µs (3.08×)<br/>750.7 KB | 975 µs ± 21.1 µs (3.06×)<br/>744.4 KB | 1.10 ms ± 14.3 µs (3.46×)<br/>923.1 KB |
| PostgreSQL | 31.40 ms ± 4.64 ms<br/>197.4 KB | 33.44 ms ± 3.93 ms (1.06×)<br/>553.0 KB | — | 2.63 ms ± 93.4 µs (0.08×)<br/>768.7 KB |
| MySQL | 23.70 ms ± 2.85 ms<br/>262.7 KB | 24.91 ms ± 2.02 ms (1.05×)<br/>762.3 KB | — | — |
| SQL Server | 64.20 ms ± 2.56 ms<br/>404.0 KB | 67.99 ms ± 2.97 ms (1.06×)<br/>1.04 MB | — | 6.02 ms ± 476 µs (0.09×)<br/>780.3 KB |

## Through the HTTP API

In-memory TestServer. Engines interleaved request by request. Median / p95, then bytes allocated per request.

### SQLite

| Scenario | stock ez-odata | SimpleCRUD engine | EF Core engine |
|---|---:|---:|---:|
| post | 0.53 / 0.74 ms<br/>56.5 KB | 0.56 / 0.76 ms<br/>64.8 KB | 0.76 / 1.06 ms<br/>128.8 KB |
| get-by-key | 0.14 / 0.21 ms<br/>53.1 KB | 0.14 / 0.18 ms<br/>54.9 KB | 0.15 / 0.19 ms<br/>55.2 KB |
| list | 0.25 / 0.33 ms<br/>98.2 KB | 0.26 / 0.34 ms<br/>99.0 KB | 0.25 / 0.33 ms<br/>99.1 KB |
| expand | 0.22 / 0.42 ms<br/>80.7 KB | 0.23 / 0.44 ms<br/>83.0 KB | 0.22 / 0.36 ms<br/>82.6 KB |
| patch | 0.49 / 0.94 ms<br/>55.8 KB | 0.55 / 0.97 ms<br/>79.9 KB | 0.80 / 1.38 ms<br/>141.0 KB |
| put | 0.19 / 0.26 ms<br/>58.3 KB | 0.22 / 0.30 ms<br/>80.1 KB | 0.35 / 0.49 ms<br/>132.7 KB |
| delete-post | 0.87 / 1.40 ms<br/>89.3 KB | 0.93 / 1.42 ms<br/>117.1 KB | 1.31 / 2.36 ms<br/>243.4 KB |
| concurrent-16 | 1659.49 / 2114.41 ms<br/>921.7 KB | 1516.28 / 2113.67 ms<br/>1.02 MB | 1667.12 / 2115.38 ms<br/>2.00 MB |
| hook-audit | — | 0.30 / 0.45 ms<br/>68.0 KB | 0.38 / 0.56 ms<br/>137.5 KB |
| direct-insert | — | 0.31 / 0.39 ms<br/>7.8 KB | 0.35 / 0.47 ms<br/>70.5 KB |

### PostgreSQL

| Scenario | stock ez-odata | SimpleCRUD engine | EF Core engine |
|---|---:|---:|---:|
| post | 1.00 / 1.26 ms<br/>60.8 KB | 1.41 / 1.86 ms<br/>69.5 KB | 2.22 / 2.81 ms<br/>131.1 KB |
| get-by-key | 0.56 / 0.70 ms<br/>59.3 KB | 0.57 / 0.75 ms<br/>61.5 KB | 0.57 / 0.72 ms<br/>61.0 KB |
| list | 0.72 / 0.83 ms<br/>103.8 KB | 0.73 / 0.84 ms<br/>105.1 KB | 0.72 / 0.85 ms<br/>105.6 KB |
| expand | 1.09 / 1.74 ms<br/>91.3 KB | 1.09 / 1.82 ms<br/>93.3 KB | 1.08 / 1.66 ms<br/>92.7 KB |
| patch | 0.89 / 1.36 ms<br/>59.9 KB | 1.65 / 2.63 ms<br/>84.2 KB | 2.58 / 3.86 ms<br/>142.4 KB |
| put | 0.79 / 0.97 ms<br/>62.1 KB | 1.49 / 1.88 ms<br/>84.9 KB | 1.31 / 1.63 ms<br/>129.9 KB |
| delete-post | 1.55 / 1.89 ms<br/>98.5 KB | 2.28 / 2.94 ms<br/>125.5 KB | 3.72 / 4.42 ms<br/>242.3 KB |
| concurrent-16 | 1.91 / 3.65 ms<br/>1.11 MB | 2.60 / 4.64 ms<br/>1.19 MB | 4.62 / 7.60 ms<br/>2.11 MB |
| hook-audit | — | 1.95 / 3.25 ms<br/>71.1 KB | 3.62 / 6.23 ms<br/>142.1 KB |
| direct-insert | — | 0.39 / 0.53 ms<br/>6.2 KB | 0.44 / 0.62 ms<br/>62.4 KB |

### MySQL

| Scenario | stock ez-odata | SimpleCRUD engine | EF Core engine |
|---|---:|---:|---:|
| post | 1.98 / 2.43 ms<br/>69.6 KB | 2.04 / 2.48 ms<br/>73.2 KB | — |
| get-by-key | 0.82 / 0.97 ms<br/>61.0 KB | 0.83 / 0.97 ms<br/>62.5 KB | — |
| list | 1.03 / 1.43 ms<br/>105.9 KB | 1.04 / 1.46 ms<br/>106.3 KB | — |
| expand | 1.58 / 1.87 ms<br/>93.2 KB | 1.59 / 1.91 ms<br/>96.2 KB | — |
| patch | 1.87 / 3.19 ms<br/>67.8 KB | 2.24 / 3.88 ms<br/>90.1 KB | — |
| put | 1.55 / 2.24 ms<br/>70.0 KB | 1.90 / 2.96 ms<br/>90.5 KB | — |
| delete-post | 3.52 / 7.14 ms<br/>106.9 KB | 3.97 / 9.21 ms<br/>133.5 KB | — |
| concurrent-16 | 4.62 / 6.80 ms<br/>1.13 MB | 4.65 / 7.15 ms<br/>1.21 MB | — |
| hook-audit | — | 2.63 / 3.41 ms<br/>76.3 KB | — |
| direct-insert | — | 1.11 / 1.43 ms<br/>9.1 KB | — |

### SQL Server

| Scenario | stock ez-odata | SimpleCRUD engine | EF Core engine |
|---|---:|---:|---:|
| post | 2.61 / 4.99 ms<br/>64.4 KB | 3.30 / 6.22 ms<br/>75.5 KB | 4.27 / 7.69 ms<br/>173.2 KB |
| get-by-key | 0.82 / 1.34 ms<br/>59.8 KB | 0.83 / 1.45 ms<br/>61.5 KB | 0.84 / 1.40 ms<br/>60.9 KB |
| list | 0.97 / 1.27 ms<br/>104.5 KB | 0.99 / 1.17 ms<br/>104.2 KB | 0.98 / 1.28 ms<br/>104.9 KB |
| expand | 1.52 / 1.77 ms<br/>92.1 KB | 1.51 / 1.79 ms<br/>94.1 KB | 1.52 / 1.84 ms<br/>93.9 KB |
| patch | 2.47 / 2.89 ms<br/>63.4 KB | 3.81 / 4.41 ms<br/>94.8 KB | 4.77 / 5.50 ms<br/>187.6 KB |
| put | 2.50 / 3.59 ms<br/>65.4 KB | 3.75 / 5.53 ms<br/>95.4 KB | 2.90 / 3.90 ms<br/>170.5 KB |
| delete-post | 5.10 / 6.49 ms<br/>101.8 KB | 6.60 / 8.94 ms<br/>137.9 KB | 8.50 / 10.81 ms<br/>326.2 KB |
| concurrent-16 | 8.97 / 16.03 ms<br/>1.07 MB | 11.62 / 20.28 ms<br/>1.23 MB | 14.77 / 26.40 ms<br/>2.72 MB |
| hook-audit | — | 4.23 / 5.39 ms<br/>82.7 KB | 5.85 / 7.67 ms<br/>189.9 KB |
| direct-insert | — | 1.22 / 1.45 ms<br/>10.9 KB | 1.32 / 1.58 ms<br/>101.6 KB |

## How this was measured

- **Correctness first.** Before any timing, every library reads the same 20-row page, then inserts, reads back, updates and deletes a row, on every database; the run stops on any difference.
- **One database at a time.** Each database runs in its own process with only its own container started (PostgreSQL 16, MySQL 8.4, SQL Server 2022, or Azure SQL Edge on ARM), so they do not compete for the Docker VM. SQLite is a local file in WAL mode.
- **Library benchmarks** use BenchmarkDotNet in process: 5 warm-up and 20 measured iterations of about 250 ms each, with the memory diagnoser. Each (library, database) case starts from a freshly created table with 5,000 rows.
- **Same work for everyone.** Dapper runs hand-written SQL and reads the new id back (RETURNING, OUTPUT or LAST_INSERT_ID). SimpleCRUD uses Get, GetListPaged, Insert, Update and Delete. EF Core uses a pooled DbContext factory with one context per operation, as ASP.NET Core does. `Insert 100` is one transaction: SimpleCRUD inserts row by row and returns every id, EF Core batches, and Dapper's list form runs row by row without returning ids.
- **SimpleCRUD twice.** The EzOdata facade runs SimpleCRUD in an isolated copy per dialect; SimpleCRUD's own static API is measured next to it on SQLite (its dialect is process-wide, so one database only) to show what the facade costs.
- **EF Core × MySQL** is not measured: there is no MySqlConnector-based EF Core 10 provider yet.
- **SQLite has one writer.** Its `concurrent-16` row measures SQLite's lock retries (about 1.6 s per batch of 16 inserts), not the engines.
- **HTTP numbers** come from an in-memory TestServer (no sockets), so they show the API layer plus the database, not the network. Engines are interleaved request by request so that none benefits from running later; allocations are whole-process bytes per request.
- Microbenchmarks on one machine: treat differences inside the confidence interval as equal, and rerun with `./try.sh --benchmark` on your own hardware.

## Environment

- OS: macOS 26.6.2
- arch: Arm64
- cores: 16
- .NET: .NET 10.0.1
- Dapper: 2.0.78
- Dapper.SimpleCRUD: 2.3.0
- EF Core: 10.0.0
- ez-odata-api: 1.0.7
- EzOdata.SimpleCrud: 2.0.1
- drivers: Npgsql 10.0.0, MySqlConnector 2.3.7, SqlClient 6.1.1, Microsoft.Data.Sqlite 10.0.0
