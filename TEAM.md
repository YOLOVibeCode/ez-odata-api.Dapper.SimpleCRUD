# To the team,

## Why this note

I wrote [a note to Eric](ERIC.md) for the same reason I'm writing this one. Instead of the usual interview prep,
I spent the time working in Dapper.SimpleCRUD's code. It seemed the most honest way to show how I work. This note
covers what came of it, so you don't have to dig through pull requests to find out.

**Nothing here needs anything from you.** If it's useful to talk through any of it today, I'd enjoy that.

## Two pull requests to SimpleCRUD

**[#282](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/282): switching dialects reuses stale quoting.**
SimpleCRUD caches table and column names already quoted for the dialect active at first use, and `SetDialect`
never clears them. After the first query, a switch from SQL Server to PostgreSQL sends `[brackets]` to PostgreSQL.
The fix is 16 lines and clears the caches only when the dialect or a name resolver actually changes. I wrote the
failing test first; it fails on unpatched code and passes with the fix.

**[#283](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/283): an enhancement that computes the property lists
once per type.** A benchmark of SimpleCRUD against hand-written Dapper and EF Core showed SimpleCRUD allocating 3.4×
what Dapper does on a Get by id. Measuring SimpleCRUD's own static API next to my wrapper gave the same numbers, so the
opportunity was inside SimpleCRUD itself. On every call, `GetScaffoldableProperties<T>()` and `GetIdProperties(type)`
scan the entity's properties and attributes by reflection, and the answer never changes for a type. The enhancement
computes them once per type and reuses them: 30 lines added, 19 removed, and no public API change.

### It builds on Dave's #281

[#281](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/281) taught `GetScaffoldableProperties<T>()` to include
`IConvertible` properties and properties marked `[Key]` or `[Column]`. That's what lets a strongly typed ID like
`OfficeId` work through a Dapper `TypeHandler`. #283 caches exactly that function. The rule is kept as it is, now
evaluated once per type rather than on every call. The two work together: #281 made the rule richer, and #283 makes it cost nothing after the first call.

The suite run I used to verify both PRs registers the `OfficeId` handler and includes #281's two tests:
`Tests_Insert_GetList_WithOfficeId` and `Tests_Update_Get_WithOfficeId`.

### What #283 measured

**SimpleCRUD's `master` against `master` plus #283**, compiled side by side into one process, on in-memory SQLite:

| Operation | master | with #283 | time | allocated |
|---|---:|---:|---:|---:|
| `Get(id)` | 17.54 µs · 12.37 KB | 13.29 µs · 4.66 KB | −24% | −62% |
| `Update` | 19.03 µs · 12.94 KB | 14.96 µs · 6.36 KB | −21% | −51% |
| `GetListPaged` (20 rows) | 37.03 µs · 20.89 KB | 32.42 µs · 13.19 KB | −12% | −37% |
| `GetList(new { Score })` | 111.67 µs · 18.92 KB | 105.30 µs · 5.70 KB | −6% | −70% |

**Against EF Core, before and after, in a full application on SQLite:**

| | Dapper, hand-written | SimpleCRUD before | SimpleCRUD after | EF Core |
|---|---:|---:|---:|---:|
| Get by id | 5.1 µs | 11.9 µs | **5.8 µs** | 17.1 µs |
| Allocated per Get | 3.1 KB | 12.9 KB | **5.1 KB** | 12.2 KB |

With the enhancement, SimpleCRUD's Get is within 14% of hand-written Dapper and three times faster than EF Core.

**Two honest limits:**
- **On a networked database the round trip dominates.** On PostgreSQL, Get takes about 350 µs whichever way, so
  the time saved disappears into it. What remains is 36–67% less garbage on reads: a CPU and garbage-collector win
  under load, not faster responses.
- **The two measurements differ.** The A/B test shows −24% on Get, the full application −52%. The setups differ:
  in-memory SQLite with an 8-property entity, against file SQLite with 5 properties. The allocation saving is about
  60% in both.

### How I checked it

- **SimpleCRUD's own suite:** 78/78 with #283, and 79/79 with #282 and #283 together. The two PRs merge cleanly.
- **A correctness gate before any timing:** both builds must return identical rows and leave identical data, on
  SQLite and PostgreSQL.
- **The whole application on both builds:** a 44-check tour of the API passes on each. File hashes prove which
  build was loaded.
- **Reproducible without my repository's code:**
  [`benchmarks/SimpleCrud.CachingAB/run.sh`](benchmarks/SimpleCrud.CachingAB/run.sh) downloads both versions of
  SimpleCRUD from GitHub and reruns the comparison.

## What I built on SimpleCRUD

[This repository](README.md) turns a database into an instant OData/REST API with Swagger, using
[ez-odata-api](https://github.com/YOLOVibeCode/ez-odata-api). Any table can be taken over by an ordinary
SimpleCRUD class with typed hooks (validation, auditing in the same transaction, soft delete). Each database
dialect gets its own isolated copy of SimpleCRUD, so several dialects run in one process without changing
SimpleCRUD at all.

- **See it run:** `./try.sh` (`try.cmd` on Windows) goes from a fresh clone to the live API, Swagger UI and a
  narrated tour. It installs the .NET 10 SDK locally if it's missing.
- **The benchmarks:** [the report](https://yolovibecode.github.io/ez-odata-api.Dapper.SimpleCRUD/benchmarks/report.html),
  the method and its caveats in [`docs/benchmarks`](docs/benchmarks/README.md), and
  [the drop-in analysis](docs/benchmarks/drop-in.md): what adding the package to a new project costs.

One thing I haven't covered with a test yet: the isolated copies share the application's Dapper, so type handlers
registered with `SqlMapper.AddTypeHandler` should apply to them by design. There's no test proving it yet.

## If it's helpful today

I'm glad to walk through any of it: the PRs, the load-context approach, or how the benchmark avoids fooling
itself. It would also be good to hear what your data layer looks like today, and where the friction is.

Thank you for your time.

**rvegajr** · Noctusoft
[github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD](https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD)
