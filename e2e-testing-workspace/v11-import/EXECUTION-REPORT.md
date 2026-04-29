# v1.1 Data Import — E2E Execution Report

**Tester:** Jayne (Squad QA)  
**Date:** 2026-04-26  
**Environment:** Aspire local dev (PostgreSQL 17.6, Blazor webapp at localhost:7071)  
**Verdict:** **DO-NOT-SHIP** (3 P0/P1 bugs block release)

---

## Scenario Results

| Scenario | Name | Result | Notes |
|----------|------|--------|-------|
| **J** | Migration gating | **PASS** | Both migrations applied. Zero Unknown rows. Backfill ran correctly. |
| **A** | Vocabulary CSV regression | **PASS (with bug)** | 5 rows parsed, 2 created, 3 deduped. BUG-1: NULL UserProfileId. |
| **B** | Korean phrases (Margo) | **FAIL** | Created 3, Skipped 2. BUG-3: Multi-word phrases stored as LexicalUnitType=1 (Word) instead of 2 (Phrase). |
| **C** | Transcript prose | **FAIL** | Created 12, Skipped 1. BUG-2: Transcript text NOT stored on LearningResource despite checkbox checked. BUG-1 repeat. |
| **D** | Auto-detect high confidence | **PASS** | CSV auto-detected as Vocabulary (95% confidence). Auto-routed correctly. Change button present. |
| **E** | Auto-detect medium confidence | **CONDITIONAL PASS** | Ambiguous blob classified as Transcript (85%). 85% is the boundary — technically auto-routes. Could not trigger mandatory chooser. |
| **F** | Auto-detect low confidence | **FAIL** | Noise input ("hhh/123/ok") classified as Phrases at 95% confidence. AI never returns low confidence. |
| **G** | Zero checkboxes validation | **PASS** | Inline error "Please select at least one harvest option" blocks commit. |
| **H** | Checkbox override | **FAIL** | Import succeeded (3 created, 5 skipped), but BUG-2 confirmed again: Transcript text NOT saved despite checkbox checked. |
| **I** | Cancel during medium confidence | **SKIPPED** | Could not trigger medium confidence (see E, F). |

**Edge cases:** Not executed (blocked by P0 bugs — shipping priority).

---

## Bugs Found

### BUG-1 (P1): NULL UserProfileId on LearningResource
- **Where:** Every import creates a LearningResource with NULL `UserProfileId`
- **Impact:** Imported resources are orphaned — not associated with the logged-in user. Other users may see or miss resources they shouldn't.
- **Evidence:** All 4 test resources have `has_user = false`
- **Owner:** Wash (API/service layer — `CommitImportAsync` in `ContentImportService.cs`)

### BUG-2 (P0): Transcript text never stored
- **Where:** Checking "This is a Transcript" checkbox does nothing — `LearningResource.Transcript` is always NULL
- **Impact:** The entire transcript import feature is non-functional. Users who import transcripts lose the original text completely.
- **Evidence:** "Seoul Trip Transcript" and "Margo Override Test H" both have empty Transcript column despite checkbox being checked
- **Repro:** Scenario C or H — select Transcript type or check Transcript checkbox, commit, verify DB
- **Owner:** Wash (service layer — transcript storage path in `CommitImportAsync`)

### BUG-3 (P1): LexicalUnitType not set correctly during import
- **Where:** Multi-word phrase terms (e.g., "안 좋다", "잘 못 보다") are stored as `LexicalUnitType=1` (Word) instead of `LexicalUnitType=2` (Phrase)
- **Impact:** Phrase-level vocabulary entries are misclassified. Practice activities that filter by LexicalUnitType will include phrases in word-only drills and exclude them from phrase drills.
- **Evidence:** Scenario B — all 5 Margo terms stored as Word, including 3 multi-word phrases
- **Owner:** Wash (service layer — LexicalUnitType assignment in commit path)

### BUG-4 (P2): AI confidence gate never returns low confidence
- **Where:** Auto-detect always returns 85-95% confidence regardless of input quality
- **Impact:** The low-confidence and medium-confidence UX paths (mandatory chooser, "can't auto-detect" message) are effectively dead code — users never see them
- **Evidence:** Scenario F — pure noise ("hhh/123/ok") got 95% confidence as "Phrases"
- **Owner:** Wash/River (AI classification prompt or confidence calibration)

### Minor Issues
- **Harvest section not localized:** "What should we harvest?" heading and checkbox labels are in English while the rest of the UI is Korean. Cosmetic. Owner: Kaylee.
- **Resource title silently required:** First commit click in Scenario A appeared to do nothing when title was empty. No inline validation error shown. Owner: Kaylee.

---

## Checkbox Defaults Verified

| Content Type | Transcript | Phrases | Words |
|-------------|-----------|---------|-------|
| Vocabulary (어휘) | OFF | OFF | ON |
| Phrases (문구) | OFF | ON | ON |
| Transcript (대본) | ON | OFF | ON |
| Auto-detect (자동 감지) | depends on classification | | |

All defaults match the spec.

---

## DB State After Testing

**LearningResources created:**
| Title | MediaType | UserProfileId | Transcript |
|-------|-----------|---------------|------------|
| Test Vocab CSV Import | Vocabulary List | NULL | NULL |
| Captain's Margo Phrases | Vocabulary List | NULL | NULL |
| Seoul Trip Transcript | Transcript | NULL | NULL |
| Margo Override Test H | Transcript | NULL | NULL |

**VocabularyWord distribution:** 132 Word, 3 Phrase, 0 Unknown

---

## Screenshots

| File | Description |
|------|-------------|
| screenshot-A-preview.png | Scenario A: CSV preview with 5 rows |
| screenshot-A-result.png | Scenario A: Import result (Created 2, Skipped 3) |
| screenshot-B-preview.png | Scenario B: Margo phrases AI extraction preview |
| screenshot-B-result.png | Scenario B: Import result (Created 3, Skipped 2) |
| screenshot-C-preview.png | Scenario C: Transcript preview with harvest checkboxes |
| screenshot-C-result.png | Scenario C: Import result (Created 12, Skipped 1) |
| screenshot-D-autodetect.png | Scenario D: Auto-detect banner showing 95% Vocabulary |
| screenshot-E-autodetect-medium.png | Scenario E: Auto-detect banner showing 85% Transcript |
| screenshot-F-lowconf.png | Scenario F: Noise input classified as Phrases 95% |
| screenshot-G-zerocheckbox.png | Scenario G: Validation error for zero checkboxes |
| screenshot-H-override.png | Scenario H: Import result after checkbox override |

---

## Verdict: DO-NOT-SHIP

Three bugs block release:
1. **BUG-2 (P0):** Transcript storage is completely broken — core feature does not function
2. **BUG-1 (P1):** All imported resources orphaned (no user association)
3. **BUG-3 (P1):** LexicalUnitType classification broken during import

The checkbox harvest model UI works correctly (defaults, validation, override). The auto-detect confidence gate works but the AI never returns low confidence, making the medium/low UX paths untestable. Migration and backfill (Scenario J) are solid.

**Recommendation:** Fix BUG-2, BUG-1, and BUG-3 in the service layer (`CommitImportAsync`), then re-run Scenarios A, B, C, H to verify. BUG-4 can be addressed post-ship as a calibration issue.
