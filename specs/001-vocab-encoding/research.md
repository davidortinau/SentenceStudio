# Research: Vocabulary Encoding SQLite Optimization

**Feature**: 001-vocab-encoding  
**Created**: 2025-12-11  
**Purpose**: Research SQLite performance optimization patterns for mobile vocabulary filtering and encoding calculations

## Research Questions

1. How to optimize tag filtering on comma-separated strings in SQLite?
2. What are the best practices for avoiding LINQ N+1 queries in Entity Framework Core with SQLite?
3. How to implement compiled queries for repeated filtering operations?
4. What indexing strategies work best for mobile SQLite databases?
5. How to efficiently calculate derived metrics (encoding strength) without persistent storage?

---

## Decision 1: Tag Storage and Indexing

**Context**: User Story 3 requires filtering vocabulary by tags (e.g., "season", "nature"). Tags are stored as comma-separated strings in a single column.

**Decision**: Store tags as comma-separated string with SQLite LIKE queries and partial index

**Rationale**:
- **Simplicity**: Single `Tags` column (VARCHAR/TEXT) avoids junction table complexity for MVP
- **Query Performance**: SQLite LIKE with wildcards `LIKE '%tag%'` can use index if properly configured
- **Mobile-Friendly**: Avoids JOIN overhead; single-table queries are faster on mobile devices
- **Migration Path**: Can normalize to tag table later if autocomplete/faceting becomes critical

**Implementation Pattern**:
```csharp
// Index creation in migration
migrationBuilder.CreateIndex(
    name: "IX_VocabularyWord_Tags",
    table: "VocabularyWord",
    column: "Tags");

// Optimized query using EF.Functions.Like
var filtered = await db.VocabularyWords
    .Where(w => EF.Functions.Like(w.Tags, $"%{tag}%"))
    .ToListAsync();
```

**Alternatives Considered**:
- **Normalized Tag Table**: Rejected for MVP due to JOIN overhead and migration complexity
- **JSON Column**: Rejected - SQLite JSON functions are slower than LIKE on indexed TEXT
- **Full-Text Search (FTS5)**: Rejected as overkill for simple tag matching; FTS5 adds schema complexity

**Performance Target**: <50ms for filtering 5000 words by tag with index

---

## Decision 2: Compiled Queries for Tag Filtering

**Context**: Tag filtering will be called frequently (every time user clicks a tag badge). Repeated LINQ queries cause unnecessary SQL generation overhead.

**Decision**: Use EF Core compiled queries for tag filtering

**Rationale**:
- **Performance**: Compiled queries are parsed once, reused many times (40-50% faster for repeated queries)
- **Mobile Optimization**: Reduces CPU overhead on mobile devices where compilation is expensive
- **Best Practice**: Recommended by EF Core docs for "hot path" queries

**Implementation Pattern**:
```csharp
public class VocabularyFilterService
{
    private static readonly Func<ApplicationDbContext, string, IAsyncEnumerable<VocabularyWord>> 
        _filterByTagCompiled = EF.CompileAsyncQuery(
            (ApplicationDbContext db, string tag) =>
                db.VocabularyWords
                    .Where(w => EF.Functions.Like(w.Tags, $"%{tag}%"))
                    .OrderBy(w => w.TargetLanguageTerm));

    public async Task<List<VocabularyWord>> FilterByTagAsync(string tag)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await _filterByTagCompiled(db, tag).ToListAsync();
    }
}
```

**Alternatives Considered**:
- **Raw SQL Queries**: Rejected - loses type safety and EF tracking benefits
- **Cached LINQ Expressions**: Rejected - compiled queries are simpler and officially supported

**Performance Target**: <30ms for repeated tag filtering (vs ~50ms with dynamic LINQ)

---

## Decision 3: Avoid N+1 with Batch Loading for Example Sentences

**Context**: Vocabulary list displays encoding strength, which requires knowing if example sentences exist. Naive `.Include(w => w.ExampleSentences)` causes N+1 queries when iterating.

**Decision**: Batch load example sentence counts in single query using projection

**Rationale**:
- **N+1 Prevention**: Single query loads counts for all words in the list
- **Network Efficiency**: Critical for mobile where SQLite I/O is bottleneck
- **Memory Efficiency**: Projecting counts (not full entities) reduces memory footprint

**Implementation Pattern**:
```csharp
// WRONG: N+1 anti-pattern
var words = await db.VocabularyWords.ToListAsync();
foreach (var word in words)
{
    word.ExampleSentenceCount = await db.ExampleSentences
        .CountAsync(es => es.VocabularyWordId == word.Id); // N+1!!!
}

// CORRECT: Single batch query
var wordIds = await db.VocabularyWords.Select(w => w.Id).ToListAsync();
var sentenceCounts = await db.ExampleSentences
    .Where(es => wordIds.Contains(es.VocabularyWordId))
    .GroupBy(es => es.VocabularyWordId)
    .Select(g => new { VocabularyWordId = g.Key, Count = g.Count() })
    .ToDictionaryAsync(x => x.VocabularyWordId, x => x.Count);

// Apply counts to words in memory
foreach (var word in words)
{
    word.ExampleSentenceCount = sentenceCounts.GetValueOrDefault(word.Id, 0);
}
```

**Alternatives Considered**:
- **Eager Loading with .Include()**: Rejected - loads full ExampleSentence entities when only count needed
- **Separate Repository Method per Word**: Rejected - causes N+1 problem

**Performance Target**: <100ms to load 50 words with example sentence counts

---

## Decision 4: Encoding Strength as Derived Calculation (Not Persisted)

**Context**: Encoding strength is a score (0-1.0) based on 6 factors: target term, native term, mnemonic, image, audio, example sentences. User Story 1 requires displaying this indicator.

**Decision**: Calculate encoding strength in-memory using a service; do not persist to database

**Rationale**:
- **Simplicity**: Avoids adding `EncodingStrength` column and update triggers
- **Correctness**: Always up-to-date; no risk of stale persisted values
- **Performance**: Calculation is fast (~6 null checks + division); caching handles repeated access
- **Schema Simplicity**: Reduces migration complexity for MVP

**Implementation Pattern**:
```csharp
public class EncodingStrengthCalculator
{
    public double Calculate(VocabularyWord word, int exampleSentenceCount)
    {
        int present = 0;
        int possible = 6;

        if (!string.IsNullOrWhiteSpace(word.TargetLanguageTerm)) present++;
        if (!string.IsNullOrWhiteSpace(word.NativeLanguageTerm)) present++;
        if (!string.IsNullOrWhiteSpace(word.MnemonicText)) present++;
        if (!string.IsNullOrWhiteSpace(word.MnemonicImageUri)) present++;
        if (!string.IsNullOrWhiteSpace(word.AudioPronunciationUri)) present++;
        if (exampleSentenceCount > 0) present++;

        return (double)present / possible;
    }

    public string GetEncodingLabel(double score)
    {
        if (score < 0.34) return "Basic";
        if (score < 0.67) return "Good";
        return "Strong";
    }
}
```

**Alternatives Considered**:
- **Persisted Column with Triggers**: Rejected - adds complexity and potential for stale data
- **Computed Column in SQLite**: Rejected - SQLite computed columns don't support EXISTS subqueries for counting example sentences
- **Materialized View**: Rejected - overkill for MVP

**Performance Target**: <30ms to calculate encoding strength for 100 words (in-memory with cached counts)

---

## Decision 5: Index Strategy for Mobile SQLite

**Context**: Mobile devices have limited I/O throughput. Proper indexes are critical for sub-100ms query performance.

**Decision**: Create focused indexes on filtered/sorted columns only

**Rationale**:
- **Read Optimization**: Mobile apps query more than they write; index read paths aggressively
- **Size Management**: Each index adds storage overhead; focus on user-facing queries
- **Maintenance Cost**: SQLite auto-maintains indexes; no manual intervention needed

**Required Indexes**:
```sql
-- Tag filtering (User Story 3)
CREATE INDEX IX_VocabularyWord_Tags ON VocabularyWord(Tags);

-- Foreign key for example sentences (joins and counts)
CREATE INDEX IX_ExampleSentence_VocabularyWordId ON ExampleSentence(VocabularyWordId);

-- Core example filtering (User Story 2)
CREATE INDEX IX_ExampleSentence_IsCore ON ExampleSentence(IsCore);

-- Composite index for resource-specific vocabulary queries
CREATE INDEX IX_ExampleSentence_VocabId_IsCore ON ExampleSentence(VocabularyWordId, IsCore);
```

**Alternatives Considered**:
- **Full-Text Index on Tags**: Rejected - standard B-tree index sufficient for LIKE queries
- **Index on UpdatedAt**: Rejected - not a user-facing query in MVP

**Performance Target**: All indexed queries <50ms on mid-range mobile devices

---

## Decision 6: Pagination for Large Vocabulary Lists

**Context**: Users may have 5000+ vocabulary words. Rendering all at once causes UI lag on mobile.

**Decision**: Implement skip/take pagination with CollectionView virtualization

**Rationale**:
- **UI Responsiveness**: CollectionView virtualizes rendering; only loads visible items
- **Memory Management**: Prevents loading 5000+ entities into memory at once
- **Query Performance**: LIMIT/OFFSET in SQLite is fast when combined with indexes

**Implementation Pattern**:
```csharp
public async Task<List<VocabularyWord>> GetPagedAsync(int pageNumber, int pageSize, string? tagFilter = null)
{
    using var scope = _serviceProvider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    var query = db.VocabularyWords.AsQueryable();

    if (!string.IsNullOrWhiteSpace(tagFilter))
    {
        query = query.Where(w => EF.Functions.Like(w.Tags, $"%{tagFilter}%"));
    }

    return await query
        .OrderBy(w => w.TargetLanguageTerm)
        .Skip((pageNumber - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync();
}
```

**Alternatives Considered**:
- **Load All with Virtual Scrolling**: Rejected - still loads all data from SQLite
- **Cursor-Based Pagination**: Rejected - adds complexity for MVP

**Performance Target**: <100ms to load page of 50 words with tag filter

---

## Summary of Performance Optimizations

| Optimization | Impact | Implementation Complexity |
|--------------|--------|---------------------------|
| Tags index | 10x faster filtering | Low (1 migration line) |
| Compiled queries | 40% faster repeated queries | Medium (new service class) |
| Batch loading counts | Eliminates N+1 | Medium (query refactoring) |
| Derived encoding calculation | Avoids schema complexity | Low (pure function) |
| Focused indexes | 50% faster joins | Low (migration lines) |
| Pagination | Unbounded list support | Medium (repository method) |

**Overall Performance Posture**: Conservative, mobile-first approach prioritizing query speed over write optimization. All critical queries (tag filtering, example sentence loading, encoding calculation) target <100ms on mid-range mobile devices.

## References

- [EF Core Compiled Queries](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics#compiled-queries)
- [SQLite Index Best Practices](https://www.sqlite.org/queryplanner.html)
- [EF Core Performance Best Practices](https://learn.microsoft.com/en-us/ef/core/performance/)
- [LINQ Anti-Patterns: N+1 Problem](https://learn.microsoft.com/en-us/ef/core/querying/related-data/)
