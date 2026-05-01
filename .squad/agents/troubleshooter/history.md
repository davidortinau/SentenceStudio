# Troubleshooter — History & Learnings

## Learnings

### 2026-05-01 — CoreSync VocabularyProgress sync failure (Postgres 42804)

- **Symptom Captain reported:** Quiz activity failed to start with "some data error" + Mac Catalyst app unresponsive after a few navigations on `feature/dashboard-vocab-tile-nav` (PR #184).
- **Where the evidence lived:** Aspire structured logs were initially invisible because `aspire-list_structured_logs` defaulted to `SentenceStudio.Workers` (chatty `HttpMessageHandler` debug logs filled the 76-entry size cap). Passing `resourceName: "SentenceStudio.Api"` (the OTLP service name, NOT the dashboard resource id `api-edufvexh`) surfaced 11 errors immediately. **Lesson:** the Aspire dashboard `resource_name` (e.g. `api-edufvexh`) is NOT the same as the OTLP `resource_name` attribute (e.g. `SentenceStudio.Api`). For `aspire-list_structured_logs` use the OTLP service name. For `aspire-list_console_logs` use the dashboard resource id.
- **Pulling full exception text:** When the structured-logs payload truncates to "Duplicate of exception for log entry NNN", use `aspire-list_trace_structured_logs` with the trace_id from any of the duplicates — that returns the original entry with full exception/stack.
- **Root cause (verbatim Postgres error):**
  ```
  CoreSync.SynchronizationException: Unable to Insert item Insert on VocabularyProgress: {... "UserDeclaredAt": "UserDeclaredAt", "VerificationState": "VerificationState" ...} 42804: column "UserDeclaredAt" is of type timestamp with time zone but expression is of type text
  ```
  CoreSync's change-set dictionary literally contains the **column NAME as the value** for both `UserDeclaredAt` (DateTime?) and `VerificationState` (int enum). The Postgres provider then sends `'UserDeclaredAt'` as a text literal into a `timestamp with time zone` column → 42804 type mismatch.
- **Hypothesis on the CoreSync bug:** the SQLite change-tracking trigger or the column extraction for these two columns is mis-generated — likely because they were added in a later EF migration and CoreSync's provisioning didn't re-derive triggers. Repro is deterministic: same batch GUID `1c18d82b-d160-424d-9ecf-bec51d04ba1e` re-fails on every retry (RequestIds EF→F0→F1 incremented on each retry).
- **Mac Catalyst hang:** Process 71568 was `S` state, 0% CPU — kernel-level idle. Sync failures themselves are caught & released the semaphore (SyncService.cs:326-340), so sync doesn't block. Hang is therefore **independent or downstream** — most likely a Blazor WebView modal/dialog from the data-error toast that's not getting dismissed cleanly, or a JS interop deadlock after the failed sync chain. Without `maui devflow` connected (broker daemon TCP listener not reachable), can't introspect further.
- **Relationship to PR #184:** ZERO. PR #184 only touches Vocabulary.razor filter switch (client-side LINQ on already-loaded data) and Index.razor anchor wrappers. Does not touch sync, EF models, VocabularyProgress, or any write path. Bug pre-dates this branch.

### Diagnostic playbook for "some data error" in this app
1. `aspire-list_structured_logs resourceName:"SentenceStudio.Api"` — filter for severity Error/Warning.
2. If exception text shows "Duplicate of … for log entry NNN", call `aspire-list_trace_structured_logs traceId:<from any dup>` to recover the original.
3. Sync failures appear as `CoreSync.SynchronizationException` on `/api/sync-agent/changes-bulk-complete/{guid}`. The batch GUID is constant across retries — useful to confirm "stuck batch" pattern.
4. Recurring sync failures don't kill the process but DO accumulate retry pressure; a UI that awaits sync completion (or prompts an error dialog) can leave the app feeling unresponsive even though the process is healthy.

---

## Round 2 — VocabularyProgress data corruption deep-dive (2026-05-01)

**Context:** Followed up Round 1's `42804` upload bug after Captain reported a new toast on the Vocabulary page: *"Error loading vocabulary: The string 'UserDeclaredAt' was not recognized as a valid DateTime."* This is the same bug surfacing through the EF read path instead of the CoreSync upload path.

### Key new evidence

- **Live DB inspection** at `~/Library/Containers/C5750C50-.../Data/Library/sstudio.db3` (copied to `/tmp/sstudio.db3` for `sqlite3` access — sandbox locks the parent dir): **all 1745 `VocabularyProgress` rows** have `UserDeclaredAt='UserDeclaredAt'`, `VerificationState='VerificationState'`, `IsUserDeclared='IsUserDeclared'` — the column NAME stored as a literal text VALUE.
- Schema itself is clean (`InitialSqlite` migration is correct; later ALTER TABLE columns `LastExposedAt`/`ExposureCount` are unrelated).
- EF Core's `DateTime?` materializer calls `DateTime.Parse("UserDeclaredAt", ...)` → `FormatException` at `VocabularyProgressRepository.GetAllForUserAsync` (line 111-114), caught by `Vocabulary.razor` LoadData's try/catch and shown via toast.

### Where the corruption did **not** come from (verified)

- No EF migration ever wrote a column-name literal as a default — `20260307234624_AddFamiliarStatusAndVerification.cs` (the original) and `20260321133148_InitialSqlite.cs` both use clean `defaultValue: false / 0`.
- `SyncService.PatchMissingColumnsAsync` does not — and per `git log -S` has never — patch `UserDeclaredAt`/`VerificationState`/`IsUserDeclared`.
- Full-repo grep for the column-name string literals (excluding `Migrations/`, `obj/`, `bin/`) returns zero matches in app code, current or historical.
- CoreSync `0.1.128-local` package is just a metadata-only re-stamp of `0.1.127` (assembly version differs, **decompiled bodies are byte-identical** — verified with `diff`). Don't waste time chasing it as a separate version.
- CoreSync.Sqlite issues no `ALTER TABLE` against user tables in any decompiled version (122/127/128-local).

### Where the corruption probably came from

Static analysis of all CoreSync versions on Captain's machine doesn't reveal a reproducible "writes column-name as value" path. The corruption is most likely a **one-time event** — perhaps a previous CoreSync release pre-0.1.122, a manual SQLite UPDATE, or an aborted EF migration mid-March. Captain's machine is the only known carrier; there's no evidence in our git history that we ever shipped a build that produces this. **Don't keep chasing the producer** — fix the data and harden the read/sync path.

### Three latent CoreSync defects that turn local corruption into a multi-surface failure

1. **`CoreSync.Sqlite.GetValueFromRecord(SqliteDataReader, int, Type)`** (~line 939, same shape in `CoreSync.PostgreSQL` ~line 1064) — type dispatch handles only the unwrapped primitives. `Nullable<T>` and `enum` fall through to `r.GetValue(ord)`, which returns the raw text for SQLite TEXT columns. Combined with **(3)**, the value silently becomes a `string` on the wire.
2. **`CoreSync.PostgreSQL.ConvertValueForColumn`** (~line 1576) — when the source value is `string` and the column is `timestamptz`, it calls `DateTime.TryParse`; on failure it **passes the unparsed string straight through to the INSERT**, so the database engine is the only thing that catches the type mismatch (Postgres `42804`). A defensive provider would fail-fast with a clear "type mismatch" error.
3. **`CoreSync.SyncItemValue.DetectTypeOfObject`** (~`CoreSync` line 780) — pure runtime-type switch. Once a value has been silently demoted to `string` by (1), it gets serialized as `SyncItemValueType.String` with no schema check.

These are upstream defects to **track**, not patch in our tree without Coordinator approval (per Captain's standing rule).

### Recommended workaround (in our repo only)

Add a `RepairTaintedVocabularyProgressAsync` helper to `SyncService.cs`, called from `PatchMissingColumnsAsync` before `MigrateAsync()` — does an idempotent `UPDATE` that resets the three known tainted columns to safe defaults. Full code + safety justifications are in `.squad/decisions/inbox/troubleshooter-coresync-vocabprogress-userdeclaredat.md` under "Round 2".

### Test plan (per Captain's "if it came back, it needs a test" rule)

- Unit test for the repair helper itself (clean row untouched, fully-tainted row reset, partially-tainted row partial-reset, idempotent on second call).
- EF round-trip integration test (insert NULL/0/0, materialize without `FormatException`, mutate, save, reload).
- Optional CoreSync regression test (round-trip a tainted SyncItem through SqliteSyncProvider→PostgreSQLSyncProvider, expect type-aware rejection rather than `42804`).

### Diagnostic playbook updates (additions to round-1 playbook)

- `ilspycmd` 9.1.0.7988 from dotnet 8 won't run on the local SDK 10 by default — set `DOTNET_ROLL_FORWARD=LatestMajor` before invoking. **Always pre-decompile all installed versions** of the suspect package (`~/.nuget/packages/<pkg>/<ver>/lib/<tfm>/...dll`) and `diff` them; "local" version stamps may be metadata-only re-stamps.
- For sandboxed Mac Catalyst databases, `cp` to `/tmp` (not `mv`) before `sqlite3` opens — the original parent dir is locked while the app is running, but read copies are fine.
- `git log -S '<column-name>' -- <file>` is much faster than reading every historical revision when answering "did this code ever contain this string?" — use it before doing a `git show` walk.
- When EF materialization throws an opaque-looking exception, **reproduce by running the same `Where` clause manually in `sqlite3` and inspect the suspect column's distinct values** (`SELECT col, COUNT(*) FROM tbl GROUP BY col`) — type-mismatch corruption is usually visible in one query.

### What I deliberately did **not** do

- Did not kill PID 99325 or modify Captain's live DB (per standing order — Captain's Mac is production).
- Did not open any upstream issue against CoreSync (per standing order).
- Did not commit any code changes — round 2 was read-only investigation as instructed.
