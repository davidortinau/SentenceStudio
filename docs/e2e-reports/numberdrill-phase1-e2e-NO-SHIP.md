# NumberDrill Phase 1 E2E Test - SHIP BLOCKER FOUND

## Test Status: **NO-SHIP**

Date: 2026-05-04  
Tester: Jayne  
Branch: `squad/numbers-activity-phase-1`  
Backend Tests: 52/52 ✅ (Wash confirmed)  

---

## Summary

NumberDrill activity page `/numberdrill` **crashes on load** with unhandled Blazor circuit exception. Root cause: **Migration `20260504174821_NumbersActivityPhase1` was not applied to the database**, causing page to fail when querying non-existent `NumberCounter` and `NumberContext` tables.

---

## Critical Bug: Migration Not Applied

### Evidence

1. **Page crash on navigation to `/numberdrill`:**
   ```
   [ERROR] Error: There was an unhandled exception on the current circuit, so this circuit will be terminated.
   ```

2. **Database state verification:**
   ```sql
   sqlite3 "/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db"
   > .tables
   # No NumberCounter or NumberContext tables found
   
   > SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 5;
   20260317205704_AddLanguageToVocabularyWord
   20260315013232_AddRefreshTokens
   20260315011600_AddIdentity
   20260307234624_AddFamiliarStatusAndVerification
   20260307201226_AddWordAssociationScore
   ```

3. **Migration file exists but not applied:**
   ```bash
   $ find src -name "*20260504174821*"
   src/SentenceStudio.Shared/Migrations/Sqlite/20260504174821_NumbersActivityPhase1.cs
   src/SentenceStudio.Shared/Migrations/20260504174821_NumbersActivityPhase1.cs
   ```

4. **Database also missing `20260503221947_AddRefreshTokenReplacedBy`** (Wash's April 28 commit)

### Impact

- ❌ NumberDrill page completely non-functional
- ❌ No NumberContent tables → no seed data → no activity can run
- ❌ Dashboard Numbers Insights tile renders empty state (which is CORRECT for no data, but users can't START because page crashes)

### Root Cause

The server database file at `~/Library/Application Support/sentencestudio/server/sentencestudio.db` had migrations only through `20260317205704_AddLanguageToVocabularyWord` (March 17). Two subsequent migrations were never applied:
- `20260503221947_AddRefreshTokenReplacedBy`
- `20260504174821_NumbersActivityPhase1`

This suggests either:
1. The AppHost didn't call `MigrateAsync()` on startup
2. The database file is from an older session and migrations failed silently
3. The `UserProfileRepository.GetAsync()` path (where migrations run per Captain's directive) wasn't hit during Aspire startup

---

## Verification Gates (Attempted)

### ✅ Passed
- [ ] **Dashboard empty state renders + correct copy** → ✅ PASS  
  Screenshot: `numberdrill-dashboard-empty.png`  
  Tile shows: "Start mastering Korean numbers / Your first session unlocks Time, Age, and Counting practice."

### ❌ Blocked by Migration Bug
All remaining gates blocked:
- [ ] Setup screen 3 contexts + Bootstrap icons
- [ ] System color chips visible
- [ ] Counting Read-and-Produce session completes
- [ ] Correct answer triggers positive feedback
- [ ] Incorrect answer triggers grader tip
- [ ] Time session enforces MM ∈ {00,15,30,45}
- [ ] Summary screen renders score
- [ ] Streak NOT broken by incorrect answers
- [ ] Dashboard tile populates with progress

**Cannot proceed without database schema.**

---

## Required Fix

1. **Investigate migration application flow:**
   - Verify `UserProfileRepository.GetAsync()` is called during AppHost startup
   - Confirm `MigrateAsync()` runs for both PostgreSQL (Azure) and SQLite (local dev)
   - Check for silent migration failures in Aspire logs

2. **Test migration on fresh database:**
   - Delete stale DB: `rm ~/Library/Application\ Support/sentencestudio/server/sentencestudio.db*`
   - Start AppHost
   - Verify `SELECT * FROM __EFMigrationsHistory` includes `20260504174821`
   - Verify `SELECT COUNT(*) FROM NumberCounter` returns 6 (3 contexts × 2 sub-modes + 살)

3. **Run NumberContentSeeder after migration:**
   - Confirm seed runs automatically on startup OR provide manual trigger
   - Verify 3 contexts (Counting, Time, Age) with 5 counters each + 살

---

## Deliverables

- Screenshot: `numberdrill-dashboard-empty.png` ✅ (committed)
- Bug report: This file
- Verdict: **NO-SHIP** — Migration bug blocks all E2E verification

---

## Next Steps

1. Squad coordinator review this bug report
2. Assign migration fix (likely Wash or backend specialist)
3. After fix: Jayne re-runs full E2E test suite on this branch
4. Only proceed to Wave 5 (Scribe) after E2E is GREEN

---

## Test Environment

- macOS Darwin  
- .NET 10.0 SDK  
- Aspire CLI `13.3.0-preview.1.26203.28`  
- Database: SQLite 3.x  
- Browser: Playwright (Chromium)  

---

## Logs & Artifacts

- Dashboard screenshot: `numberdrill-dashboard-empty.png`
- Page crash console: `.playwright-mcp/console-2026-05-04T23-42-21-604Z.log` lines 3-4
- Aspire logs: `~/.aspire/logs/cli_20260504T233842_a12c9423.log`

---

**End of Report**
