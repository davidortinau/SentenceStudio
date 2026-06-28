## Active Decisions

(Most recent decisions below. Archived decisions in `decisions/archive/scribe-archive-2026-06-26.md` and earlier archive/processed folders.)

---

### 2026-06-26 — Quick-add existing vocabulary to a Learning Resource

**Session:** Quick-add existing vocabulary to a Learning Resource
**Surface:** Blazor WebApp, `ResourceEdit.razor`
**Requested by:** Captain (David Ortinau)
**Status:** Implemented and E2E verified by Squad agents.

#### Decision

Learning Resource editing now supports quick-add of existing vocabulary through a debounced typeahead backed by a user-scoped repository search. The search is intentionally strict on `Language` and excludes vocabulary words already mapped to the target resource. Inline create-on-miss stamps new `VocabularyWord.Language` from the resource language so newly-created words become eligible for future strict-language lookup.

#### Backend

Wash added `LearningResourceRepository.SearchVocabularyWordsForResourceAsync(query, language, resourceId, limit = 20)` at `src/SentenceStudio.Shared/Data/LearningResourceRepository.cs:737`. The method is user-scoped, applies a strict language filter, excludes words already mapped to the resource, returns best matches first, and is covered by `LearningResourceRepositoryVocabularyLookupTests` (5/5 passing per handoff).

#### WebApp UI

Kaylee updated `ResourceEdit.razor` with debounced typeahead search, keyboard navigation, input refocus after add, inline create-on-miss, per-word remove, a language guard, and a collapsed Bulk import toggle. The UI uses minimal local CSS and new localized resource strings in `AppResources.resx` and `AppResources.ko.resx`.

#### E2E verification

Jayne verified the flow through Aspire WebApp + Playwright + Postgres: quick-add matched existing Korean words, exact attached words were excluded from subsequent results, inline create persisted and mapped a new word with `Language=Korean`, Bulk import collapsed/expanded correctly, remove persisted, browser console had no errors, and Aspire structured logs had no exceptions.

#### Language=NULL ruling

Bulk-import-seeded vocabulary words can have `VocabularyWord.Language = NULL`. The quick-add search's strict language filter intentionally excludes those rows. This is accepted behavior, not a bug: inline create stamps the resource language and will gradually backfill language-stamped vocabulary through normal use. Do not relax the strict language filter to include NULL-language rows without a separate product decision and multi-tenant/data-quality review.

#### Source notes merged

##### jayne-quickadd-e2e.md

# Jayne quick-add E2E finding

Date: 2026-06-26
Surface: local Blazor WebApp via Aspire (`https://localhost:7071`) using `squad-jayne@sentencestudio.test`.

## Result
PASS for Resource details quick-add vocabulary flow on target resource `Jayne QuickAdd Target 20260626-2113` (`b44b34cb-4086-4e39-8e00-2bd64d770994`).

## Evidence
- Existing Korean word quick-add: attached `열쇠-1782074474` and `보다`; input cleared and refocused after add.
- Exclusion: after `보다` was attached, searching `보다` offered `눈여겨보다` and `사용해보다`, not the attached exact `보다` row.
- Inline create: created `새제인단어` with translation `new Jayne word`; DB shows `Language=Korean` and mapping exists.
- Bulk import controls were collapsed by default and visible after toggle.
- Remove: removed `보다`; DB shows all `보다` rows are `not_mapped` to target resource after removal.
- Browser console had 0 errors; Aspire structured logs search for `Exception` returned 0 entries.

## Note
The old bulk import flow on an edit resource seeded `검증단어`, `테스터말`, and `분리단어`, but those words had `Language=NULL` in `VocabularyWord`, so the strict Korean quick-add correctly did not return them. Used existing Korean-stamped words for strict-language quick-add verification instead.

##### kaylee-quickadd-ui.md

# Kaylee quick-add UI decisions

Date: 2026-06-26
Surface: Blazor WebApp ResourceEdit page

## Decisions

- Quick-add search uses a `CancellationTokenSource` debounce with a 250 ms delay before calling `LearningResourceRepository.SearchVocabularyWordsForResourceAsync`.
- Bulk import uses a bound `showBulkImport` Razor toggle rather than Bootstrap JavaScript collapse so the page stays Blazor-state-driven and avoids new JS dependencies.
- The quick-add dropdown uses minimal local CSS (`resource-quick-add-dropdown`, `resource-quick-add-spinner`) and otherwise reuses `card-ss`, `form-control-ss`, Bootstrap list-group, and button classes.

## Rationale

The CTS debounce keeps the repository lookup responsive while cancelling stale searches as Captain types. The Razor toggle keeps existing import handlers and state intact while making the bulky import UI collapsed by default. Minimal CSS keeps the Resource details page decluttered without creating a new component or style system.
