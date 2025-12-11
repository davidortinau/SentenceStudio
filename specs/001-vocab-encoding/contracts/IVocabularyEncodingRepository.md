# Contract: IVocabularyEncodingRepository

**Purpose**: Repository interface for vocabulary encoding operations with optimized SQLite queries

```csharp
namespace SentenceStudio.Data;

/// <summary>
/// Repository for vocabulary encoding features (tags, mnemonics, lemmas, filtering)
/// PERFORMANCE: All methods use compiled queries and proper indexing for mobile optimization
/// </summary>
public interface IVocabularyEncodingRepository
{
    /// <summary>
    /// Filter vocabulary words by tag using compiled query
    /// </summary>
    /// <param name="tag">Tag to filter by (e.g., "nature")</param>
    /// <param name="pageNumber">Page number (1-indexed)</param>
    /// <param name="pageSize">Number of results per page</param>
    /// <returns>Filtered and paginated vocabulary words</returns>
    /// <performance>Target: &lt;50ms for 5000 words with index</performance>
    Task<List<VocabularyWord>> FilterByTagAsync(string tag, int pageNumber = 1, int pageSize = 50);

    /// <summary>
    /// Get vocabulary words with encoding strength calculated
    /// Uses batch loading to avoid N+1 queries for example sentence counts
    /// </summary>
    /// <param name="pageNumber">Page number (1-indexed)</param>
    /// <param name="pageSize">Number of results per page</param>
    /// <param name="tagFilter">Optional tag filter</param>
    /// <param name="sortByEncodingStrength">Sort by encoding strength (weakest first)</param>
    /// <returns>Vocabulary words with EncodingStrength and EncodingStrengthLabel populated</returns>
    /// <performance>Target: &lt;100ms for 50 words with counts</performance>
    Task<List<VocabularyWord>> GetWithEncodingStrengthAsync(
        int pageNumber = 1,
        int pageSize = 50,
        string? tagFilter = null,
        bool sortByEncodingStrength = false);

    /// <summary>
    /// Search vocabulary by lemma (dictionary form)
    /// </summary>
    /// <param name="lemma">Lemma to search for (e.g., "가다")</param>
    /// <returns>All vocabulary words with matching lemma</returns>
    /// <performance>Target: &lt;30ms with index on Lemma</performance>
    Task<List<VocabularyWord>> SearchByLemmaAsync(string lemma);

    /// <summary>
    /// Get all unique tags used across vocabulary words
    /// </summary>
    /// <returns>Distinct tags for UI filtering/autocomplete</returns>
    /// <performance>Target: &lt;100ms for 5000 words</performance>
    Task<List<string>> GetAllTagsAsync();

    /// <summary>
    /// Update encoding metadata for a vocabulary word
    /// </summary>
    /// <param name="wordId">Vocabulary word ID</param>
    /// <param name="lemma">Dictionary form</param>
    /// <param name="tags">Comma-separated tags (max 10)</param>
    /// <param name="mnemonicText">Memory association</param>
    /// <param name="mnemonicImageUri">Optional image URL</param>
    /// <param name="audioPronunciationUri">Optional audio URL</param>
    /// <returns>Updated vocabulary word</returns>
    Task<VocabularyWord> UpdateEncodingMetadataAsync(
        int wordId,
        string? lemma,
        string? tags,
        string? mnemonicText,
        string? mnemonicImageUri,
        string? audioPronunciationUri);
}
```

## Implementation Notes

**Compiled Queries**: Use `EF.CompileAsyncQuery` for `FilterByTagAsync` (hot path query).

**Batch Loading**: `GetWithEncodingStrengthAsync` must load example sentence counts in single batch query to avoid N+1.

**Tag Parsing**: `GetAllTagsAsync` splits comma-separated tags in-memory after loading (no string parsing in SQL).

**Pagination**: All list methods support pagination to avoid loading 1000+ records.

**Indexes Required**: Tags, Lemma (see data-model.md for index definitions).
