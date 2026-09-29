# Native SQLite migration parity

The iOS and Android heads use the `net11.0-ios` and `net11.0-android` builds of
`SentenceStudio.Shared`. On those targets, `ApplicationDbContext` derives from
`DbContext`, not the server's `IdentityDbContext<ApplicationUser>`. The mobile
model explicitly excludes the server-only `ApplicationUser` and `RefreshToken`
entities. The SQLite model snapshot describes this mobile model; this parity
repair does not alter the PostgreSQL model or snapshot. Historical SQLite
migrations still create ASP.NET Identity and refresh-token tables, but this
change neither drops those tables nor deletes their data.

EF's relational model differ initially reported 54 operations between the
compiled mobile model and SQLite snapshot: server Identity entities, PostgreSQL
column-type annotations, an omitted `DiaryEntry`, and missing indexes. The
SQLite snapshot now matches the compiled iOS and Android models without
ignoring `PendingModelChangesWarning`. The following PostgreSQL migrations had
no SQLite counterpart; both now have a same-ID, attributed SQLite migration:

- `20260404185452_AddNarrativeJson`: metadata-only on SQLite. The earlier
  `SyncDailyPlanAndUserActivity` plan-table creation now includes nullable
  `DailyPlanCompletion.NarrativeJson`. Older MAUI databases can already have
  that column from the legacy schema patch. The old table's data-copy query
  projects `SELECT *, NULL AS NarrativeJson`: it copies a real narrative when
  the source has one and supplies null on an unpatched source, without
  referencing a nonexistent column. Adding the column again at this later
  migration would fail and risk the existing offline plan data.
- `20260621193737_AddVocabularyDuplicateLookupIndexes`: resource/word lookup
  and resource-owner indexes.

SQLite's `CurrentStreakToFloat` migration rebuilds `VocabularyProgress`. Its
target model had omitted `ExposureCount` and `LastExposedAt`, which were added
by `AddPassiveExposureFields` immediately before that migration. The corrected
target model carries both columns and preserves their values during the
rebuild. The historical `AddExampleSentenceMetadata` SQLite operation now
declares `IsFlagged` as `INTEGER` for new applications. Already-applied copies
are not replayed or rewritten; a legacy `boolean` declaration is left in
place rather than rebuilding a populated table merely to change its declared
type.

## Isolated validation

From the repository root:

```bash
bash scripts/validate-migration-attributes.sh
bash scripts/validate-mobile-migrations.sh --validate-native-sqlite-only
```

The native SQLite mode restores and builds `SentenceStudio.Shared` sequentially
for iOS and Android into per-project `obj/sqlite-parity/` and
`bin/sqlite-parity/`. It does not overwrite the normal platform build outputs,
launch an app, use a device database, or call `EnsureCreated`. A standard .NET
test host loads each actual mobile Shared binary, checks it is the binary
tested, and exercises EF against private scratch SQLite files. Seven tests per
target check migration-file discovery, a zero-operation model diff and
`HasPendingModelChanges == false`, fresh `MigrateAsync`, upgrades from both
an early database and the immediately previous migration, a non-null
`NarrativeJson` added before the historical table rebuild, complete history
and column materialization, the latest archive column and filtered index, the nullable
StreamHistory owner column/index, and preservation of offline stream, plan,
and passive-exposure rows. The test removes only its own scratch files.

The validator's latest transition is
`20260927100455_AddStreamHistoryOwnerScope` ->
`20260928050905_ArchiveDisplacedPlanItems`; the EF-only scratch mode and
database-only mode check that pair and the partial active-plan index.

The older unqualified `validate-mobile-migrations.sh` path launches a temporary
MAUI reference head and rejects its production bundle identity; it is not the
new iOS/Android-head validation path. Do not bypass that guard or use a Debug
`EnsureCreated` preview database as evidence for release migrations.

Normal iOS startup (Debug without `NativePreviewSchema=true`) uses the
isolated native Debug bundle and `sentencestudio.sqlite`. Normal Android
startup uses `AndroidServiceHost.Create` and
`sentence-studio-native.sqlite`; its existing-database guard intentionally
refuses pending migrations without a verified backup. Both normal paths must
be checked on disposable simulators before any physical-device migration.
This gate does not authorize importing a legacy MAUI database into either
native head. A database with seeded migration history but unpatched legacy
columns needs a separately backed-up and verified recovery path, not a
silent assumption that its recorded migration IDs describe its real schema.
