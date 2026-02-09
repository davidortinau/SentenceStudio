# VocabularyManagementPage Performance Optimization — After-Action Report

**Date:** 2026-02-09  
**Commit:** 044bffd  
**Dataset:** 1,854 vocabulary words, 30 learning resources, 1,660 progress records

## Summary

Reduced VocabularyManagementPage load time by **70%** (1,968ms → 589ms) through database query optimization and UI element reduction. All changes are backward-compatible with no data loss.

## Baseline Measurement

| Metric | Before |
|--------|--------|
| Total LoadData | 1,968ms |
| Parallel DB queries | 1,934ms |
| ViewModel creation | 24ms |
| ApplyFilters | 1ms |
| SetState | 9ms |

**Root cause:** 98% of time spent in database queries. The progress query loaded full navigation property chains (VocabularyWord → LearningContexts → LearningResource) when only `IsKnown`/`IsLearning` booleans were needed. Stats query used an inefficient subquery pattern. No `AsNoTracking()` on read-only queries.

## Changes Made (Phase 1)

### T1: AsNoTracking on Read-Only Queries
- `LearningResourceRepository.GetAllVocabularyWordsWithResourcesAsync()` — added `.AsNoTracking()`
- `VocabularyProgressRepository.GetAllForUserAsync()` — added `.AsNoTracking()`
- **Impact:** Eliminates EF change tracking overhead for 1,854+ entities

### T2: Remove Unnecessary Includes
- `VocabularyProgressRepository.GetAllForUserAsync()` — removed `.Include(VocabularyWord).ThenInclude(LearningContexts).ThenInclude(LearningResource)`
- Only `IsKnown`/`IsLearning` are used from progress records; navigation properties were wasted work
- **Impact:** Largest single improvement — eliminated loading ~5,500 related entities

### T3: Optimized Stats Query
- `LearningResourceRepository.GetVocabularyStatsAsync()` — changed from `VocabularyWords.Where(Any(mapping))` subquery to `ResourceVocabularyMappings.Select(VocabWordId).Distinct().Count()`
- **Impact:** Simpler, more efficient query plan

### T4: Database Indexes
- Added via raw SQL (`CREATE INDEX IF NOT EXISTS`) in `UserProfileRepository.GetAsync()`:
  - `IX_VocabularyWord_TargetLanguageTerm`
  - `IX_VocabularyWord_NativeLanguageTerm`
  - `IX_ResourceVocabularyMapping_VocabularyWordId`
- Applied as idempotent raw SQL rather than EF model changes to avoid `PendingModelChangesWarning`
- **Impact:** Faster filtering and joins on vocabulary queries

### T5: UI Element Reduction
- Desktop card: removed VStack wrapper when not in multi-select mode (ternary returns content directly)
- Mobile card: combined native term + status into single interpolated Label (`"term · status"`)
- Desktop view mode: combined resource count + status into single Label (already done earlier)
- **Impact:** ~2 fewer visual elements per card × 1,854 cards = ~3,700 fewer elements

## After Measurement

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| Total LoadData | 1,968ms | 589ms | **70% faster** |
| Parallel DB queries | 1,934ms | 565ms | **71% faster** |
| ViewModel creation | 24ms | 1ms | **96% faster** |
| ApplyFilters | 1ms | 0ms | — |

## Lessons Learned

1. **EF Include chains are expensive** — always check if navigation properties are actually used downstream. Loading 3 levels of Includes when only 2 boolean properties were needed was the #1 bottleneck.

2. **AsNoTracking matters at scale** — for 1,854+ entities with progress records, change tracking overhead is measurable.

3. **Don't put indexes in OnModelCreating without a migration** — EF Core 10 throws `PendingModelChangesWarning` as an error. Raw SQL `CREATE INDEX IF NOT EXISTS` is idempotent and migration-free.

4. **Measure first** — the existing timing logs in LoadData/LoadVocabularyData were invaluable for identifying the bottleneck was 98% DB, not UI.

## Future Opportunities (Not Implemented)

- **T4 Projection query:** Replace 3 separate queries + Include with a single projection query joining words, progress, and mappings. Could reduce to ~200ms.
- **T5 Pagination/Incremental loading:** Load first ~50 items immediately, then load rest in background. Would improve perceived performance to near-instant.
- **Virtualized CollectionView:** Currently renders all 1,854 cards. CollectionView with DataTemplate would only render visible items (~24 on screen).

These are Phase 2/3 optimizations that can be pursued if further improvement is needed.

## Files Changed

| File | Change |
|------|--------|
| `src/SentenceStudio/Data/LearningResourceRepository.cs` | AsNoTracking, optimized stats query |
| `src/SentenceStudio/Data/VocabularyProgressRepository.cs` | AsNoTracking, removed 3 Includes |
| `src/SentenceStudio/Data/UserProfileRepository.cs` | Raw SQL indexes on startup |
| `src/SentenceStudio/Pages/VocabularyManagement/VocabularyManagementPage.cs` | Reduced card elements |
| `src/SentenceStudio.Shared/Data/ApplicationDbContext.cs` | Removed model-based indexes (moved to raw SQL) |
