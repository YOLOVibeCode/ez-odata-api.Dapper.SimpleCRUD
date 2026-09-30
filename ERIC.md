# Eric,

## Why I did this

Instead of the usual interview prep, I wanted to spend the time in your code. It seemed like an honest
way to show how I work, and a better thank-you than words alone.

And honestly, it was fun. We're both lucky to get paid to solve logic puzzles for a living. I can't
think of a better blessing.

Thank you for Dapper.SimpleCRUD. For more than ten years it has given a lot of us the rare thing: a
data layer small enough to understand in one sitting, and good enough to trust in production. I
know every issue and PR takes time from a project you maintain on your own, so I'll keep this short.
**Nothing here needs anything from you.** It's a thank-you with a fix attached.

## A small fix, verified: [PR #282](https://github.com/ericdc1/Dapper.SimpleCRUD/pull/282)

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

## A little about me

I've been writing software since 1992. I started in COBOL on IBM mainframes, moved through Delphi and
VB6, and have worked mostly in C# since .NET 1.0. Most of that time has gone into the same kind of
work: getting data out of SQL databases and into applications, usually with some code generation
involved. Along the way I've used most of the .NET data-access options, including ADO.NET, Entity
Framework, Simple.Data and Dapper, and published a few small tools of my own, like
[ez-db-codegen-core](https://github.com/rvegajr/ez-db-codegen-core). Since 2011 I've done this as a
consultant through my company, Noctusoft.

I mention it only so you know where I'm coming from. After enough years of heavy data layers, a
library that stays small and does the common work well is something I appreciate.

## If it's helpful today

I'm happy to walk through the sample app, the load-context approach, or the PR, whichever is useful,
or none of it. I'd also enjoy hearing what problem you were solving when you first wrote SimpleCRUD,
and what it's been like to maintain it for so long.

Thank you for making the time to meet.

I hope it's useful, both the fix and the proof that people are still building new things on your
work. If you'd like anything changed in the PR, tell me and I'll adjust it. If you'd rather not take
it, no hard feelings at all.

With respect and thanks,

**rvegajr** · Noctusoft
[github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD](https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD)
