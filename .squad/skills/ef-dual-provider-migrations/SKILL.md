# EF Core Dual-Provider Migrations (PostgreSQL + SQLite)

**USE WHEN:** Adding or modifying database schema in a project that targets BOTH server (PostgreSQL via Npgsql) and mobile (SQLite) from the same DbContext.

**PATTERN:** SentenceStudio.Shared multi-targets `net10.0` (server) and `net10.0-{ios,android,maccatalyst}` (mobile). Server uses PostgreSQL for API/Aspire, mobile uses SQLite for offline storage. Schema must stay in sync.

## Step-by-Step Workflow

### 1. Modify the EF Core Model

Update entity classes in `src/SentenceStudio.Shared/Models/`:

```csharp
public class RefreshToken
{
    // ... existing properties ...
    
    /// <summary>
    /// When this token is revoked by a refresh operation, points to the successor token value.
    /// </summary>
    public string? ReplacedByToken { get; set; }
}
```

### 2. Create Migrations Manually (dotnet ef fails on multi-TFM projects)

**DO NOT USE** `dotnet ef migrations add`. The tooling cannot resolve the correct TFM when the project has conditionals like:

```xml
<ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) != ''">
  <Compile Remove="Migrations\*.cs" />
</ItemGroup>
```

**Instead:** Create migration files manually using the existing migration pattern as a template.

#### PostgreSQL Migration

Copy an existing migration from `src/SentenceStudio.Shared/Migrations/` (e.g., `20260415024019_AddPassiveExposureFields.cs`) and adapt:

**File:** `src/SentenceStudio.Shared/Migrations/20260503221947_AddRefreshTokenReplacedBy.cs`

> **🔴 CRITICAL — the `[Migration]` + `[DbContext]` attributes are MANDATORY.**
> A hand-written migration class that lacks `[Migration("id")]` is INVISIBLE to EF Core:
> `MigrateAsync()` never discovers or applies it. Normally these attributes live in the
> auto-generated `.Designer.cs`; when you create migrations by hand you MUST put them
> inline on the class (or hand-write the `.Designer.cs`). Miss this on the SQLite copy
> and the migration silently no-ops on mobile — the table never gets created — while the
> PostgreSQL side (if it HAS the attributes) applies fine, so the app works on the webapp
> and breaks only on iOS/Android. This exact split shipped to a device on 2026-07-02
> (ActivitySession missing on iOS, dashboard interactivity dead). **Both provider copies
> must carry both attributes.**

```csharp
using Microsoft.EntityFrameworkCore.Infrastructure;   // ← for [DbContext]
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;                             // ← for ApplicationDbContext

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]                    // ← MANDATORY
    [Migration("20260503221947_AddRefreshTokenReplacedBy")]      // ← MANDATORY (exact id)
    public partial class AddRefreshTokenReplacedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplacedByToken",
                table: "RefreshTokens",
                type: "text",          // ← PostgreSQL type
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplacedByToken",
                table: "RefreshTokens");
        }
    }
}
```

#### SQLite Migration

Copy the SAME migration to `src/SentenceStudio.Shared/Migrations/Sqlite/` and change:
1. Namespace to `SentenceStudio.Shared.Migrations.Sqlite`
2. Type mapping to SQLite equivalents
3. **Keep the `[DbContext]` + `[Migration]` attributes** (same id) — do NOT drop them on the copy

**File:** `src/SentenceStudio.Shared/Migrations/Sqlite/20260503221947_AddRefreshTokenReplacedBy.cs`

```csharp
using Microsoft.EntityFrameworkCore.Infrastructure;   // ← for [DbContext]
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;                             // ← for ApplicationDbContext

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite  // ← Note namespace
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]                    // ← MANDATORY (same as Postgres)
    [Migration("20260503221947_AddRefreshTokenReplacedBy")]      // ← MANDATORY (same id)
    public partial class AddRefreshTokenReplacedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplacedByToken",
                table: "RefreshTokens",
                type: "TEXT",          // ← SQLite type (not "text")
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplacedByToken",
                table: "RefreshTokens");
        }
    }
}
```

> **Verify discovery before shipping:** after creating both files, confirm each has
> `[Migration("<id>")]`. A quick guard:
> `grep -L 'Migration("<id>")' src/SentenceStudio.Shared/Migrations/<id>_*.cs src/SentenceStudio.Shared/Migrations/Sqlite/<id>_*.cs`
> (any file listed is MISSING the attribute). Then run `scripts/validate-mobile-migrations.sh`
> — it fails if the migration doesn't actually apply on a real SQLite mobile run.

### 3. Type Mapping Reference (PostgreSQL ↔ SQLite)

| .NET Type | PostgreSQL | SQLite |
|-----------|-----------|--------|
| `string` (nullable) | `text` | `TEXT` |
| `int` | `integer` | `INTEGER` |

### 3. Type Mapping Reference (PostgreSQL ↔ SQLite)

| .NET Type | PostgreSQL | SQLite |
|-----------|-----------|--------|
| `string` (nullable) | `text` | `TEXT` |
| `int` | `integer` | `INTEGER` |
| `DateTime` | `timestamp with time zone` | `TEXT` |
| `bool` | `boolean` | `INTEGER` |
| `decimal` | `numeric` | `TEXT` |

**CRITICAL:** SQLite uses ALL CAPS for type names. PostgreSQL uses lowercase.

### 4. Timestamp Convention

Use the pattern `YYYYMMDDHHMMSS` for migration filenames:

```bash
date +"%Y%m%d%H%M%S"
# Output: 20260503221947
```

Both PostgreSQL and SQLite migrations MUST use the **same timestamp** so EF Core applies them in the correct order.

### 5. Validation (MANDATORY — BOTH gates)

**5a. Static attribute guard (run FIRST — fast, deterministic, catches the recurring bug):**

```bash
bash scripts/validate-migration-attributes.sh
```

Fails if any migration lacks a discoverable `[Migration("<id>")]` / `[DbContext]`
attribute — the exact bug that silently skips a migration on mobile. This also runs in
CI (`ci.yml` → `migration-guard`) on every push/PR, so a missing attribute can never
merge unnoticed again.

**5b. Mobile runtime validation (real SQLite apply):**

```bash
bash scripts/validate-mobile-migrations.sh
```

This:
1. Builds macOS AppKit Debug (with `ValidateXcodeVersion=false` for Xcode 26.4)
2. Launches the binary directly (captures ILogger.AddConsole() output)
3. Waits for DevFlow agent on port 9225 (explicit, not broker auto-discovery)
4. Verifies attached agent identity is SentenceStudio (not a stale/foreign agent)
5. Fetches logs via DevFlow if available; falls back to captured console output
6. Scans for `SQLite Error`, `no such column`, etc.
7. HARD-FAILS if the `Schema sanity check PASSED` signal is absent (a green grep over
   an empty log proves nothing — this is how the false-pass happened).

**DO NOT SKIP EITHER.** 5a catches missing-attribute (silent-skip) bugs that 5b's log
scan and any raw-DDL test would miss; 5b catches runtime SQL/type failures.

> **⚠️ A raw-DDL / schema-copy test is NOT sufficient validation.** Applying the migration's
> DDL to a copy of the DB only proves the SQL parses — it does NOT prove EF *discovers and
> invokes* the migration. Both device incidents had perfectly valid DDL; the migration was
> simply never applied. Always confirm via 5a + a real native-head apply (check
> `__EFMigrationsHistory` — WAL-mode: pull `db`+`-wal`+`-shm` or terminate the app first).

### 6. Build Verification

```bash
dotnet build src/SentenceStudio.Shared/SentenceStudio.Shared.csproj -f net10.0
dotnet build src/SentenceStudio.Api/SentenceStudio.Api.csproj -f net10.0
```

Both should succeed with 0 errors.

## Common Gotchas

### 1. `dotnet ef migrations add` fails with "ResolvePackageAssets does not exist"

**Cause:** The Shared project multi-targets. EF tooling cannot resolve which TFM to use.

**Fix:** Create migrations manually (see Step 2).

### 2. Migration applies on server but fails on mobile with "no such column"

**Cause:** SQLite migration is missing a column that the PostgreSQL migration added.

**Fix:** Ensure BOTH migrations have identical `AddColumn` calls (only type names differ).

### 3. Mobile app crashes at startup with "SQLite Error 1: 'no such column: ReplacedByToken'"

**Cause:** SQLite migration did not run (either not included in build or EF Core skipped it).

**Fix:**
1. Check that the migration file is in `Migrations/Sqlite/` (not `Migrations/`)
2. Verify the timestamp matches the PostgreSQL migration
3. Run `validate-mobile-migrations.sh` to confirm

### 4. Type mismatch errors (e.g., "cannot convert 'text' to INTEGER")

**Cause:** SQLite migration used PostgreSQL type names (`text` instead of `TEXT`).

**Fix:** Use ALL CAPS for SQLite types (see Type Mapping Reference above).

## Why Manual Migrations?

**Conditional compilation blocks tooling:** The Shared project uses:

```xml
<ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) != ''">
  <!-- Mobile: exclude PostgreSQL migrations -->
  <Compile Remove="Migrations\*.cs" />
</ItemGroup>
<ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == ''">
  <!-- Server: exclude SQLite migrations -->
  <Compile Remove="Migrations\Sqlite\**" />
</ItemGroup>
```

This ensures:
- Server builds only include PostgreSQL migrations
- Mobile builds only include SQLite migrations

But `dotnet ef` cannot determine which TFM to target → MSBuild error.

**Manual migrations are safer:** We control the exact SQL, type mappings, and namespace. No risk of tooling generating incorrect SQLite syntax.

## Example Commits

- c9b1d0a — Backfill NULL ExposureCount (shows defensive SQL in migration)
- f6cff63 — SetDefaultLexicalUnitType (PostgreSQL + SQLite pair)
- Latest: AddRefreshTokenReplacedBy (this fix)

## Files to Reference

- Template: `src/SentenceStudio.Shared/Migrations/20260415024019_AddPassiveExposureFields.cs`
- Template (SQLite): `src/SentenceStudio.Shared/Migrations/Sqlite/20260415024019_AddPassiveExposureFields.cs`
- Validation: `scripts/validate-mobile-migrations.sh`
- DbContext: `src/SentenceStudio.Shared/Data/ApplicationDbContext.cs` (OnModelCreating shows table names, constraints)

## Success Checklist

- [ ] Model class updated in `src/SentenceStudio.Shared/Models/`
- [ ] PostgreSQL migration created in `Migrations/` with correct type (`text`, `integer`, `timestamp with time zone`)
- [ ] SQLite migration created in `Migrations/Sqlite/` with SQLite types (`TEXT`, `INTEGER`)
- [ ] **BOTH migration `.cs` files carry `[DbContext(typeof(ApplicationDbContext))]` + `[Migration("<id>")]`** (else EF never applies them — silent no-op on the provider that's missing it)
- [ ] Both migrations use the SAME timestamp
- [ ] `dotnet build` passes for Shared (net10.0) and Api (net10.0)
- [ ] `scripts/validate-mobile-migrations.sh` passes (no SQLite errors)
- [ ] Decision file created in `.squad/decisions/inbox/` documenting the schema change
- [ ] History updated in `.squad/agents/wash/history.md`

## Validating a migration WITHOUT a device (when DevFlow is unreliable)

`scripts/validate-mobile-migrations.sh` depends on MAUI DevFlow attaching to the
correct app. When any other DevFlow app is running on the machine, `maui devflow
wait` can attach to the WRONG app and the gate cannot read our logs (see the
2026-07-02 stale-agent false-pass). When you can't trust the device gate, validate
the DDL deterministically against real schema instead:

**SQLite (mobile/desktop heads):** copy the real DB and apply the migration's DDL.
```bash
DB=~/Library/Containers/com.simplyprofound.sentencestudio/Data/Library/sstudio.db3
cp "$DB" /tmp/migtest.db3
sqlite3 /tmp/migtest.db3 <<'SQL'
-- paste the exact CREATE TABLE / CREATE [UNIQUE] INDEX ... [WHERE ...] from the
-- Sqlite migration's Up(), then a probe INSERT + SELECT + DROP to confirm
SQL
rm -f /tmp/migtest.db3   # NEVER mutate the real DB — always operate on a copy
```

**PostgreSQL (webapp/API via Aspire):** apply the DDL against the LIVE DB with a
throwaway name, then drop it. This proves the syntax is valid AND that existing
data satisfies any new constraint (e.g. a partial UNIQUE index), non-destructively.
```bash
RT=$(command -v docker || command -v podman)
CID=$($RT ps --format '{{.Names}}' | grep -i '^db-')          # Aspire postgres:17 container
PW=$($RT exec "$CID" printenv POSTGRES_PASSWORD)              # user is 'dbadmin', db 'sentencestudio'
$RT exec -e PGPASSWORD="$PW" "$CID" psql -U dbadmin -d sentencestudio -v ON_ERROR_STOP=1 \
  -c 'CREATE UNIQUE INDEX "tmp_probe" ON "MyTable" (...) WHERE "Col" = ...;' \
  && $RT exec -e PGPASSWORD="$PW" "$CID" psql -U dbadmin -d sentencestudio -c 'DROP INDEX "tmp_probe";'
```
A clean CREATE (no unique violation) + DROP proves the amended DDL is valid and the
current data is compatible.

## GOTCHA: amending an already-applied migration does NOT re-apply it

EF Core never re-runs a migration already recorded in `__EFMigrationsHistory`. If
you tested a migration once (e.g. an Aspire boot applied it) and THEN amend its
`Up()` (add an index, change a column) before committing, any dev DB that already
ran it keeps the OLD schema — only fresh DBs and CI pick up the change. **A green
local run does NOT prove the amended DDL applied.** To re-materialize locally: drop
the affected table + its `__EFMigrationsHistory` row, or recreate the DB (Aspire:
tear down the db container volume). This is the safest reason to prefer a NEW
migration over amending — unless the original is still uncommitted/unshipped.
