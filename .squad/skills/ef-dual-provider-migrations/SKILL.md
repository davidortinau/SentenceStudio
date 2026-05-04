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

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
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

**File:** `src/SentenceStudio.Shared/Migrations/Sqlite/20260503221947_AddRefreshTokenReplacedBy.cs`

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite  // ← Note namespace
{
    /// <inheritdoc />
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

### 5. Validation (MANDATORY)

Run the mobile migration validation script:

```bash
bash scripts/validate-mobile-migrations.sh
```

This:
1. Builds Mac Catalyst Debug
2. Launches via `maui devflow`
3. Waits for migrations to apply
4. Scans logs for `SQLite Error`, `no such column`, etc.
5. Fails if any migration errors are found

**DO NOT SKIP.** This catches:
- Type mapping mismatches (e.g., `text` vs `TEXT`)
- Missing columns in SQLite migration
- Syntax errors in migration SQL

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
- [ ] Both migrations use the SAME timestamp
- [ ] `dotnet build` passes for Shared (net10.0) and Api (net10.0)
- [ ] `scripts/validate-mobile-migrations.sh` passes (no SQLite errors)
- [ ] Decision file created in `.squad/decisions/inbox/` documenting the schema change
- [ ] History updated in `.squad/agents/wash/history.md`
