using SentenceStudio.Shared.Models;

namespace SentenceStudio.Application.Vocabulary;

/// <summary>
/// The scheduling and scoring facts for one word the learner is tracking, with no term attached.
/// </summary>
/// <remarks>
/// A summary of due volume must be able to say how many words are due, how they band, and how
/// often the learner lapses — without naming a single word. Keeping the terms out of this record
/// is what makes that guarantee structural: there is no term here to leak.
/// </remarks>
public sealed record VocabularyProgressFacts(
    float MasteryScore,
    int ProductionInStreak,
    int TotalAttempts,
    int CorrectAttempts,
    bool IsUserDeclared,
    VerificationStatus VerificationState,
    DateTime? NextReviewDate);

/// <summary>One word the learner is tracking, as much of it as a list needs.</summary>
public sealed record VocabularyWordSummary(
    string WordId,
    string? TargetLanguageTerm,
    string? NativeLanguageTerm,
    string? Lemma,
    string? Language,
    string? Tags,
    float MasteryScore,
    DateTime? LastPracticedAt,
    bool IsUserDeclared);

/// <summary>One word the learner is tracking, with the attempt history a detail view needs.</summary>
public sealed record VocabularyWordDetailFacts(
    string WordId,
    string? TargetLanguageTerm,
    string? NativeLanguageTerm,
    string? Lemma,
    string? Language,
    string? Tags,
    float MasteryScore,
    DateTime? LastPracticedAt,
    int TotalAttempts,
    int CorrectAttempts,
    bool IsUserDeclared);

/// <summary>A page of words, with the size of the set it was drawn from.</summary>
/// <param name="MatchedCount">
/// How many of the learner's tracked words matched, <em>before</em> the due embargo was applied.
/// A caller that reports only <paramref name="TotalCount"/> cannot tell "you have ten words" from
/// "you have fourteen, four of which I am not allowed to show you", and the second sentence is the
/// true one.
/// </param>
/// <param name="TotalCount">
/// How many matched and were eligible to be shown — that is, matched and were not due.
/// </param>
/// <param name="Words">The page drawn from the eligible set.</param>
public sealed record VocabularyWordPage(
    int MatchedCount,
    int TotalCount,
    IReadOnlyList<VocabularyWordSummary> Words);

/// <summary>
/// Reads the vocabulary a learner is tracking.
/// </summary>
/// <remarks>
/// <para>
/// Ownership of a word runs through <c>VocabularyProgress</c>, not through the word row. A
/// <c>VocabularyWord</c> is shared content; the progress row is what makes it this learner's. So
/// every query here starts from progress and joins outward, which is also why a word the learner
/// has never encountered cannot be read by guessing its identifier.
/// </para>
/// <para>
/// <see cref="SearchUndueWordsAsync"/> excludes words that are currently due for review, and the
/// exclusion applies to the count as well as the page. A due word's term is the answer to a review
/// the learner has not taken yet; a bulk list that included it would hand over the answers through
/// the side door.
/// </para>
/// </remarks>
public interface IVocabularyQueries
{
    /// <summary>Counts the words the learner is tracking. An empty identifier counts zero.</summary>
    Task<int> CountTrackedWordsAsync(string userProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the scheduling and scoring facts for every word the learner is tracking, with no
    /// terms attached. An empty identifier returns nothing.
    /// </summary>
    Task<IReadOnlyList<VocabularyProgressFacts>> GetProgressFactsAsync(
        string userProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the raw tag strings of the words that are due at or before
    /// <paramref name="asOfUtc"/> — one entry per due word, unsplit and uncounted, so the caller
    /// decides how to categorise. An empty identifier returns nothing.
    /// </summary>
    Task<IReadOnlyList<string?>> GetDueWordTagsAsync(
        string userProfileId,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the learner's words that are <em>not</em> currently due, highest mastery first,
    /// capped at <paramref name="maxResults"/>, optionally filtered by a substring match over the
    /// target term, the native term, and the lemma. The returned page carries both the count that
    /// matched before the due embargo and the count that survived it, so a caller can say how many
    /// words it is not showing without ever naming one. An empty identifier returns an empty page.
    /// </summary>
    Task<VocabularyWordPage> SearchUndueWordsAsync(
        string userProfileId,
        string? query,
        int maxResults,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one word the learner is tracking, or <c>null</c> when they are not tracking it. An
    /// empty identifier returns <c>null</c>.
    /// </summary>
    Task<VocabularyWordDetailFacts?> GetTrackedWordAsync(
        string userProfileId,
        string wordId,
        CancellationToken cancellationToken = default);
}
