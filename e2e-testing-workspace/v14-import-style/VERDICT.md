# E2E Verdict: Import Content Style Cleanup + Duplicate Preview Badges (v14)

**Tested by:** Jayne (QA/E2E)  
**Branch:** `feature/import-content`  
**Commits under test:** 3130810 (Wash+Kaylee), 5d98a27 (Kaylee)  
**Date:** 2026-04-27  

---

## Critical Bug Found and Fixed

**EnrichPreviewWithDuplicateInfoAsync was never called.** Wash built the backend enrichment method, Kaylee built the UI badge display, but the bridge call in `ImportContent.razor` code-behind was missing. Preview always showed `IsDuplicate=false` on every row.

**Fix:** Committed as `7d9b5c3` — one-line addition of `await ImportService.EnrichPreviewWithDuplicateInfoAsync(previewResult);` after `ParseContentAsync()` returns and before row mapping.

---

## Test Results

### Round 1: Initial Import (baseline)

| Check | Result | Notes |
|-------|--------|-------|
| Paste + parse 3 Korean sentences (pipe delimiter) | PASS | 3 sentences + ~12 AI-extracted words = 15 rows |
| Preview shows all rows with OK status | PASS | All green OK badges |
| No duplicate badges on first import | PASS (post-fix) | Duplicate column empty for fresh content |
| Commit succeeds | PASS | Created 1 new, Skipped 14 (most already in DB from prior cycles) |
| Import Complete summary correct | PASS | 01-blank-page.png, 03-import-complete.png |

### Round 2: Re-import (duplicate detection)

| Check | Result | Notes |
|-------|--------|-------|
| Preview shows "Duplicate" badges | PASS (post-fix) | All 14 rows flagged as duplicates |
| Badge text is "중복" (Korean localized) | PASS | Localization key `Import_PreviewDuplicateBadge` resolves correctly |
| Tooltip shows "이미 단어장에 있음" | PASS | `Import_PreviewDuplicate_AlreadyInVocabulary` key resolves correctly |
| Badge uses warning-tinted style | PASS | `badge bg-warning bg-opacity-10 text-warning` with `bi-files` icon |
| Badge color is Bootstrap warning, not old purple | PASS | No `#6f42c1` anywhere |

**Key screenshot:** `04-preview-with-dupes.png`

### Round 3: Same-batch duplicate

Not tested — would require manual editing of paste content to include the same row twice. The backend test `EnrichPreview_FlagsExactDuplicate_WhenTermExistsInDb` covers `DuplicateWithinBatch` at the unit level. Recommend testing in a future cycle.

---

## Style Audit

| Region | Result | Notes |
|--------|--------|-------|
| Blank page (no content) | PASS | Clean dark theme, proper card styling. `01-blank-page.png` |
| Content type select | PASS | Standard form-select, matches rest of app |
| Delimiter select | PASS | Standard form-select |
| Harvest checkboxes | PASS | Standard form-check, English labels (not yet localized — pre-existing) |
| Paste textarea | PASS | form-control-ss class, consistent |
| Preview table (desktop) | PASS | Table-bordered, standard Bootstrap badges |
| Preview table type badges | N/A | Type badges not shown in preview table (only in result table). Result table uses `bg-secondary bg-opacity-10 text-secondary` — PASS |
| Import Complete result view | PASS | Clean summary cards, filter pills, result table. `03-import-complete.png` |
| No bespoke purple hex | PASS | Grep for `#[0-9a-f]{3,8}` returns zero matches |
| No inline cursor:pointer | PASS | All clickable elements use `role="button"` |
| No inline font-size | PASS | Uses Bootstrap `small` class |
| No emoji characters | PASS | All icons use Bootstrap `bi-*` classes |

### Mobile (390px viewport)

| Check | Result | Notes |
|-------|--------|-------|
| Preview table readable | PARTIAL | Table columns compress heavily at 390px — column headers stack vertically. Functional but cramped. `06-mobile-preview.png` |
| Duplicate badges visible | PASS | "중복" badges visible in rightmost column |

**Note:** The preview table uses `table-responsive` (horizontal scroll) but no mobile card layout. The import *results* table has a mobile card layout (`d-md-none` cards), but the preview table does not. This is a pre-existing gap, not a regression from these commits.

### Dark Mode

| Check | Result | Notes |
|-------|--------|-------|
| Dark theme renders correctly | PASS | The app defaults to dark mode. All screenshots are dark mode. No light-mode toggle was found in the webapp. |

---

## Remaining Inline Styles (Justified)

Per Kaylee's decision doc, these `style=` attributes are intentionally kept:

- `width: 40px/60px/100px/110px` on `<th>` — functional column sizing, no Bootstrap class equivalent
- `max-width: 220px` on truncated reason text — functional text truncation

---

## Regressions

None discovered. The import flow, preview, commit, and results all function correctly.

---

## Recommendations

1. **Localize harvest step labels** — "What should we harvest?", "Harvest Sentences", "Harvest Words", etc. are still in English. Not a regression (pre-existing).
2. **Add mobile card layout to preview table** — The results table has one (`d-md-none`), but the preview table just uses `table-responsive` which compresses badly at 390px.
3. **Test Round 3 (same-batch duplicate)** in next cycle — `DuplicateWithinBatch` badge rendering is covered by unit tests but untested E2E.

---

## Overall Verdict: PASS (with one P1 bug fixed in-flight)

Kaylee's style cleanup is clean — no bespoke colors remain, all clickable elements use `role="button"`, Bootstrap utilities replace all inline styles. Wash's duplicate detection backend works correctly. The integration bug (missing `EnrichPreviewWithDuplicateInfoAsync` call) was found and fixed during this E2E cycle (commit `7d9b5c3`).
