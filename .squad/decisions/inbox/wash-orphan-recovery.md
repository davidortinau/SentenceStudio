# Decision: Orphan Data Recovery After Server Wipe

**Author:** Wash (Backend Dev)  
**Date:** 2025-07-26  
**Status:** Implemented  
**Requested by:** Captain (David Ortinau) — URGENT DATA RECOVERY

## Problem

When the production Postgres database is wiped and the Captain re-registers with the same email, the server creates a **new** `UserProfileId`. The local SQLite database on his iPhone still carries all learning data tagged with the **old** `UserProfileId`. CoreSync won't delete local data, but every repository query filters by `active_profile_id`, making the old records invisible.

## Solution

An automatic `DataRecoveryService` that runs during the login flow, **after** the new `active_profile_id` is set in preferences but **before** CoreSync triggers. It scans all user-scoped tables in local SQLite for records belonging to any user ID other than the new one and re-tags them.

### Tables Covered

**UserId column:** VocabularyProgress, MinimalPair, MinimalPairSession, MinimalPairAttempt  
**UserProfileId column:** SkillProfile, DailyPlanCompletion, UserActivity, LearningResource, WordAssociationScore, MonitoredChannel, VideoImport  
**PK (Id) column:** UserProfile (promoted or cleaned up)

### Safety Guards

1. **SQLite-only** — skips entirely when running on Postgres (server/webapp)
2. **Idempotent** — running twice does nothing; rows already matching the new ID are untouched
3. **Multiple old IDs** — handled with a warning log; all are re-tagged to the new ID
4. **Non-blocking** — failures are caught and logged; login + sync proceed even if recovery fails
5. **UserProfile PK** — if no profile with the new ID exists yet, the old profile is promoted (renamed) to preserve user settings; if one already exists, old profiles are cleaned up

### Trigger Point

`IdentityAuthService.StoreTokens()` → after `_preferences.Set("active_profile_id", ...)` → calls `_dataRecovery.RecoverOrphanedDataAsync(userProfileId)` → then sync fires.

## Files Changed

| File | Change |
|------|--------|
| `src/SentenceStudio.Shared/Data/DataRecoveryService.cs` | **New** — the recovery service |
| `src/SentenceStudio.AppLib/Services/IdentityAuthService.cs` | Injects `DataRecoveryService?` and calls it in `StoreTokens()` |
| `src/SentenceStudio.AppLib/Services/CoreServiceExtensions.cs` | Registers `DataRecoveryService` as singleton |

## What Happens at Runtime

1. Captain opens the app, logs in with his email
2. Server returns a new `UserProfileId` in the auth response
3. `StoreTokens()` sets `active_profile_id` to the new ID
4. `RecoverOrphanedDataAsync()` runs:
   - Discovers old user IDs across all 11+ tables
   - Updates every orphaned row to carry the new ID
   - Promotes or cleans up old UserProfile rows
   - Logs a summary (e.g., "142 records re-tagged")
5. CoreSync triggers and pushes the re-tagged data to the server
6. Data is fully recovered on both device and server

## Build Verification

```
dotnet build src/SentenceStudio.AppLib/SentenceStudio.AppLib.csproj
# 0 Error(s), all warnings pre-existing
```
