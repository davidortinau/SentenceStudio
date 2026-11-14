# Progress count repair and the 527 deleted answers (2026-11-13)

The Captain's quiz info sheet showed "0 attempts" for words he had practiced (필요하다, 곧, 굽). Two
production data operations followed, both approved by the Captain in the session.

## 1. Stale progress counts (repaired)

**Cause.** Between March and October 2026, updates to `VocabularyProgress` counts were lost in sync
conflicts while the answers themselves (`VocabularyLearningContext` rows) synced. Example, 곧
(`250c49f0-…`): its progress row was last written 2026-03-07 at 0 attempts; two correct answers from
2026-06-22 point at that row. Rows whose last answer is newer than their last update cluster in
2026-03..09, 3 in October, none in November: the concurrent progress merge (`3d2ab417`, `8864512b`,
2026-10-31) stopped it. Not an owner problem: one owner on every row.

**Repair.** `tools/progress-count-repair` (tests in `tools/progress-count-repair.Tests`, 6 passing):
for one owner, every progress row with more active answers than counted attempts is folded with
`VocabularyProgressFold.FoldInto` (the duplicate-merge fold: replay the answers, never lower counts,
dates or a declaration). Mastery set outside the answers (a known-word confirmation, a declaration,
a merge) cannot be replayed (a row at 1.0 after one counted answer replays to 0.15), so a row whose
replay comes out lower keeps its stored mastery, streak and review schedule. The tool reads JSON
exports and writes guarded SQL: each `UPDATE` changes only the folded columns and only while `Id`,
`UserId`, `UpdatedAt` and `TotalAttempts` are as exported (a row practiced since is skipped).

**Run.**
- Backup: `scripts/prod-db-access.sh backup` -> `~/sstudio-prod-backup-20261113-2244/sentencestudio.dump`
  (71 of 71 tables; VocabularyWord 4832, VocabularyProgress 2635, UserProfile 24).
- Scratch: the dump restored into a local `postgres:16` container; 862 rows updated, a second run
  `UPDATE 0` for all (the guard), other owners' rows byte-identical (md5), 862 CoreSync `U`
  entries and nothing else, a new plan 0 rows.
- Production (owner `80227a51-…`, 2026-11-14 05:09 UTC): 862 rows updated, 0 skipped, other owners
  unchanged, a new plan 0 rows. Attempts 1,248 -> 4,265; correct 1,145 -> 4,091; words at 0
  attempts 202 -> 0; Known 14 -> 75, Unknown 216 -> 10; no count, date or mastery lowered.
  Files: `~/sstudio-prod-backup-20261113-2244/progress-repair/`.

`scripts/prod-db-access.sh` now detects the az CLI's firewall-rule parameters (az 2.92 names the
server `--server-name` and the rule `--name`; older versions used `--name` and `--rule-name`).

## 2. 527 answers deleted by a sync echo (restored; bug open)

**What happened.**
- 04:35-04:39 UTC: a server-side duplicate merge (source `NULL` in `__core_sync_ct`, most likely the
  WebApp) deleted 258 words, 171 progress rows and 959 resource mappings of the Captain's
  (`SyncDeletion`) and moved their answers to the kept words.
- 05:07 UTC: Duotopia's next sync deleted 527 of the Captain's answers on the server
  (`SyncDeletion`, `__core_sync_ct` op `D`, source `dd0dea61-8c74-9c7a-e75c-9529861f2250`). 54
  words lost their whole history, 64 part of it; all were answers on kept words.
- Likely mechanism (not yet proven in code): the device applied the downloaded word deletes, its
  local cascade removed the answers that still pointed at the deleted copies, and its change log
  uploaded those removals as its own deletes of rows the server had already moved.

**Identifying a device from `__core_sync_ct.src`.** The server records a scoped store ID,
`SHA256("SentenceStudio.Native.Store.v3:{ownerId:N}:{storeId:N}")[0..16]` as a .NET `Guid`
(`src/SentenceStudio.Api/Sync/NativeSyncStoreBindings.cs`); the device's own store ID is in its
SQLite `__CORE_SYNC_LOCAL_ID`. Duotopia's `f3bb887c-fa71-4600-a734-1b53f366b559` with the Captain's
owner gives `dd0dea61-…`.

**Restore (2026-11-14 05:29 UTC).** The 527 rows from the 04:44 backup, inserted with
`json_populate_recordset` (exact column types), only where the progress row is still the owner's,
`ON CONFLICT DO NOTHING`, a resource link dropped if its resource is gone. Scratch test (delete,
restore, count, rollback): 527. Production: 0 present before, `INSERT 0 527`, 527 present after,
CoreSync insert entries recorded. No answers were lost before the backup: every answer on Duotopia
at 22:17 local was on the server at 22:44.

**Cause (confirmed).** CoreSync.Sqlite 0.1.129 `SqliteSyncProvider.ApplyChangesAsync` sets `SRC`
only on `MAX(ID)` of the change log after each applied row; SQLite logs a delete's ON DELETE
CASCADE and SET NULL rows before the deleted row, so they stay local. Duotopia's change log after
the 23:07 sync: 257 downloaded `VocabularyWord` deletes interleaved with local deletes of 869
`ResourceVocabularyMapping`, 527 `VocabularyLearningContext` and 159 `VocabularyProgress` rows. Only
the answers still existed on the server (the merge had re-parented them), so only they were lost.

**Fix (B87, 2026-11-14, pending deploy and installs).**
- Clients: CoreSync.Sqlite `0.1.129-local4` (`lib/coresync`), built from tag v0.1.129 plus upstream
  commit 429c330 (first released in v0.1.132; davidortinau/CoreSync branch
  `fix/sqlite-cascade-src`): an apply marks every change-log entry it writes (`ID > pre-apply
  version AND SRC IS NULL`) as the source store's, inside its own transaction, so cascades, SET NULL
  rows and anything else the apply causes stay out of the next upload. 0.1.132 itself was not taken:
  it also moves Microsoft.Data.Sqlite to 10.0.9, pins SQLitePCLRaw 3.0.3 and moves MessagePack to 3.
  Every MAUI and native client gets it through the package.
- Server: `NativeOwnerSyncProvider.OwnDeletesAsync` applies an answer delete only when the same
  upload deletes its stored progress row (no client deletes an answer on its own), logs the ones it
  keeps, and touches them after the upload so the device that removed them downloads them again.
  This protects answers only: example sentences, minimal pairs and phrase links a merge moves can be
  deleted on their own, so the server cannot tell an older build's echo from a learner's delete.
- Tests: `NativeSyncCascadeEchoTests` (SQLite to SQLite: a merge on the server, the device's syncs
  delete nothing; a sync stopped right after the download commits uploads no cascade; a word the
  learner deletes still uploads its progress and answer deletes),
  `MauiOwnerCheckedSyncPostgresTests.MergeOnTheWeb_DeletesNoMovedAnswer_WhenAnInstallDownloadsIt`
  (real API, PostgreSQL) and `OwnerCheckedDeletesPostgresTests.AnswerDelete_IsAppliedOnlyWithItsProgressRow`
  (the filter, the kept answers sent back). On stock CoreSync.Sqlite 0.1.129 the echo tests fail
  and the MAUI install holds 4 cascade deletes to upload; with both fixes off the PostgreSQL tests
  keep 0 of 2 moved answers.

**Open.**
- Until the API is deployed and every app updated, a device that still holds pre-merge copies can
  repeat the loss when it next syncs (DX24, a MAUI or Mac app): answers are protected by the server
  guard once deployed, moved example sentences, minimal pairs and phrase links are not. No
  duplicate merges until all of them are updated.
- A download applies the duplicate's delete before the updates that move its children, so the
  device removes the moved rows and relies on CoreSync's forced write to put them back. In the
  tests it does; on Duotopia the moved answers were missing until the restore re-sent them, not yet
  explained. A minimal pair answer naming the duplicate (ON DELETE RESTRICT) would keep that word
  on the device.
- The server's duplicate finder offers merges the merge then refuses ("No merge was needed"), e.g.
  먹다: the recommended keeper `38` is linked from the ownerless Starter Vocabulary resource.
