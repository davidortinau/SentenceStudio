# Native migration validator accessed the live AppKit database

Date: 2026-09-03

## Summary

The macOS native migration validator launched the normal unsandboxed AppKit app. The app
resolved `FileSystem.AppDataDirectory` to the user's home Library directory, so EF Core applied
`20260903175044_AddApplicationOperationLedger` to the live `sstudio.db3` instead of a disposable
validation database.

The first corrective validator was rejected during blocking review. Although it redirected the
app to a temporary copy, it did not satisfy the native gate: its path checks were lexical, the copy
already contained the migration being tested, its schema checks accepted Id-only substitutes, and
an early DevFlow timeout could leave the launched process running. That result is recorded as a
rejected fix, not as successful validation.

A second blocking review also rejected the follow-up. The follow-up validated a path identity and
then reopened that path through a raw interpolated connection string, so connection-string
metacharacters and a path swap remained relevant. Its partial-index check accepted the expected
text as a substring of a different predicate, and its cleanup regression assigned `APP_PID` before
failing, so it did not exercise the LaunchServices PID-acquisition gap. Those were review findings,
not controls that had passed.

## Timeline

- The validator built and launched the `net11.0-macos` Debug app through LaunchServices.
- Before launch, the coordinator backed up the bundle-container database. That was the wrong
  database and did not protect the AppKit database that the process opened.
- Startup applied the ledger migration and passed the mobile schema sanity check against
  `~/Library/sstudio.db3`.
- Normal startup then attempted CoreSync against an unavailable API. The validator timed out
  after 90 seconds waiting for a usable DevFlow connection.
- Console evidence identified the actual database path. A post-migration backup of the actual
  database was retained in session storage.
- A first correction redirected the app to a SQLite `.backup` copy in a unique temporary
  directory and added read-only before/after fingerprints.
- Blocking review rejected that correction because the copy was already at the latest migration,
  path and identity isolation were incomplete, direct checks were structurally weak, and PID
  capture occurred only after DevFlow connected.
- A follow-up created a new private pending database and tightened schema and process checks, but a
  second blocking review identified unsafe connection construction, an open-by-path identity gap,
  substring partial-index validation, and a cleanup test that missed the unset-PID window.
- The final correction stopped using live data as migration input. It creates a new private
  database, advances it to the immediately previous migration with EF tooling, and requires the
  native app to produce the single pending history transition.

## Impact

The live AppKit database received one additive migration without a correct pre-migration backup.
The migration added six operation-ledger tables and their indexes. Post-incident checks found:

- `PRAGMA integrity_check` returned `ok`.
- The expected migration-history row is present.
- All six expected tables and key indexes are present.
- No corruption or data loss was observed.

The live database was not rolled back or otherwise modified during remediation.
These observations are not absolute proof that no data was lost; they are the bounded evidence
available from integrity, migration-history, schema, and semantic read-only comparisons.

## Root cause

`SentenceStudioAppBuilder` always registered EF Core and CoreSync with
`Constants.DatabasePath`. The validator had no Debug-only database override, and its
LaunchServices invocation passed no isolation arguments. On unsandboxed AppKit,
`FileSystem.AppDataDirectory` resolves to the home Library directory.

The immediate cause was compounded by missing controls:

1. Normal authentication, seeding, connectivity, and CoreSync startup ran during migration
   validation, delaying deterministic completion and touching unrelated startup surfaces.
2. The script trusted logs and a fixed DevFlow port instead of proving the app process,
   project, target framework, disposable database schema, and live-source invariants.
3. The first correction treated path strings as identities. A non-temporary root, a symlinked
   ancestor, or a hardlink could still make a nominally disposable path alias a live file.
4. A reusable marker authenticated a directory but did not make the launch authorization
   one-shot.
5. Copying an already-migrated live database tested startup after the migration, not execution of
   the pending migration. Presence-only table and index checks then allowed toy schemas to pass.
6. Process ownership was inferred from the DevFlow listener. Because the PID was assigned only
   after DevFlow connected, failures before that point escaped cleanup.
7. The validator design coupled migration proof to user data even though no source data was
   required to validate this additive migration.

## Rejected first correction

- Added an explicit Debug-only command-line contract for migration validation.
- Injected only the validated disposable path into EF Core and CoreSync registration while
  preserving the default `Constants.DatabasePath` behavior.
- Added a minimal validation application and suppressed automatic user-store, authentication,
  seeding, connectivity, and network sync startup only in validation mode.
- Copied the live database with read-only `sqlite3 .backup` and added direct SQLite checks.
- Added per-run DevFlow ports and live read-only fingerprints.

Those changes reduced exposure but did not prove isolation or a pending migration and therefore did
not satisfy the native validation gate.

## Rejected second correction

- Added physical path, device/inode, ownership, mode, link-count, one-shot marker, complete-schema,
  pending-migration, dynamic-port, and exact-PID controls.
- Still passed EF tooling a connection string created by raw `Data Source=...` interpolation and
  gave the app only a checked path, allowing connection-string parsing and check-to-open concerns.
- Accepted a partial-index SQL definition when its normalized SQL merely contained the expected
  `WHERE` fragment; appended or alternative predicates could pass.
- Added an early-timeout cleanup test, but the test populated `APP_PID` before returning the
  simulated failure. It did not prove cleanup after `open` succeeds and before PID assignment.

The second correction was not considered complete until all three findings had executable
regressions and the native run produced evidence from the connection and process actually used.

## Final controls

- The validator creates a new mode-700 directory under the canonical physical current-user
  `TMPDIR`; the database directory is mode 700 and the exclusively created database is mode 600.
- Shell and Debug-only native validation walk every ancestor with no-follow identity checks,
  reject symlinks, require canonical physical paths, verify owner and permissions, require link
  count one, and compare device/inode identity with the live database.
- The launch carries the prepared database device and inode. The native parser rechecks them,
  consumes the one-shot marker, and immediately opens a non-pooled, private-cache, read-write
  `SqliteConnection` whose connection string is produced by `SqliteConnectionStringBuilder`.
- After `Open`, the validator reads `PRAGMA database_list`, rechecks physical ancestors, ownership,
  mode, link count, device and inode, then queries the current process's macOS vnode descriptor
  information. It requires exactly one open descriptor whose path and physical identity match the
  validated disposable file. The retained evidence is the reported main path, device, inode, and
  descriptor number; lexical path equality alone is not treated as opened-file proof.
- A mode-600, link-count-one marker is renamed and consumed atomically before database
  registration. A replay of the same command line fails.
- The exact open connection is registered with `UseSqlite(connection,
  contextOwnsConnection: false)` and remains owned by the validation session for application
  lifetime. The validation initializer runs EF migration and sanity checks through that connection.
  CoreSync provider registration is omitted in validation mode, so it cannot reopen by path.
- After migration, the validation initializer applies the same three existing mobile compatibility
  columns required by the normal 8-table/13-column sanity contract, idempotently and through the
  retained connection. These columns are unrelated to the ledger migration and do not alter its
  required history transition or six-table contract.
- Live data is not copied. EF tooling advances the fresh database to
  `20260819120000_AddSkillProfileIsArchived`; pre-launch checks require that exact prior history,
  the prior `SkillProfile.IsArchived` shape, no ledger tables, and a latest-migration count of zero.
- The app runs the real `ApplicationDbContext` migration and existing mobile sanity path.
  Post-launch checks require the latest history count to transition from zero to exactly one.
- A deterministic contract transcribed from the migration validates all six tables: exact columns,
  SQLite types, nullability, primary keys, foreign keys and delete behavior, named check
  expressions, and every named index's columns, uniqueness, partial flag, and filter.
- Partial-index validation extracts the complete top-level `WHERE` clause and compares canonical
  tokens exactly. Normalization is limited to whitespace, SQL keyword/identifier case, and
  identifier quoting; appended predicates, alternatives, reordering, and missing terms fail.
- Before `open`, the script generates a 256-bit launch token, records the exact bundle and
  executable, snapshots processes matching that executable and token, and arms cleanup. Immediately
  after `open`, the one exact new process is resolved. DevFlow must match the exact project path,
  TFM, platform, app name, dynamic port, and that PID's listener.
- The exit trap sends TERM only to that verified PID on every exit path, waits, re-verifies
  identity, and uses exact-PID KILL escalation only when necessary. If `APP_PID` is still unset, the
  trap resolves only a unique post-snapshot process carrying the exact executable and launch token;
  zero or multiple matches are reported and never trigger a broad kill.
- Supplementary DevFlow logs are scoped to the current run's unique opened-binding record before
  error scanning, preventing retained entries from an older validator launch from being treated as
  current-process failures.
- Validation-only startup suppression remains explicit and Debug-only. Normal Debug startup and
  all Release composition retain their existing database, authentication, and sync behavior.
- If the live database or WAL is open, validation stops before semantic inspection. Otherwise,
  read-only integrity, migration-history, schema, content hashes, and device/inode/link/size/mtime
  metadata are captured before and after without copying or checkpointing the live database.

## Prevention

Regression tests cover non-temporary roots, symlinked ancestors, hardlink and device/inode aliases,
marker replay, malformed arguments, unsafe ports, connection-string metacharacters, post-open path
replacement, opened-descriptor binding, Release absence, exact pending-to-applied history, Id-only
schemas, wrong index columns, four negative partial-index predicates, harmless filter formatting,
missing foreign keys, missing check constraints, early-timeout cleanup, and interruption after
`open` but before PID assignment with a preexisting process left untouched. Debug and Release
macOS builds verify that validation support is available only in Debug. The final focused runs
passed 46 shell regressions and 33 validator-option unit tests before the corrected native
pending-to-applied validation was accepted.
