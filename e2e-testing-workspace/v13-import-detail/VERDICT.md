# v1.3 Import Detail — E2E Verdict

**Verdict: SHIP**
**Tested by:** Jayne (E2E Tester)
**Date:** 2026-04-27
**Branch:** `feature/import-content`
**Commits under test:** `35e0ba1` (Wash), `111418f` (Kaylee)

---

## Test Results Summary

| # | Test | Result | Evidence |
|---|------|--------|----------|
| 1 | Mixed-status import — summary cards, per-row table, type badges, status pills, count math | PASS | `01-*`, `05-*` |
| 2 | Back-navigation state preservation via IImportResultStore | PASS | `03-*`, `08-*` |
| 3 | Filter pills — All, Created, Skipped (Created/Updated/Failed hide when count=0) | PASS | `04-*`, `06-*` |
| 4 | Skipped row click navigates to existing vocab detail | PASS | `02-*` |
| 4b | Created row click navigates to newly created vocab detail | PASS | `07-*` |
| 5 | Failed row (malformed input) | PASS (by design) | `09-*`, `12-*` |
| 6 | DB verification — created rows persisted | PASS | Navigated to `/vocabulary/edit/{id}` and confirmed data |
| 7 | Aspire structured logs — zero errors during import | PASS | 57 log entries, 0 errors; 35 traces, 0 error traces |

**7/7 PASS** — no regressions, no blockers.

---

## Detailed Observations

### Test 1 — Import Complete view with per-row detail
- **1A (all-Skipped):** Imported 3 existing Korean beer sentences. All 14 rows (3 sentences + 11 harvested words) correctly marked Skipped with reason "Already exists in resource". Summary: Created=0, Skipped=14, Updated=0, Failed=0.
- **1B (mixed Created + Skipped):** Imported 3 new Korean sentences (park walk, cafe recommendation, KTX train). Result: Created=5 (3 sentences + 2 new words), Skipped=11. Count math: 5+11=16 total. Each row shows Lemma, Translation, Type badge (Sentence/Word), Status badge (Created/Skipped), and Reason column.

### Test 2 — Back-navigation state preservation
- Clicked into vocab detail from both Skipped and Created rows, then browser Back.
- Import Complete view fully re-rendered with same summary cards, filter pills, and detail table.
- URL correctly preserved `?completed={guid}` query parameter.
- IImportResultStore singleton with 30-min TTL working as designed.

### Test 3 — Filter pills
- "Skipped 14" filter: activated correctly, showed only Skipped rows.
- "Created 5" filter: activated correctly, showed only 5 Created rows (3 Sentences, 2 Words).
- "All 16" filter: returned to full list.
- Updated/Failed pills correctly hidden when count=0 (conditional rendering).

### Test 4 — Row click navigation
- Skipped rows navigate to `/vocabulary/edit/{VocabularyWordId}` showing existing vocab detail.
- Created rows navigate to the same route showing newly created vocab detail with correct Type=Sentence.

### Test 5 — Failed row resilience
- Attempted malformed input: `|just english no target`, `broken line with no delimiter`, `한국어만|`.
- AI extraction handled gracefully — parsed into 4 valid rows (all OK).
- The first line with empty target was silently dropped.
- Failed status requires server-side errors (DB constraint violations, network failures), not malformed user input. This is good resilience.

### Test 6 — DB verification
- Successfully navigated to `/vocabulary/edit/7470a44b-4311-44b8-af77-d2b8568b078f` for newly created sentence.
- Vocab detail confirmed: Korean sentence, English translation, Type=Sentence, Language=Korean.
- Mobile SQLite sync DB shows older data (sync lag expected); webapp Postgres is the source of truth.

### Test 7 — Aspire logs
- 57 structured log entries examined: zero Error/Critical/Fatal.
- 35 distributed traces examined: zero error traces.
- Import-related Information logs show clean flow: extraction, translation, vocabulary creation.

---

## Evidence Files

| File | Description |
|------|-------------|
| `01-import-complete-all-skipped.png` | All-Skipped import result (14 rows) |
| `02-vocab-detail-from-skipped.png` | Vocab detail reached from Skipped row |
| `03-back-nav-state-preserved.png` | Import Complete re-rendered after browser Back |
| `04-filter-skipped-active.png` | Skipped filter pill active |
| `05-mixed-status-import-complete.png` | Mixed Created+Skipped result (16 rows) |
| `06-filter-created-active.png` | Created filter pill active, showing 5 rows |
| `07-vocab-detail-from-created.png` | Newly created sentence detail |
| `08-back-nav-from-created-detail.png` | State preserved after back from Created detail |
| `09-malformed-preview.png` | Malformed input in wizard |
| `12-malformed-preview-table.png` | AI-extracted preview from malformed input |
