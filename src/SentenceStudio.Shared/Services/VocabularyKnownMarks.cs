using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// The score reset a Practice again or Start over mark made at <paramref name="At"/>. A replay of
/// the word's history applies it at that instant, with the numbers the live action used, so the
/// replay and the live path agree.
/// </summary>
/// <param name="FromZero">Start over (score zero); otherwise Practice again (typing level).</param>
public readonly record struct VocabularyScoreReset(DateTime At, bool FromZero);

/// <summary>
/// What the learner's actions and live practice write to a word's mark
/// (docs/known-words-plan.md). Pure row changes; the caller saves them and proves the word is the
/// learner's own. A mark never changes how the score is calculated.
/// </summary>
public static class VocabularyKnownMarks
{
    /// <summary>
    /// Where Practice again restarts a word: the quiz's typing threshold
    /// (<see cref="VocabularyQuizItem.ChooseInteractionMode"/>), so it is asked by typing first and a
    /// miss drops it to multiple choice.
    /// </summary>
    public const float PracticeAgainMastery = 0.5f;

    /// <summary>Mark Known: the mark only. The score keeps what practice earned.</summary>
    public static void MarkKnown(VocabularyProgress progress, DateTime at)
    {
        progress.VerificationState = VerificationStatus.Confirmed;
        progress.IsUserDeclared = true;
        progress.UserDeclaredAt = at;
        progress.UpdatedAt = at;
    }

    /// <summary>
    /// Practice again: the word is Learning at typing level and due now; it grows back to Known
    /// through practice. History and counts are kept.
    /// </summary>
    public static void PracticeAgain(VocabularyProgress progress, DateTime at) =>
        Reopen(progress, VerificationStatus.Reopened, new VocabularyScoreReset(at, FromZero: false));

    /// <summary>Start over: as Practice again, from a score of zero.</summary>
    public static void StartOver(VocabularyProgress progress, DateTime at) =>
        Reopen(progress, VerificationStatus.StartedOver, new VocabularyScoreReset(at, FromZero: true));

    private static void Reopen(VocabularyProgress progress, VerificationStatus mark, VocabularyScoreReset reset)
    {
        progress.VerificationState = mark;
        progress.IsUserDeclared = true;
        progress.UserDeclaredAt = reset.At;
        ApplyReset(progress, reset);
        progress.UpdatedAt = reset.At;
    }

    /// <summary>The reset a row's mark made, or null when its mark is not a reset.</summary>
    public static VocabularyScoreReset? ResetOf(VocabularyProgress progress) =>
        VocabularyKnownRule.IsResetMark(progress.VerificationState) && progress.UserDeclaredAt is { } at
            ? new VocabularyScoreReset(at, progress.VerificationState == VerificationStatus.StartedOver)
            : null;

    /// <summary>
    /// The numbers of a reset, the same live and in a replay: streaks to zero, the schedule back to
    /// its start, and the score to zero (Start over) or typing level, due at once (Practice again).
    /// </summary>
    public static void ApplyReset(VocabularyProgress progress, VocabularyScoreReset reset)
    {
        progress.MasteryScore = reset.FromZero ? 0f : PracticeAgainMastery;
        progress.CurrentStreak = 0f;
        progress.ProductionInStreak = 0;
        progress.ReviewInterval = 1;
        progress.EaseFactor = 2.5f;
        progress.NextReviewDate = reset.FromZero ? null : reset.At;
    }

    /// <summary>
    /// After a live answer in Learner-led: a word that was Known before the answer, or is Known by
    /// score after it, and has no Known mark, is stamped Known by practice, so it stays Known.
    /// Stamped on an unmarked row the stamp carries no time (any learner action is newer in a
    /// merge); stamped over Practice again or Start over, the word grew back, and the stamp takes
    /// the answer's time. In Practice-led nothing is stamped. Replays and merges never call this.
    /// </summary>
    /// <param name="wasKnown">The Known rule for the row before the answer was scored.</param>
    public static void AfterLiveAttempt(VocabularyProgress progress, bool wasKnown,
        LearningMode mode, DateTime scoredAt)
    {
        if (mode != LearningMode.LearnerLed
            || VocabularyKnownRule.IsKnownMark(progress.VerificationState)
            || (!wasKnown && !VocabularyKnownRule.IsKnownByScore(progress)))
            return;
        var grewBack = VocabularyKnownRule.IsResetMark(progress.VerificationState);
        progress.VerificationState = VerificationStatus.KnownByPractice;
        if (grewBack)
            progress.UserDeclaredAt = scoredAt;
    }
}
