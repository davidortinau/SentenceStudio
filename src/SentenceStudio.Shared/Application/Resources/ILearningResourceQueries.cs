namespace SentenceStudio.Application.Resources;

/// <summary>
/// A learning resource described by its metadata only.
/// </summary>
/// <remarks>
/// <see cref="HasTranscript"/> is deliberately a boolean rather than the transcript. A resource's
/// transcript and translation are the largest columns on the row and the ones a language model
/// must never be handed, so the presence flag is computed in SQL and the text is never selected.
/// Loading the entity and reading <c>Transcript != null</c> in memory would answer the same
/// question having already pulled every word of it across the wire.
/// </remarks>
public sealed record LearningResourceSummary(
    string ResourceId,
    string? Title,
    string? MediaType,
    string? Language,
    string? MediaUrl,
    bool HasTranscript,
    string? Tags,
    bool IsSmartResource,
    int VocabularyCount);

/// <summary>
/// Reads a learner's learning resources as metadata, never as content.
/// </summary>
public interface ILearningResourceQueries
{
    /// <summary>Counts the learner's resources. An empty identifier counts zero.</summary>
    Task<int> CountResourcesAsync(string userProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every resource the learner owns, unordered. Callers that rank by something the
    /// database cannot see — days since last use, for instance — need the whole set before they
    /// can pick a page from it. An empty identifier returns nothing.
    /// </summary>
    Task<IReadOnlyList<LearningResourceSummary>> GetResourceSummariesAsync(
        string userProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the learner's resources, most recently updated first, capped at
    /// <paramref name="maxResults"/>. An empty identifier returns nothing.
    /// </summary>
    Task<IReadOnlyList<LearningResourceSummary>> GetRecentResourceSummariesAsync(
        string userProfileId,
        int maxResults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one resource the learner owns, or <c>null</c> when it is missing or belongs to
    /// someone else. An empty identifier returns <c>null</c>.
    /// </summary>
    Task<LearningResourceSummary?> GetResourceSummaryAsync(
        string userProfileId,
        string resourceId,
        CancellationToken cancellationToken = default);
}
