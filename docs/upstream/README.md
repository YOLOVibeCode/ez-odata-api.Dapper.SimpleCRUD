# Upstream contributions to Dapper.SimpleCRUD

These are drafts for [ericdc1/Dapper.SimpleCRUD](https://github.com/ericdc1/Dapper.SimpleCRUD).
Nothing here has been posted. Each item is small, and each was verified against `master`
(`62a5431`, 2026-08-21) and the released 2.3.0 package.

---

## PR 1: rebuild cached names when the dialect or a resolver actually changes

**Patch:** [`0001-SetDialect-clears-cached-names.patch`](0001-SetDialect-clears-cached-names.patch)
(16 lines in `SimpleCRUD.cs` plus one test in `Tests.cs`, CRLF preserved, no public API change).
Apply it with `git apply`.

### The bug

`TableNames`, `ColumnNames` and `StringBuilderCacheDict` store names *already quoted* for the dialect
active at first use, and nothing clears them. After the first query:

1. **Switching dialects produces mixed SQL.** SQL Server `[brackets]` end up inside PostgreSQL's
   `LIMIT` paging, and PostgreSQL rejects them.
2. **Setting a resolver after first use has no effect.** Cached names win.

### The fix

Clear the three caches when `SetDialect` changes the dialect, or when a resolver is replaced.
Clearing happens **only on a real change**: on #56 the recommended pattern is to call `SetDialect`
whenever a connection opens, and that pattern keeps its caching.

### How we reproduced and verified it

| Check | 2.3.0 (NuGet) | `master` 62a5431 | `master` + patch |
|---|---|---|---|
| [Real servers](proof/real-servers/Program.cs): SQL Server, then `SetDialect(PostgreSQL)`, then `Get`/`Insert` on PostgreSQL | ❌ `42601: syntax error at or near "["` | ❌ same | ✅ |
| New test `TestChangeDialectRebuildsCachedNames` | ❌ `0 should be equal to 1` | ❌ | ✅ |
| His full SQLite suite ([runner](proof/suite-runner/Program.cs); x64 under Rosetta, because `System.Data.SQLite` has no osx-arm64 build) | n/a | 78/78 | 79/79 (the new test included) |
| `Dapper.SimpleCRUDTests` builds (net8.0) | n/a | ✅ | ✅ (same pre-existing CA1416 warning) |

His `Program.Main` runs SQL Server and then SQLite in one process, and SQLite accepts `[brackets]`.
That's likely why the suite never caught this.

### The test (added after `TestChangeDialect`)

```csharp
public void TestChangeDialectRebuildsCachedNames()
{
    var resolver = new CountingTableNameResolver();
    SimpleCRUD.SetTableNameResolver(resolver);
    using (var connection = GetOpenConnection())
    {
        connection.RecordCount<User>();
        connection.RecordCount<User>();
        resolver.Calls.IsEqualTo(1); //second call is served from the cache

        SimpleCRUD.SetDialect(_dbtype); //same dialect: cache is kept
        connection.RecordCount<User>();
        resolver.Calls.IsEqualTo(1);

        var other = _dbtype == SimpleCRUD.Dialect.SQLServer ? SimpleCRUD.Dialect.PostgreSQL : SimpleCRUD.Dialect.SQLServer;
        SimpleCRUD.SetDialect(other); //a real switch rebuilds names with the new quoting
        SimpleCRUD.SetDialect(_dbtype);
        connection.RecordCount<User>();
        resolver.Calls.IsEqualTo(2);
    }
    SimpleCRUD.SetTableNameResolver(new SimpleCRUD.TableNameResolver());
}
```

This makes sequential switching correct. It does **not** make concurrent use of two dialects safe,
because the dialect is still one static value. That is item 2.

---

## Item 2: multiple dialects at once (#56, #218, #231, #233)

This question has come up four times since 2015. On #218 (2021) Eric wrote:
*"Sorry but this usecase is beyond the intent of simplecrud. I would consider a PR that solves this
if it isn't too far reaching."*

`EzOdata.SimpleCrud` solves it **outside** SimpleCRUD, with SimpleCRUD unmodified. Each dialect gets
its own copy of the assembly in a separate `AssemblyLoadContext`, so each copy has its own statics.
That's a zero-risk answer for him: nothing in SimpleCRUD changes.

### Proposed comment (on #218, or in PR 1's description)

> For anyone who lands here: running several dialects at the same time is possible today without
> changing SimpleCRUD. Load one copy of the assembly per dialect, each in its own
> `AssemblyLoadContext`. SimpleCRUD matches attributes by type name, so the same POCOs work with
> every copy. Dapper stays shared, so type handlers still apply. The overhead is about 1.06× a direct
> call. [`EzOdata.SimpleCrud`](link) packages this as `SimpleCrud.For(Dialect.PostgreSQL)`; it's
> tested against SQL Server, PostgreSQL, MySQL and SQLite concurrently in one process.

### Only if he's interested later

The facade reads five private helpers (`GetTableName`, `GetColumnName`, `GetScaffoldableProperties`,
`GetUpdateableProperties` and `BuildInsertParameters`) so that integrations use exactly SimpleCRUD's
mapping rules. A small public read-only API, for example `SimpleCRUD.GetTableName(Type)` and
`SimpleCRUD.GetColumnName(PropertyInfo)`, would remove that reflection. Suggest it only if he asks
what would help.
