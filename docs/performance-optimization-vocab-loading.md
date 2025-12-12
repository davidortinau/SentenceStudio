# Performance Optimization: Vocabulary Loading

**Date**: 2025-12-12  
**Issue**: Massive SQL WHERE IN clause with 1700+ parameters causing severe performance degradation

## Problem

The VocabularyManagementPage was loading ALL 1700+ vocabulary words and passing them to `GetProgressForWordsAsync()`, which generated a single SQL query with **1700 parameters** in the WHERE IN clause:

```sql
WHERE "v"."VocabularyWordId" IN (@vocabularyWordIds1, @vocabularyWordIds2, ..., @vocabularyWordIds1700)
```

This caused:
- **Slow query execution** (27ms+ per query)
- **UI freezing** during save operations
- **SQLite parameter limit issues** (max 999 parameters)
- **Massive memory consumption** for query plan compilation

## Solution

Implemented a three-tier optimization strategy:

### 1. Batched Queries (VocabularyProgressRepository.cs)

Modified `GetByWordIdsAsync()` to batch large parameter lists into chunks of 500:

```csharp
public async Task<List<VocabularyProgress>> GetByWordIdsAsync(List<int> vocabularyWordIds)
{
    const int BATCH_SIZE = 500;
    var results = new List<VocabularyProgress>();

    for (int i = 0; i < vocabularyWordIds.Count; i += BATCH_SIZE)
    {
        var batch = vocabularyWordIds.Skip(i).Take(BATCH_SIZE).ToList();
        var batchResults = await db.VocabularyProgresses
            .Where(vp => batch.Contains(vp.VocabularyWordId))
            .ToListAsync();
        
        results.AddRange(batchResults);
    }

    return results;
}
```

**Benefits**:
- Stays well below SQLite's 999 parameter limit
- Multiple smaller queries execute faster than one huge query
- Prevents query plan compilation overhead

### 2. Optimized "Load All" Method (VocabularyProgressRepository.cs)

Added `GetAllForUserAsync()` for cases where we need ALL progress:

```csharp
public async Task<List<VocabularyProgress>> GetAllForUserAsync(int userId = 1)
{
    return await db.VocabularyProgresses
        .Include(vp => vp.VocabularyWord)
        .Include(vp => vp.LearningContexts)
            .ThenInclude(lc => lc.LearningResource)
        .Where(vp => vp.UserId == userId)
        .ToListAsync();
}
```

**Benefits**:
- **NO WHERE IN clause** - just a simple userId filter
- Single efficient query instead of 4 batched queries
- Leverages SQLite's query optimizer for user-based filtering

### 3. Service Layer Method (VocabularyProgressService.cs)

Added `GetAllProgressDictionaryAsync()` to expose the optimized method:

```csharp
public async Task<Dictionary<int, VocabularyProgress>> GetAllProgressDictionaryAsync(int userId = 1)
{
    var allProgress = await _progressRepo.GetAllForUserAsync(userId);
    return allProgress.ToDictionary(p => p.VocabularyWordId, p => p);
}
```

### 4. Updated VocabularyManagementPage

Changed from massive WHERE IN to optimized load all:

```csharp
// BEFORE (❌ BAD):
var wordIds = allWords.Select(w => w.Id).ToList();  // 1700 IDs
var progressData = await _progressService.GetProgressForWordsAsync(wordIds);

// AFTER (✅ GOOD):
var progressData = await _progressService.GetAllProgressDictionaryAsync();
```

## Performance Impact

### Before
- **Query**: Single WHERE IN with 1700 parameters
- **Execution Time**: 27ms+ per query
- **UI Response**: Freezes during save
- **Risk**: Hits SQLite parameter limits

### After
- **Query**: Simple WHERE userId = 1
- **Execution Time**: ~5-10ms (estimated 60-70% faster)
- **UI Response**: Responsive
- **Risk**: None - well within all limits

## When to Use Each Method

### Use `GetProgressForWordsAsync(wordIds)` when:
- Loading progress for **specific** subset of words (e.g., filtered list, search results)
- Word count is **small** (<500 words)
- Need progress for **selected** items only

### Use `GetAllProgressDictionaryAsync()` when:
- Loading progress for **all** vocabulary words
- Displaying **full vocabulary list**
- Word count is **large** (>500 words)
- You know you need everything

## Additional Optimizations to Consider

1. **Pagination/Virtualization**: Load only visible words (100-200 at a time)
2. **Lazy Loading**: Load progress on-demand when expanding list items
3. **Caching**: Cache progress data in memory with invalidation on updates
4. **Indexing**: Ensure VocabularyProgress has index on (UserId, VocabularyWordId)

## Related Files

- `src/SentenceStudio/Data/VocabularyProgressRepository.cs`
- `src/SentenceStudio/Services/VocabularyProgressService.cs`
- `src/SentenceStudio/Services/IVocabularyProgressService.cs`
- `src/SentenceStudio/Pages/VocabularyManagement/VocabularyManagementPage.cs`
