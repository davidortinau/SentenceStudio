# ListLearningResourcesPage Performance Optimization — After-Action Report

**Date:** 2026-02-09  
**Commit:** 3fb8395  
**Dataset:** 30 learning resources, 3,931 vocabulary mappings, 1,854 vocabulary words

## Summary

Reduced ListLearningResourcesPage load time by **99.5%** (952ms → 5ms) by eliminating an unnecessary `.Include(r => r.Vocabulary)` that eagerly loaded all vocabulary data for a page that only displays resource metadata.

## Baseline Measurement

From EF Core logs on navigation to ListLearningResourcesPage:
- **Data reader time: 952ms** reading results
- Thousands of `ChangeTracking` log entries for `VocabularyWord` and `ResourceVocabularyMapping`
- Root cause: `GetAllResourcesAsync()` uses `.Include(r => r.Vocabulary)` which joins through `ResourceVocabularyMapping` to load **3,931 mappings + 1,854 words** — none used by the list UI

**Properties actually used by `RenderResourceItem`:**
- `resource.Id`, `resource.Title`, `resource.MediaType`, `resource.Language`
- `resource.IsSmartResource`, `resource.CreatedAt`

## Changes Made

### T1: Switch to Lightweight Query (HIGH IMPACT)
- Changed `LoadResources()` to call `GetAllResourcesLightweightAsync()` instead of `GetAllResourcesAsync()`
- `GetAllResourcesLightweightAsync()` already existed in the repository but was unused by this page
- Added `AsNoTracking()` to the lightweight method
- **Impact:** Eliminated loading 5,785 unnecessary entities (3,931 mappings + 1,854 words)

### T2: AsNoTracking on Search Query
- Added `.AsNoTracking()` to `SearchResourcesAsync()` in `LearningResourceRepository.cs`
- Search results are read-only display — no change tracking needed

### T3: Timing Instrumentation
- Added `ILogger<ListLearningResourcesPage>` injection
- Added `Stopwatch` timing to `LoadResources()` logging total time and resource count

### T4: Push Filters to SQL
- Extended `GetAllResourcesLightweightAsync()` with optional `filterType` and `filterLanguages` parameters
- `LoadResources()` now passes `State.FilterType` and `State.FilterLanguages` directly to the query
- Eliminated redundant `GetAllResourcesAsync()` call in `FilterResources()` — now delegates to `LoadResources()`
- Existing callers use default parameter values, so no breaking changes

### T5: UI Elements Verified
- `RenderResourceItem` was already optimized in commit f3f8092 (previous session task)
- Uses Grid layout with 3 elements: icon, title label, combined metadata label, date label
- No further reduction needed

## After Measurement

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| LoadResources total | 952ms | **5ms** | **99.5% faster** |
| Entities loaded | 5,815 (30 + 3,931 + 1,854) | 30 | **99.5% fewer** |
| Change tracking entries | ~11,630 | 0 | Eliminated |

## Lessons Learned

1. **Audit Include chains against actual UI usage** — this page loaded 194× more entities than it displayed. A single `.Include()` can silently load thousands of related records.

2. **Lightweight query methods pay off** — `GetAllResourcesLightweightAsync()` already existed for dropdowns/selectors but the main list page wasn't using it. Always check if a lighter alternative exists.

3. **Default parameters preserve backward compatibility** — adding `filterType = null, filterLanguages = null` to the existing lightweight method let us push filters to SQL without breaking 5 other callers.

## Files Changed

| File | Change |
|------|--------|
| `src/SentenceStudio/Pages/LearningResources/ListLearningResourcesPage.cs` | Use lightweight query, add logger, simplify FilterResources |
| `src/SentenceStudio/Data/LearningResourceRepository.cs` | Add AsNoTracking + filter params to lightweight method, AsNoTracking on search |
