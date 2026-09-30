# Upstream contributions to Dapper.SimpleCRUD

These are drafts for [ericdc1/Dapper.SimpleCRUD](https://github.com/ericdc1/Dapper.SimpleCRUD).
Nothing here has been posted. Each item is small, and each was verified against `master`
(`62a5431`, 2026-08-21) and the released 2.3.0 package.

---

## PR 1: `SetDialect` and `Set*NameResolver` rebuild cached names

**Patch:** [`0001-SetDialect-clears-cached-names.patch`](0001-SetDialect-clears-cached-names.patch)
(13 lines added, one file, CRLF preserved)

### Proposed PR text

> **Title:** Rebuild cached table/column names when the dialect or a name resolver changes
>
> Hi Eric, thanks for SimpleCRUD. I found a small caching issue while building on top of it.
>
> `TableNames`, `ColumnNames` and `StringBuilderCacheDict` store names *already quoted* for the
> dialect that was active at first use, and nothing clears them. After the first query:
>
> 1. **Switching dialects produces mixed SQL.** On 2.3.0:
>    ```csharp
>    SimpleCRUD.SetDialect(SimpleCRUD.Dialect.SQLServer);
>    conn.GetList<Widget>(new { Name = "a" });
>    SimpleCRUD.SetDialect(SimpleCRUD.Dialect.PostgreSQL);
>    conn.GetListPaged<Widget>(1, 10, "", "Name");
>    // Select [Id],[Name] from [Widget]  Order By Name LIMIT 10 OFFSET ((1-1) * 10)
>    //        ^ SQL Server quoting            ^ PostgreSQL paging, which PostgreSQL rejects
>    ```
> 2. **Setting a resolver after first use has no effect.** `SetTableNameResolver` and
>    `SetColumnNameResolver` are never consulted for types that are already cached.
>
> The fix clears the three caches in `SetDialect`, `SetTableNameResolver` and
> `SetColumnNameResolver`. It's 13 lines and changes no public API. The static constructor's
> `SetDialect` call is safe, because field initializers run first.
>
> It doesn't show up in the existing suite because `Program.Main` runs SQL Server and then SQLite
> in one process, and SQLite happens to accept `[bracket]` quoting.
>
> A test in the style of `Tests.cs` (it fails on 2.3.0 and passes with the patch):
>
> ```csharp
> public void TestChangeDialectRebuildsCachedNames()
> {
>     var resolver = new CountingTableNameResolver();
>     SimpleCRUD.SetTableNameResolver(resolver);
>     using (var connection = GetOpenConnection())
>     {
>         connection.RecordCount<User>();
>         connection.RecordCount<User>();
>         resolver.Calls.IsEqualTo(1);          // second call served from the cache
>         SimpleCRUD.SetDialect(_dbtype);
>         connection.RecordCount<User>();
>         resolver.Calls.IsEqualTo(2);          // rebuilt after SetDialect
>     }
>     SimpleCRUD.SetTableNameResolver(new SimpleCRUD.TableNameResolver());
> }
>
> private class CountingTableNameResolver : SimpleCRUD.TableNameResolver
> {
>     public int Calls;
>     public override string ResolveTableName(Type type) { Calls++; return base.ResolveTableName(type); }
> }
> ```

**Verified:** the repro emits `"Id","Name" from "Widget" … LIMIT` with the patch. The counting test
fails on 2.3.0 (0 resolver calls) and passes with the patch (1, then 2). SimpleCRUD's own
`Program.Main` harness was not run: it uses Windows paths and `Console.ReadKey`.

This PR makes sequential switching correct. It does **not** make concurrent use of two dialects
safe, because the dialect is still one static value. That is item 2.

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
