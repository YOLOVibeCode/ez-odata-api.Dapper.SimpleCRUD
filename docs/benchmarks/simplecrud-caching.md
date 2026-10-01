# Dapper.SimpleCRUD: cache per-type property lists (A/B)

Submitted upstream as [ericdc1/Dapper.SimpleCRUD#283](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/283).
Reproduce with [`benchmarks/SimpleCrud.CachingAB/run.sh`](../../benchmarks/SimpleCrud.CachingAB/run.sh).

Branch `perf/cache-type-metadata` off `master` (62a5431), +30/-19 lines in SimpleCRUD.cs / SimpleCRUDAsync.cs:
- `GetScaffoldableProperties<T>()` and `GetIdProperties(Type)` are computed once per type (ConcurrentDictionary).
  They depend only on the type, not on the dialect or name resolvers, so nothing needs clearing.
- `BuildSelect<T>` is keyed by type like the other builders (the old key joined every property name per call).

Correctness: SimpleCRUD's own suite on SQLite: 78 passed, 0 failed with this change, and 79/79 together with #282. Before timing, both builds
returned identical rows (Get, GetList, GetListPaged, RecordCount) and wrote identical data (Insert, Update, Delete)
on SQLite and PostgreSQL.

Method: both builds compiled from source in one process (extern alias), BenchmarkDotNet 0.15.2, in process,
5 warm-up + 20 x 250 ms iterations, MemoryDiagnoser. .NET 10.0.1, Apple M4 Max. Entity with 8 properties
([Table], [Key], one [Column]); connection per call; 5,000 rows.

## SQLite in memory (library cost visible)

| Operation | master | cached | time | allocated |
|---|---:|---:|---:|---:|
| Get(id) | 17.54 us, 12.37 KB | 13.29 us, 4.66 KB | -24% | -62% |
| GetList(new { Score }) (5 rows) | 111.67 us, 18.92 KB | 105.30 us, 5.70 KB | -6% | -70% |
| GetListPaged (20 rows) | 37.03 us, 20.89 KB | 32.42 us, 13.19 KB | -12% | -37% |
| Insert | 19.32 us, 9.38 KB | 17.66 us, 8.84 KB | -9% | -6% |
| Update | 19.03 us, 12.94 KB | 14.96 us, 6.36 KB | -21% | -51% |
| Insert + Delete(id) | 23.53 us, 12.01 KB | 20.38 us, 10.95 KB | -13% | -9% |

Errors are 0.1-0.3 us: every time difference is well outside the confidence interval.

## PostgreSQL 16 (Docker, ~350 us round trip)

| Operation | master alloc | cached alloc | allocated | time |
|---|---:|---:|---:|---|
| Get(id) | 13.08 KB | 5.37 KB | -59% | within noise (round trip dominates) |
| GetList(new { Score }) | 19.59 KB | 6.37 KB | -67% | within noise |
| GetListPaged (20 rows) | 21.50 KB | 13.79 KB | -36% | within noise |
| Insert | 7.54 KB | 7.00 KB | -7% | within noise |
| Update | 13.87 KB | 7.28 KB | -48% | within noise |
| Insert + Delete(id) | 10.54 KB | 9.48 KB | -10% | within noise |
