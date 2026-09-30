# Eric,

Thank you for Dapper.SimpleCRUD. For more than ten years it has given a lot of us the rare thing: a
data layer small enough to understand in one sitting, and good enough to trust in production. I
know every issue and PR takes time from a project you maintain on your own, so I'll keep this short.
**Nothing here needs anything from you.** It's a thank-you with a fix attached.

## A small fix, verified: [PR #PR_NUMBER](https://github.com/ericdc1/Dapper.SimpleCRUD/pulls/rvegajr)

While building on SimpleCRUD, I found that `SetDialect` never clears the name caches
(`TableNames`, `ColumnNames`, `StringBuilderCacheDict`). After the first query, switching dialects
emits the old quoting inside the new dialect's SQL:

```
SetDialect(SQLServer)   → Get<Widget>(1) on SQL Server   ✓
SetDialect(PostgreSQL)  → Get<Widget>(1) on PostgreSQL   ✗ 42601: syntax error at or near "["
```

A name resolver set after first use is also never consulted. The PR is 16 lines in `SimpleCRUD.cs`
plus one test.

- **Real servers, not mocks:** SQL Server and PostgreSQL. 2.3.0 fails, unpatched `master` fails, and
  the patch passes.
- **Your own suite:** the full SQLite run passes, 79/79. The new test fails on unpatched code
  (`0 should be equal to 1`) and passes with the fix.
- **No cost to your recommended pattern:** on #56 you suggest calling `SetDialect` whenever a
  connection opens. The caches are cleared only when the dialect or resolver *actually changes*, so
  that pattern keeps its caching.
- **Your style:** CRLF kept, no public API change, and the test is written like the rest of `Tests.cs`.

Your suite runs SQL Server and then SQLite in one process. SQLite happens to accept `[brackets]`,
which is likely why this never surfaced. The reproductions are in
[`docs/upstream`](docs/upstream/README.md) if you want to run them yourself.

## What I built on SimpleCRUD

On #218 you wrote that several dialects at once was *"beyond the intent of simplecrud."* I agree, so I
solved it **outside** SimpleCRUD, without changing a line of it:

- **`EzOdata.SimpleCrud`** gives each dialect its own copy of your assembly in a separate
  `AssemblyLoadContext`. Each copy has its own statics. Your attributes are matched by name, so the
  same POCOs work with every copy. It's tested with SQL Server, PostgreSQL, MySQL and SQLite running
  concurrently in one process, at about 1.06× the cost of a direct call. It reads its mapping from
  your code rather than re-implementing your rules, so it follows whatever SimpleCRUD does.
- **`EzOdata.SimpleCrud.AspNetCore`** turns a database into an instant OData/REST API, and lets any
  table be taken over by an ordinary SimpleCRUD POCO with typed hooks (validation, auditing, soft
  delete). Every write goes through SimpleCRUD's `Insert` / `Update` / `Delete`.

I hope it's useful, both the fix and the proof that people are still building new things on your
work. If you'd like anything changed in the PR, tell me and I'll adjust it. If you'd rather not take
it, no hard feelings at all.

With respect and thanks,

**rvegajr** · Noctusoft
[github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD](https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD)
