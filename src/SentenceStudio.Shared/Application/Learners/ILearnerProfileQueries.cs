using SentenceStudio.Shared.Models;

namespace SentenceStudio.Application.Learners;

/// <summary>
/// The learner settings an agent, a screen, or a report is allowed to read.
/// </summary>
/// <remarks>
/// A <see cref="UserProfile"/> row also carries the learner's name, their email address, and their
/// provider API keys. Those are not settings — they are credentials and identity, and nothing that
/// renders a learner's preferences has any business loading them. This record is the projection
/// the query issues, so those columns never leave the database in the first place rather than being
/// loaded and then carefully not used.
/// </remarks>
public sealed record LearnerProfileFacts(
    string TargetLanguage,
    string? TargetLanguages,
    string NativeLanguage,
    string? DisplayLanguage,
    int PreferredSessionMinutes,
    string? TargetCefrLevel,
    DateTime CreatedAt);

/// <summary>
/// Reads a learner's own settings, scoped to one learner and nothing else.
/// </summary>
public interface ILearnerProfileQueries
{
    /// <summary>
    /// Returns the settings for <paramref name="userProfileId"/>, or <c>null</c> when the learner
    /// has no profile row. An empty identifier returns <c>null</c> rather than falling through to
    /// an unfiltered read.
    /// </summary>
    Task<LearnerProfileFacts?> GetProfileFactsAsync(
        string userProfileId,
        CancellationToken cancellationToken = default);
}
