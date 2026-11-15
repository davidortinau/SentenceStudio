using System.Linq.Expressions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>Who decided that a word is Known (for the words that say so in the UI).</summary>
public enum VocabularyKnownSource
{
    /// <summary>The word is not Known.</summary>
    None,
    /// <summary>The learner marked it Known.</summary>
    Learner,
    /// <summary>It reached Known through practice.</summary>
    Practice,
}

/// <summary>
/// The one rule for "Known" (docs/known-words-plan.md, Captain 2026-11-14), the same on every
/// client and in every query. A word is Known when, in Learner-led, it carries a Known mark, or,
/// in either mode, its practice score meets the score rule. The mark, the score and the mode are
/// separate: no mark changes the score, and the mode is only read here, so switching it writes
/// nothing. Every caller passes the learner's own <see cref="LearningMode"/>.
/// </summary>
public static class VocabularyKnownRule
{
    public const float MasteryThreshold = VocabularyMasteryCalculator.MASTERY_THRESHOLD;
    public const int MinProductionForKnown = VocabularyMasteryCalculator.MIN_PRODUCTION_FOR_KNOWN;

    // High-confidence case: a slightly lower score backed by more typed answers.
    public const float HighConfidenceMasteryFloor = 0.75f;
    public const int HighConfidenceMinProduction = 4;
    public const int HighConfidenceMinAttempts = 8;

    /// <summary>A mark that makes a word Known in Learner-led.</summary>
    public static bool IsKnownMark(VerificationStatus mark) =>
        mark is VerificationStatus.Confirmed or VerificationStatus.Pending
            or VerificationStatus.KnownByPractice;

    /// <summary>The learner's own Mark Known (the old Familiar declaration reads the same).</summary>
    public static bool IsLearnerKnownMark(VerificationStatus mark) =>
        mark is VerificationStatus.Confirmed or VerificationStatus.Pending;

    /// <summary>Practice again or Start over: the score was reset when the mark was made.</summary>
    public static bool IsResetMark(VerificationStatus mark) =>
        mark is VerificationStatus.Reopened or VerificationStatus.StartedOver;

    /// <summary>Any mark the merge carries (the old failed probe reads as no mark).</summary>
    public static bool IsMark(VerificationStatus mark) => IsKnownMark(mark) || IsResetMark(mark);

    /// <summary>
    /// Which of two marks made at the same time a merge keeps: a learner's action (2), then the
    /// automatic stamp (1), then no mark (0: an old declaration that set none, or a failed probe).
    /// </summary>
    public static int MarkRank(VerificationStatus mark) =>
        IsLearnerKnownMark(mark) || IsResetMark(mark) ? 2 : IsKnownMark(mark) ? 1 : 0;

    /// <summary>Known by the practice score alone, in either mode.</summary>
    public static bool IsKnownByScore(float masteryScore, int productionInStreak, int totalAttempts) =>
        (masteryScore >= MasteryThreshold && productionInStreak >= MinProductionForKnown)
        || (masteryScore >= HighConfidenceMasteryFloor
            && productionInStreak >= HighConfidenceMinProduction
            && totalAttempts >= HighConfidenceMinAttempts);

    public static bool IsKnownByScore(VocabularyProgress progress) =>
        IsKnownByScore(progress.MasteryScore, progress.ProductionInStreak, progress.TotalAttempts);

    /// <summary>The rule, from the fields a projection holds.</summary>
    public static bool IsKnown(LearningMode mode, VerificationStatus mark, float masteryScore,
        int productionInStreak, int totalAttempts) =>
        (mode == LearningMode.LearnerLed && IsKnownMark(mark))
        || IsKnownByScore(masteryScore, productionInStreak, totalAttempts);

    /// <summary>The rule; a word with no progress row is not Known.</summary>
    public static bool IsKnown(VocabularyProgress? progress, LearningMode mode) =>
        progress is not null && IsKnown(mode, progress.VerificationState, progress.MasteryScore,
            progress.ProductionInStreak, progress.TotalAttempts);

    /// <summary>Practising: some score, not Known.</summary>
    public static bool IsLearning(VocabularyProgress? progress, LearningMode mode) =>
        progress is not null && progress.MasteryScore > 0 && !IsKnown(progress, mode);

    /// <summary>No score yet and not Known (a word marked Known with no practice is Known).</summary>
    public static bool IsUnknown(VocabularyProgress? progress, LearningMode mode) =>
        progress is null || (progress.MasteryScore == 0 && !IsKnown(progress, mode));

    /// <summary>Known, Learning or Unknown. Familiar is no longer produced.</summary>
    public static LearningStatus Status(VocabularyProgress? progress, LearningMode mode) =>
        IsKnown(progress, mode) ? LearningStatus.Known
            : progress is { MasteryScore: > 0 } ? LearningStatus.Learning
            : LearningStatus.Unknown;

    /// <summary>Who decided, for a Known word: the learner's mark first, then practice.</summary>
    public static VocabularyKnownSource Source(VocabularyProgress? progress, LearningMode mode) =>
        !IsKnown(progress, mode) ? VocabularyKnownSource.None
            : mode == LearningMode.LearnerLed && IsLearnerKnownMark(progress!.VerificationState)
                ? VocabularyKnownSource.Learner
                : VocabularyKnownSource.Practice;

    /// <summary>
    /// The words an activity builds its practice from: those that are not Known, so a Known word is
    /// never the one being tested. When every word is Known the whole list is used, so the
    /// activity still has sentences to offer (a Known word may be context).
    /// </summary>
    public static List<TWord> PracticePool<TWord>(IEnumerable<TWord> words,
        Func<TWord, VocabularyProgress?> progressOf, LearningMode mode)
    {
        var all = words.ToList();
        var open = all.Where(word => !IsKnown(progressOf(word), mode)).ToList();
        return open.Count > 0 ? open : all;
    }

    /// <summary>The rule as a query filter, for both databases.</summary>
    public static Expression<Func<VocabularyProgress, bool>> IsKnownExpression(LearningMode mode) =>
        mode == LearningMode.LearnerLed
            ? progress => progress.VerificationState == VerificationStatus.Confirmed
                || progress.VerificationState == VerificationStatus.Pending
                || progress.VerificationState == VerificationStatus.KnownByPractice
                || (progress.MasteryScore >= MasteryThreshold
                    && progress.ProductionInStreak >= MinProductionForKnown)
                || (progress.MasteryScore >= HighConfidenceMasteryFloor
                    && progress.ProductionInStreak >= HighConfidenceMinProduction
                    && progress.TotalAttempts >= HighConfidenceMinAttempts)
            : progress => (progress.MasteryScore >= MasteryThreshold
                    && progress.ProductionInStreak >= MinProductionForKnown)
                || (progress.MasteryScore >= HighConfidenceMasteryFloor
                    && progress.ProductionInStreak >= HighConfidenceMinProduction
                    && progress.TotalAttempts >= HighConfidenceMinAttempts);

    /// <summary>The opposite filter: the words practice may still choose.</summary>
    public static Expression<Func<VocabularyProgress, bool>> IsNotKnownExpression(LearningMode mode)
    {
        var known = IsKnownExpression(mode);
        return Expression.Lambda<Func<VocabularyProgress, bool>>(
            Expression.Not(known.Body), known.Parameters);
    }
}
