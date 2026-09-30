Hi Eric, thank you for SimpleCRUD. While building on it I found a small caching issue. Here's a verified fix.

### The problem

`TableNames`, `ColumnNames` and `StringBuilderCacheDict` store names **already quoted** for the dialect that was active at first use, and nothing clears them. After the first query:

1. **Switching dialects produces mixed SQL.** On real servers with 2.3.0:
   ```
   SetDialect(SQLServer)   → Get<Widget>(1) on SQL Server   ✓
   SetDialect(PostgreSQL)  → Get<Widget>(1) on PostgreSQL   ✗ 42601: syntax error at or near "["
                           → Insert(new Widget)             ✗ 42601: syntax error at or near "["
   ```
2. **Setting a resolver after first use has no effect.** `SetTableNameResolver` and `SetColumnNameResolver` aren't consulted for types that are already cached.

### The fix (16 lines, no public API change)

Clear the three caches when `SetDialect` **actually changes** the dialect, or when a resolver is replaced. On #56 you suggest calling `SetDialect` whenever a connection opens; that pattern keeps its caching, because nothing is cleared unless something changed. The static constructor's `SetDialect` call is unaffected.

### How I verified it isn't a phantom

| Check | 2.3.0 (NuGet) | `master` (62a5431) | `master` + this PR |
|---|---|---|---|
| Real SQL Server, then real PostgreSQL (`Get` + `Insert`) | ❌ 42601 | ❌ 42601 | ✅ |
| New test `TestChangeDialectRebuildsCachedNames` | ❌ `0 should be equal to 1` | ❌ | ✅ |
| Your SQLite suite (every `Tests` method, as in `RunTestsSqLite`) | n/a | 78/78 | **79/79** |
| `Dapper.SimpleCRUDTests` builds (net8.0) | n/a | ✅ | ✅ |

`Program.Main` runs SQL Server and then SQLite in one process, and SQLite happens to accept `[brackets]`. That's likely why the existing suite never showed this.

The test follows the style of `Tests.cs`: a counting `TableNameResolver` shows the cache is kept for a same-dialect `SetDialect` and rebuilt after a real switch. CRLF line endings are preserved.

This makes sequential switching correct. It doesn't try to make two dialects safe *at the same time*, which you've said is beyond SimpleCRUD's intent (#218). For anyone who needs that, I built it outside SimpleCRUD, without changing it: [ez-odata-api.Dapper.SimpleCRUD](https://github.com/YOLOVibeCode/ez-odata-api.Dapper.SimpleCRUD) (reproductions in `docs/upstream`, and a short note in `ERIC.md`).

Happy to adjust anything. Thanks for your time.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
