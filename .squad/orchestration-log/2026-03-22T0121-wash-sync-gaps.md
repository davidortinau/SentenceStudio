# Orchestration Log: Wash — Sync Gaps Investigation & Fix

**Date:** 2026-03-22  
**Time:** 01:21  
**Agent:** Wash (Backend Dev)  
**Mode:** background  
**Status:** COMPLETED  

## Mission

Investigate and fix CoreSync data sync gaps between mobile and web clients for the same account.

## Work Summary

### Investigation
- Identified that `DailyPlanCompletion` and `UserActivity` tables were NOT registered in CoreSync
- Found both tables were using `int` primary keys instead of string GUID required by CoreSync
- Discovered missing `UserProfileId` fields for multi-user data isolation
- Root cause: Tables were created before CoreSync entity requirements were established

### Implementation
1. **Created Migrations (PostgreSQL)**
   - Added `Id` (string GUID) to `DailyPlanCompletion`
   - Added `Id` (string GUID) to `UserActivity`
   - Added `UserProfileId` to both tables
   - Preserved existing int PKs as legacy reference during migration

2. **Created Migrations (SQLite)**
   - Table recreation strategy (SQLite doesn't support ALTER COLUMN type changes)
   - Data copy/backfill approach to preserve existing records
   - `UserProfileId` assignment logic

3. **Updated SharedSyncRegistration.cs**
   - Registered `DailyPlanCompletion` in `ConfigureSyncTablesSQLite()`
   - Registered `DailyPlanCompletion` in `ConfigureSyncTablesPostgreSQL()`
   - Registered `UserActivity` in both methods
   - Sync direction: `UploadAndDownload`

4. **Repository Layer Updates**
   - Updated `DailyPlanCompletionRepository` to:
     - Generate GUID for new records
     - Filter all queries by `UserProfileId`
     - Call `TriggerSyncAsync()` after `SaveChangesAsync()`
   - Updated `UserActivityRepository` with same pattern

### Validation
- All builds pass (SQLite, PostgreSQL, multi-targeting)
- Migration tests execute without errors
- Sync registration verified in both database providers

## Decision Created

**Decision: CoreSync Entity Requirements** (documented in decisions/inbox/wash-sync-gaps.md)
- Establishes requirements for ALL synced entities
- Includes checklist for future synced entities
- Lists currently synced vs non-synced entities

## Files Modified

- `src/SentenceStudio.Shared/Services/SharedSyncRegistration.cs`
- `src/SentenceStudio.DAL/Repositories/DailyPlanCompletionRepository.cs`
- `src/SentenceStudio.DAL/Repositories/UserActivityRepository.cs`
- `src/SentenceStudio.DAL/Migrations/PostgreSQL/` (new migration files)
- `src/SentenceStudio.DAL/Migrations/SQLite/` (new migration files)

## Outcomes

✅ Data sync gap closed  
✅ DailyPlanCompletion and UserActivity now sync between mobile and web  
✅ Streak calculations will be consistent across devices  
✅ Multi-user data isolation verified  
✅ Foundation established for future synced entities  

## Related Issues

- #36 (Today's Plan progress not syncing)
- #37 (Streak badge inconsistency)
- #38 (Vocabulary count mismatches)

