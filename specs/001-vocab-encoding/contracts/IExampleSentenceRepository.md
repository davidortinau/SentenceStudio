# Contract: IExampleSentenceRepository

**Purpose**: Repository interface for example sentence CRUD operations

```csharp
namespace SentenceStudio.Data;

/// <summary>
/// Repository for example sentence operations (create, read, update, delete)
/// </summary>
public interface IExampleSentenceRepository
{
    /// <summary>
    /// Get all example sentences for a vocabulary word
    /// </summary>
    /// <param name="vocabularyWordId">Vocabulary word ID</param>
    /// <returns>Example sentences ordered by CreatedAt</returns>
    /// <performance>Target: &lt;30ms with index on VocabularyWordId</performance>
    Task<List<ExampleSentence>> GetByVocabularyWordIdAsync(int vocabularyWordId);

    /// <summary>
    /// Get core example sentences for a vocabulary word
    /// </summary>
    /// <param name="vocabularyWordId">Vocabulary word ID</param>
    /// <returns>Core example sentences only</returns>
    /// <performance>Target: &lt;20ms with composite index (VocabularyWordId, IsCore)</performance>
    Task<List<ExampleSentence>> GetCoreExamplesAsync(int vocabularyWordId);

    /// <summary>
    /// Get example sentence counts for multiple vocabulary words (batch query)
    /// PERFORMANCE: Avoids N+1 by loading counts in single query
    /// </summary>
    /// <param name="vocabularyWordIds">List of vocabulary word IDs</param>
    /// <returns>Dictionary mapping vocabulary word ID to example sentence count</returns>
    /// <performance>Target: &lt;50ms for 100 words</performance>
    Task<Dictionary<int, int>> GetCountsByVocabularyWordIdsAsync(List<int> vocabularyWordIds);

    /// <summary>
    /// Create a new example sentence
    /// </summary>
    /// <param name="sentence">Example sentence to create</param>
    /// <returns>Created example sentence with ID populated</returns>
    /// <performance>Target: &lt;10ms (standard EF write)</performance>
    Task<ExampleSentence> CreateAsync(ExampleSentence sentence);

    /// <summary>
    /// Update an existing example sentence
    /// </summary>
    /// <param name="sentence">Example sentence to update</param>
    /// <returns>Updated example sentence</returns>
    Task<ExampleSentence> UpdateAsync(ExampleSentence sentence);

    /// <summary>
    /// Delete an example sentence
    /// </summary>
    /// <param name="sentenceId">Example sentence ID</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteAsync(int sentenceId);

    /// <summary>
    /// Toggle core example flag
    /// </summary>
    /// <param name="sentenceId">Example sentence ID</param>
    /// <param name="isCore">New core flag value</param>
    /// <returns>Updated example sentence</returns>
    Task<ExampleSentence> SetCoreAsync(int sentenceId, bool isCore);
}
```

## Implementation Notes

**Batch Counts**: `GetCountsByVocabularyWordIdsAsync` is critical for performance. Must use:
```csharp
var counts = await db.ExampleSentences
    .Where(es => vocabularyWordIds.Contains(es.VocabularyWordId))
    .GroupBy(es => es.VocabularyWordId)
    .Select(g => new { VocabularyWordId = g.Key, Count = g.Count() })
    .ToDictionaryAsync(x => x.VocabularyWordId, x => x.Count);
```

**Cascade Delete**: Deleting a VocabularyWord cascades to ExampleSentences (configured in EF model).

**Core Examples**: Composite index on (VocabularyWordId, IsCore) optimizes core example queries.

**Audio Generation**: Audio URIs are populated by separate audio service (not repository responsibility).
