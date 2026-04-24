# SQLite Migration History Reconciliation (Local Dev Drift)

Use when: a local SQLite dev DB has schema from an earlier EF migration lineage that's been consolidated/renamed, causing new-lineage migrations to fail at launch with errors like `table "X" already exists` or `no such column`.

## Non-negotiables
1. Locate the DB via `find ~/Library/Containers -name "<file>.db3"`. Confirm bundle-ID / container.
2. **Back up first**: `cp -a "$DB" "$DB.bak.$(date +%Y%m%d-%H%M%S)"`. Verify identical size. Do nothing until backup is confirmed.
3. Never delete the DB. Never rename the original. Never wipe data.

## Procedure
1. `sqlite3 "$DB" ".tables"` and `".schema"` — capture current schema.
2. `sqlite3 "$DB" "SELECT * FROM __EFMigrationsHistory;"` — see what's tracked (may be empty/missing/legacy).
3. If `__EFMigrationsHistory` missing, create it:
   ```sql
   CREATE TABLE "__EFMigrationsHistory" (
     "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
     "ProductVersion" TEXT NOT NULL
   );
   ```
4. List migrations in the assembly: `ls Migrations/<Provider>/*.cs`.
5. For each migration, read `Up()` and compare to actual `.schema` to decide: **already applied** or **needs to run**.
6. Get `ProductVersion` from each migration's `.Designer.cs` (`HasAnnotation("ProductVersion", ...)`).
7. `INSERT INTO __EFMigrationsHistory` rows ONLY for migrations whose effects are already in the schema.
8. Leave the target migration (and any other genuinely-unapplied migrations) unlisted.
9. Verify with `SELECT * FROM __EFMigrationsHistory ORDER BY MigrationId`.
10. Verify table row counts unchanged (data preserved).

## Gotchas
- EF applies migrations in MigrationId (timestamp) order. Gaps are fine; EF fills them.
- Orphan rows (legacy lineage, no matching migration in assembly) are harmless — EF ignores them. Preserve them.
- SQLite column types are advisory; an `AlterColumn INT→REAL` is safe and cheap.
- Do not try to "fix" the schema manually. Let EF do it via the migrations you leave unlisted.
- If the schema disagrees with even `InitialSqlite` (e.g. table missing), STOP. That's not drift — that's a different DB. Escalate.

## Handoff
When done, record: backup path, rows inserted, final history state, expected next-launch behavior. Tell QA: "DB reconciled, backup at <path>, safe to launch."
