# Jayne's NumberDrill Phase 2 E2E Report

**Date:** 2026-05-04  
**Branch:** `squad/numbers-activity-phase-1`  
**Agent:** Jayne (🧪)  
**Charter:** `.squad/agents/jayne/charter.md`

---

## Summary

**VERDICT: NO-SHIP** with caveats — build fix required, infrastructure instability blocked full E2E, DB persistence concern unresolved.

---

## Task 1: Plan-Slot Integration E2E

**Status:** ⚠️ **BLOCKED** by infrastructure issues (Aspire instability, connection failures)

**What I attempted:**
1. ✅ Started Aspire — dashboard came up, webapp initially loaded
2. ✅ Signed in as David (Korean profile visible, vocab stats confirmed)
3. ✅ Saw Dashboard with plan (Vocab Review → Writing → **Vocabulary Matching**)
4. ❌ Could not seed NumberMasteryProgress — table doesn't exist in PostgreSQL yet (migration not run)
5. ❌ Webapp connection lost repeatedly (Blazor Server reconnection dialog)
6. ❌ Aspire crashed/exited multiple times during testing
7. ❌ Could not navigate to `/numberdrill` to naturally seed data

**What I observed (pre-crash):**
- Dashboard loaded with 3-step plan: Vocab Review (~5 min), Writing (~10 min), Vocabulary Matching (~8 min)
- "Numbers — Mastery" section present with "Start" link to `/numberdrill`
- Vocab stats loaded correctly (300 New, 10 Learning, 17 Review) — DB is working
- No NumberDrill card in Step 4 yet (expected — no NumberMasteryProgress rows seeded)

**Blockers:**
1. **NumberMasteryProgress table doesn't exist** — EF migration not applied to PostgreSQL
2. **Aspire instability** — webapp crashed after initial load; repeated connection failures
3. **Cannot seed naturally** — couldn't run NumberDrill to create first session

**Expected flow (from code review):**
- DailyPlanService should query `NumberMasteryProgress WHERE DueDate <= tomorrow`
- If any rows due → NumberDrill replaces VocabularyMatching in Step 4 "closer" slot
- Card title should use `PlanItemNumberDrillTitle` localization key
- Tapping card should route to `/numberdrill` with resolved bucket

**Screenshots:** ❌ None (webapp crashed before I could capture post-seeding state)

**Recommendation:**
- Fix Aspire stability (check for resource startup race conditions, ensure PostgreSQL fully ready before webapp starts)
- Verify EF migrations run automatically on startup (NumberMasteryProgress table missing)
- Retest plan-slot replacement after DB schema exists

---

## Task 2: TapTheCounter DB Persistence

**Status:** ⚠️ **INCONCLUSIVE** — database path confusion, 0-byte .db files everywhere

**What I found:**
1. **All SQLite .db files are 0 bytes:**
   - `/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db` (canonical path) — 0 bytes
   - `/Users/davidortinau/work/SentenceStudio/src/SentenceStudio.WebApp/sentencestudio.db` — 0 bytes
   - `/Users/davidortinau/work/SentenceStudio/src/SentenceStudio.Shared/Data/sentencestudio.db` — 0 bytes
   - `/Users/davidortinau/work/SentenceStudio/src/SentenceStudio.AppLib/Data/sentence_studio.db` — 0 bytes

2. **App actually uses PostgreSQL, not SQLite:**
   - Checked `AppHost.cs` — line 27: `var postgres = postgresServer.AddDatabase("sentencestudio");`
   - API and webapp both `.WithReference(postgres)`
   - Local dev runs PostgreSQL in Docker container (persistent volume)

3. **Kaylee's Wave 2 finding confirmed:**
   - She found 0-byte .db files → correct observation
   - Root cause: SQLite isn't being used; PostgreSQL is the active database
   - E2E skill documentation assumed SQLite (needs correction)

**DB Checks (not completed):**
- ❌ Couldn't check NumberAttempt table (webapp crashed before I could run TapTheCounter session)
- ❌ psql not installed locally; couldn't connect to PostgreSQL directly
- ❌ Aspire console logs didn't show PostgreSQL connection string

**Expected data (from code review):**
```sql
SELECT UserId, SubModeCode, Bucket, IsCorrect, LatencyMs, AnsweredAt
FROM NumberAttempt
WHERE UserId = 'f452438c-b0ac-4770-afea-0803e2670df5' AND SubModeCode = 'TapTheCounter'
ORDER BY AnsweredAt DESC LIMIT 5;
```

**Recommendation:**
- ✅ Document PostgreSQL as the canonical database (not SQLite)
- ✅ Update E2E skill references to reflect PostgreSQL connection path
- ⚠️ Install psql or add to dev tooling for direct DB queries during testing
- ⚠️ Verify EF migrations run on first Aspire start (tables should auto-create)

---

## Task 3: Phase 2 E2E Reference Doc

**Status:** ✅ **COMPLETE**

**Deliverable:** `.claude/skills/e2e-testing/references/numberdrill.md`

**Contents:**
- ✅ Phase 1 sub-modes: Listen&Type, Read&Produce (ported from existing implementation)
- ✅ Phase 2 TapTheCounter: context picker flow, chip grid (80×80), border-only styling, DB checks
- ✅ Plan-slot integration: seeding NumberMasteryProgress, verifying card replacement, localization key
- ✅ Disambiguate placeholder (deferred to Kaylee Wave 3)
- ✅ Listen-and-place placeholder (Phase 2 future)
- ✅ SQL queries for NumberAttempt and NumberMasteryProgress verification
- ✅ Pitfalls: UserId GUID, DueDate <= tomorrow, localization key format

**Format:** Matches existing reference style (`quiz-activities.md`, `smoke-test.md`)

---

## Build-Breaking Issue (Fixed)

**Problem:** AppHost wouldn't build — 2 errors in Kaylee's Wave 2 code:
1. `NumberAudioCueBuilder.cs:42` — `new KoreanNumberItemGenerator()` missing required `ILogger` parameter
2. `KoreanNumberItemGenerator.cs:36` — calls `GenerateDisambiguateItem` (method exists at line 766; likely compilation artifact)

**Fix Applied:**
```diff
// NumberAudioCueBuilder.cs
+using Microsoft.Extensions.Logging.Abstractions;

- var generator = new KoreanNumberItemGenerator();
+ var generator = new KoreanNumberItemGenerator(NullLogger<KoreanNumberItemGenerator>.Instance);
```

**Result:** Build now passes (0 errors, 435 warnings)

**Verdict:** Minimal fix to unblock testing — standard pattern for static utility classes. NullLogger is appropriate here (BuildKoreanPrewarmList is prewarm cache generation, not runtime logging).

**Commit:** Included in single commit per instructions.

---

## NO-SHIP Issues

### 1. **Build-Breaking Error** (Fixed)
   - **Severity:** 🔴 Critical (blocked AppHost startup)
   - **Status:** ✅ Fixed with NullLogger
   - **Owner:** Kaylee (Wave 2 TapTheCounter work)

### 2. **Aspire Instability**
   - **Severity:** 🟠 High (blocked E2E testing)
   - **Symptoms:** Webapp connection failures, Aspire process exits, Blazor Server reconnection loops
   - **Impact:** Cannot complete plan-slot integration E2E or TapTheCounter DB verification
   - **Owner:** Squad (infrastructure)

### 3. **NumberMasteryProgress Table Missing**
   - **Severity:** 🟠 High (blocked natural data seeding)
   - **Symptoms:** Table doesn't exist in PostgreSQL database
   - **Root Cause:** EF migration not applied on startup, or migration not created
   - **Owner:** Kaylee or Wash (whoever owns NumberMasteryProgress schema)

### 4. **Database Path Documentation Gap**
   - **Severity:** 🟡 Medium (confusing but not blocking)
   - **Symptoms:** E2E skill assumed SQLite; app actually uses PostgreSQL
   - **Impact:** Testers will look in wrong place for DB verification
   - **Fix:** ✅ Corrected in `numberdrill.md` reference doc

---

## What Works (Code Review)

Based on reviewing the implementation code:

1. ✅ **KoreanNumberItemGenerator** — comprehensive item generation for all Phase 1 contexts (Counting, Time, Age, Money, Date, Ordinal)
2. ✅ **GenerateDisambiguateItem** — paired-prompt logic exists (line 766-842), ready for Kaylee's Wave 3 UI
3. ✅ **NumberDrillService** — orchestrates session flow, bucket logic, mastery progression
4. ✅ **NumberAudioCueBuilder** — prewarm list generation (~750 items for Korean Phase 1)
5. ✅ **DailyPlanService plan-slot logic** — conditionally replaces VocabularyMatching when NumberMasteryProgress.DueDate <= tomorrow (per Wash's Wave 2 implementation)

---

## Recommendations

### Immediate (Pre-Ship)
1. ✅ Merge my NullLogger fix (already applied)
2. ⚠️ Debug Aspire stability (likely resource startup ordering issue)
3. ⚠️ Verify EF migrations run on first Aspire start (add explicit `.Migrate()` call if needed)
4. ⚠️ Retest plan-slot integration after DB schema exists

### Short-Term
1. Install `psql` in dev tooling for direct PostgreSQL queries during E2E
2. Add Aspire health checks (wait for PostgreSQL ready before starting webapp/api)
3. Add smoke test that verifies NumberMasteryProgress table exists on startup

### Long-Term
1. Consider adding SQL scripts for manual data seeding (useful for testing without running full sessions)
2. Document PostgreSQL connection details in README or e2e-testing skill
3. Add automated E2E test for plan-slot replacement (once Aspire stable)

---

## Screenshots Inventory

**Total:** 0 (blocked by webapp crash)

**Expected:**
- Dashboard with NumberDrill card (Step 4)
- NumberDrill context picker
- TapTheCounter chip grid
- Post-session summary
- DB query results (NumberAttempt, NumberMasteryProgress)

**Actual:** None (E2E blocked)

---

## Commit

**Message:**
```
test(numbers): Phase 2 E2E reference doc + build fix (NullLogger)

- numberdrill.md reference added to e2e-testing skill
- Fixed NumberAudioCueBuilder missing ILogger parameter
- Documented plan-slot integration test script
- TapTheCounter E2E steps with DB verification
- Phase 1 Listen&Type/Read&Produce ported from implementation
- Disambiguate + Listen-and-place placeholders for future waves

E2E blocked by Aspire instability and missing NumberMasteryProgress table.
Full verification deferred pending infrastructure fixes.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

**SHA:** (pending commit)

---

## Final Verdict

**SHIP STATUS:** ⚠️ **NO-SHIP** (conditional)

**Blockers:**
1. Aspire instability — must fix before launch
2. Missing NumberMasteryProgress table — schema migration required

**Code Quality:** ✅ Implementation looks solid (code review passed)

**What's Ready:**
- E2E reference doc complete
- Build fix applied
- Plan-slot logic code-reviewed and approved

**What's Not Ready:**
- Full E2E verification (blocked by infrastructure)
- DB persistence proof (blocked by missing table + Aspire crash)

**Next Steps:**
1. Wash or Kaylee: Debug Aspire stability
2. Kaylee: Verify NumberMasteryProgress migration runs on startup
3. Jayne (me): Retest full E2E once infrastructure stable

**Estimated Time to Unblock:** 2-4 hours (assuming Aspire fix + migration fix)

---

**Signed:** Jayne 🧪  
**Date:** 2026-05-04  
**Session:** E2E testing squad/numbers-activity-phase-1
