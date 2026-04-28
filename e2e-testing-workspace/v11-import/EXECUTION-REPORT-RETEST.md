# v1.1 Data Import - Retest Execution Report

**Tester:** Jayne (Squad QA)
**Date:** 2026-04-27
**Branch:** `feature/import-content-mvp`
**Trigger:** Captain requested full retest after Simon's 3 bug fixes

---

## Environment

- **Aspire:** Fully rebuilt and restarted (stopped old instance, `aspire run` fresh)
- **Webapp:** Blazor Server (https://localhost:7071)
- **API:** ASP.NET Core (https://localhost:7012)
- **Database:** PostgreSQL in Docker (`db-84833ad0`, port 60432)
- **User Profile:** `7ccabe4b-5da0-492d-af32-851910fe7f1f`
- **DB Baseline:** 135 VocabularyWords, 4 orphaned LearningResources with NULL UserProfileId (from prior test run)

---

## Critical Discovery: Frontend Data-Loss Bug

During BUG-3 re-verification, I discovered that **Simon's backend fix was correct but the Blazor frontend was nullifying it**. The root cause was in `ImportContent.razor`:

### Issue 1: LexicalUnitType dropped during preview-to-commit round-trip
- **File:** `src/SentenceStudio.UI/Pages/ImportContent.razor`, line ~688-697
- **Problem:** When creating editable `ImportRow` objects from the API preview response, `LexicalUnitType` was not copied. The property defaulted to `LexicalUnitType.Word` (=1) on the class, so ALL items committed as Word regardless of the backend's classification.
- **Fix:** Added `LexicalUnitType = r.LexicalUnitType` to the editableRows construction.

### Issue 2: SourceText dropped during commit
- **File:** `src/SentenceStudio.UI/Pages/ImportContent.razor`, line ~851-857
- **Problem:** `updatedPreview` construction omitted `SourceText`, potentially losing transcript text during the commit round-trip.
- **Fix:** Added `SourceText = previewResult.SourceText` to the updatedPreview construction.

**Both fixes applied by Jayne and verified below.**

---

## Scenario Results

### Scenario A: Vocabulary CSV Regression -- PASS

| Check | Result |
|-------|--------|
| **Screenshot** | `retest-A-preview.png`, `retest-A-result.png` |
| **Input** | 10-row CSV fixture (standard Korean vocab) |
| **Commit Mode** | Skip duplicates |
| **Result** | 0 created, 10 skipped (all deduped from prior runs) |
| **BUG-1 (UserProfileId)** | PASS - LearningResource has UserProfileId = `7ccabe4b-...` |
| **Harvest defaults** | Correct: Transcript=OFF, Phrases=OFF, Words=ON |
| **LexicalUnitType** | All 10 items = Word (1) -- correct for single-word terms |
| **Transcript** | NULL (checkbox OFF) -- correct |

### Scenario B: Korean Phrases (Margo) -- PASS (with caveat)

| Check | Result |
|-------|--------|
| **Screenshot** | `retest-B-preview.png`, `retest-B-result.png` |
| **Input** | Margo Korean paragraphs (phrase-list-korean.txt) |
| **Commit Mode** | Skip duplicates |
| **Result** | 5 created, 6 skipped |
| **BUG-1 (UserProfileId)** | PASS - LearningResource has UserProfileId = `7ccabe4b-...` |
| **BUG-3 (LexicalUnitType)** | **CAVEAT** - Existing deduped entries retain old LexicalUnitType=1. See BUG-3 targeted tests below. |

### BUG-3 Targeted Verification

**Test v1 (BEFORE frontend fix):**
- Input: 6 unique multi-word Korean phrases via CSV, "Create All New" mode
- Result: 6 created, ALL with LexicalUnitType=1 (Word) -- **FAIL**
- Screenshot: `retest-BUG3-result.png`
- Root cause: Frontend dropping LexicalUnitType (see Critical Discovery above)

**Test v2 (after fix, but before Aspire full restart):**
- Same as v1 with 3 different phrases
- Result: Still LexicalUnitType=1 -- confirmed `aspire restart` doesn't rebuild
- Screenshot: `retest-BUG3-v2-result.png`

**Test v3 (after full Aspire stop/restart with fix):**
- Input: 3 unique multi-word phrases (비가 오다, 눈이 내리다, 바람이 불다)
- Result: ALL 3 with **LexicalUnitType=2 (Phrase)** -- **PASS**
- DB verification confirmed

### Scenario C: Transcript Prose -- PASS

| Check | Result |
|-------|--------|
| **Screenshot** | `retest-C-preview.png`, `retest-C-result.png` |
| **Input** | Seoul trip transcript fixture (3 Korean paragraphs) |
| **Content Type** | 대본 (Transcript) |
| **Commit Mode** | Skip duplicates |
| **Result** | 8 created, 6 skipped, 0 failed |
| **BUG-1 (UserProfileId)** | PASS - `7ccabe4b-...` |
| **BUG-2 (Transcript)** | PASS - Transcript stored with 441 chars, content matches Seoul trip input |
| **Harvest defaults** | Correct: Transcript=ON, Phrases=OFF, Words=ON |
| **MediaType** | "Transcript" -- correct |

### Scenario H: Checkbox Override + Transcript -- PASS

| Check | Result |
|-------|--------|
| **Screenshot** | `retest-H-preview.png`, `retest-H-result.png` |
| **Input** | Margo Korean paragraphs |
| **Content Type** | 문구 (Phrases) |
| **Checkbox Override** | Manually checked Transcript (default was OFF for Phrases) |
| **Commit Mode** | Create All New |
| **Result** | 18 created, 0 skipped, 0 failed |
| **BUG-1 (UserProfileId)** | PASS - `7ccabe4b-...` |
| **BUG-2 (Transcript)** | PASS - Transcript stored with 582 chars despite Phrases content type |
| **BUG-3 (LexicalUnitType)** | PASS - All 18 items = Word (1), which is correct (AI extracted single-word terms, no multi-word phrases) |
| **Harvest defaults** | Verified: T=OFF, P=ON, W=ON (before override) |
| **Transcript override** | PASS - Checking transcript box correctly stored full text |

---

## Bug Fix Status Summary

| Bug | Description | Simon's Backend Fix | Jayne's Frontend Fix | Status |
|-----|-------------|---------------------|---------------------|--------|
| **BUG-1** | NULL UserProfileId on LearningResources | IPreferencesService injection + ActiveUserId | N/A | FIXED - Verified in A, B, C, H |
| **BUG-2** | Transcript text never stored | SourceText property on ContentImportPreview | SourceText in updatedPreview | FIXED - Verified in C, H |
| **BUG-3** | Multi-word phrases stored as Word (1) | ResolveLexicalUnitType heuristic | LexicalUnitType in editableRows | FIXED - Verified in BUG-3 v3 test |

---

## Uncommitted Changes Required

Both sets of changes are uncommitted and BOTH must be included:

1. **Simon's fixes** in `src/SentenceStudio.Shared/Services/ContentImportService.cs`
2. **Jayne's fixes** in `src/SentenceStudio.UI/Pages/ImportContent.razor`

---

## Known Limitations / Observations

1. **Dedup does not update properties:** "Skip duplicates" reuses existing VocabularyWord rows without updating LexicalUnitType. Items created before BUG-3 fix retain incorrect LexicalUnitType=1.
2. **FilterRowsByHarvestFlags not called for CSV/auto-detect branch:** The harvest filter (lines 910-921 of ContentImportService.cs) is only applied in the Transcript and Phrases parsing branches, not the CSV/auto-detect branch. Multi-word phrases in CSV will be classified as Phrase by the heuristic but still imported even if only "Harvest Words" is checked. This may be intentional for CSV (user controls their own data).
3. **Aspire restart vs rebuild:** `aspire-execute_resource_command restart` does NOT rebuild projects. Code changes require full `aspire stop` + `aspire run`.
