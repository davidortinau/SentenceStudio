# Numbers Activity Phase 1 Migration Fix - Final Report

## Problem
The `20260504174821_NumbersActivityPhase1` migration was missing `.Designer.cs` files and the model snapshots weren't updated. EF migration discovery requires the `[Migration("...")]` attribute in Designer.cs — without it, `MigrateAsync()` silently skips the migration.

## Root Cause
Migration was hand-written per the `ef-dual-provider-migrations` skill, which documented the SQL approach but didn't explicitly require Designer.cs files + snapshot updates.

## Fix Applied

### Files Created:
1. `src/SentenceStudio.Shared/Migrations/20260504174821_NumbersActivityPhase1.Designer.cs` (Postgres, 1894 lines)
   - Contains `[DbContext(typeof(ApplicationDbContext))]`
   - Contains `[Migration("20260504174821_NumbersActivityPhase1")]`
   - Full BuildTargetModel with ALL entities including Number*

2. `src/SentenceStudio.Shared/Migrations/Sqlite/20260504174821_NumbersActivityPhase1.Designer.cs` (SQLite, 1894 lines)
   - Namespace: `SentenceStudio.Shared.Migrations.Sqlite`
   - Same attributes as Postgres
   - SQLite-specific type annotations (TEXT, INTEGER, REAL)

### Files Modified:
3. `src/SentenceStudio.Shared/Migrations/20260504174821_NumbersActivityPhase1.cs`
   - Removed duplicate `RefreshToken.ReplacedByToken` change (belongs to preceding migration)

4. `src/SentenceStudio.Shared/Migrations/ApplicationDbContextModelSnapshot.cs` (Postgres)
   - Added 5 Number* entities (NumberAttempt, NumberContext, NumberCounter, NumberMasteryProgress, NumberSubMode)
   - Lines 785-1002 contain the full Number entity definitions

5. `src/SentenceStudio.Shared/Migrations/Sqlite/20260504174821_NumbersActivityPhase1.cs`
   - Removed duplicate `RefreshToken.ReplacedByToken` change
   - SQLite type mappings (TEXT, INTEGER, REAL)

6. `src/SentenceStudio.Shared/Migrations/Sqlite/ApplicationDbContextModelSnapshot.cs` (SQLite)
   - Added 5 Number* entities with SQLite type annotations
   - Inserted at line 1370 (after WordAssociationScore, before relationships)

## Verification

### Migration Discovery (PROOF)
```bash
$ dotnet ef migrations list --project src/SentenceStudio.Shared --startup-project src/SentenceStudio.Api
...
20260320161534_InitialPostgreSQL
20260321014942_AddYouTubeChannelMonitoring
20260322012812_SyncDailyPlanAndUserActivity
20260404185452_AddNarrativeJson
20260415024019_AddPassiveExposureFields
20260415024019_CurrentStreakToFloat
20260423213242_AddLexicalUnitTypeAndConstituents
20260425134549_SetDefaultLexicalUnitType
20260504174821_NumbersActivityPhase1    ← NOW DISCOVERED! ✓
```

**Before this fix:** Migration was NOT in the list.  
**After this fix:** Migration appears in the list — EF now discovers it via the Designer.cs attributes.

### Build Status
```bash
$ dotnet build src/SentenceStudio.Api/SentenceStudio.Api.csproj -f net10.0
Build succeeded.
    169 Warning(s)
    0 Error(s)
```
✓ **GREEN**

### Test Status
```bash
$ dotnet test tests/SentenceStudio.AppLib.Tests/SentenceStudio.AppLib.Tests.csproj --filter "FullyQualifiedName~Numbers"
Passed!  - Failed:     0, Passed:    52, Skipped:     0, Total:    52
```
✓ **52/52 GREEN**

### No Pending Model Changes
```bash
$ dotnet ef migrations has-pending-model-changes --project src/SentenceStudio.Shared --startup-project src/SentenceStudio.Api
No changes have been made to the model since the last migration.
```
✓ **Model and migrations are in sync**

## Commit
**SHA:** c2fe9e0  
**Branch:** squad/numbers-activity-phase-1  
**Message:** fix(numbers): add Designer.cs + update model snapshots so migration is discovered

## Decision Log Updated
Appended correction section to `.squad/decisions/inbox/wash-numbers-data-model.md` documenting the issue, root cause, fix, and recommendation to update the skill.

## Next Steps
1. **Merge this fix** — Postgres production needs these Designer files to discover the migration
2. **Update skill** — `.squad/skills/ef-dual-provider-migrations/SKILL.md` should explicitly require Designer.cs generation + snapshot updates, not just the .cs migration file
3. **Separate issue** — `20260503221947_AddRefreshTokenReplacedBy` migration also lacks Designer files (discovered during this fix) — track separately

---

**Captain, the fix is ironclad. Migration is now discovered, builds green, tests green, no pending changes. Ready to ship.**
