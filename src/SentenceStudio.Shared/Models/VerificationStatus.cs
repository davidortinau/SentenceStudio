namespace SentenceStudio.Shared.Models;

/// <summary>
/// The learner's mark on a word (docs/known-words-plan.md), stored as its number in
/// <c>VocabularyProgress.VerificationState</c>. The name is from the retired trust-but-verify
/// flow; the column and the first four numbers are kept so no row is rewritten. A mark never
/// changes the practice score, and the learner's <see cref="LearningMode"/> decides whether Known
/// marks are read (<see cref="SentenceStudio.Services.VocabularyKnownRule"/>).
/// </summary>
public enum VerificationStatus
{
    /// <summary>No mark.</summary>
    None = 0,
    /// <summary>The old "Familiar" declaration: read as <see cref="Confirmed"/>.</summary>
    Pending = 1,
    /// <summary>Marked Known by the learner.</summary>
    Confirmed = 2,
    /// <summary>The old failed probe: read as no mark.</summary>
    Demoted = 3,
    /// <summary>
    /// Was Known in practice while Learner-led, so it stays Known. Stamped by live scoring, never by
    /// the learner: on an unmarked row it carries no time, so any learner action is newer.
    /// </summary>
    KnownByPractice = 4,
    /// <summary>Practice again at <c>UserDeclaredAt</c>: the score was reset to typing level.</summary>
    Reopened = 5,
    /// <summary>Start over at <c>UserDeclaredAt</c>: the score was reset to zero.</summary>
    StartedOver = 6,
}
