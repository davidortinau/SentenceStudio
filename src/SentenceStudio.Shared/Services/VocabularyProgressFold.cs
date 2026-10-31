using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Folds several progress rows for one (user, word) into a keeper as if every attempt had
/// always belonged to it: replays all contexts, then reconciles monotonically so counts,
/// demonstrations, dates, and explicit declarations never regress. Shared by duplicate-word
/// merge and native sync's divergent-progress reconciliation.
/// </summary>
public static class VocabularyProgressFold
{
    /// <param name="keeper">The surviving row; mutated. It may also appear in <paramref name="sources"/>.</param>
    /// <param name="sources">Every pre-merge row for the user (keeper included when it existed).</param>
    /// <param name="contexts">Every learning context of those rows.</param>
    public static void FoldInto(
        VocabularyProgress keeper,
        IReadOnlyCollection<VocabularyProgress> sources,
        IReadOnlyCollection<VocabularyLearningContext> contexts)
    {
        // Pre-merge aggregates — used to reconcile so counts/dates never regress, even for ancient
        // history that predates per-attempt context tracking (where replay alone would undercount).
        int summedTotal = sources.Sum(p => p.TotalAttempts);
        int summedCorrect = sources.Sum(p => p.CorrectAttempts);
        int summedExposure = sources.Sum(p => p.ExposureCount);
        int summedQuizRecognitionDemonstrations = sources.Sum(p => p.QuizRecognitionDemonstrations);
        int summedQuizProductionDemonstrations = sources.Sum(p => p.QuizProductionDemonstrations);
        // Best mastery/streak across sources — used only to prevent regression when source history
        // predates per-attempt context tracking (so replay cannot fully reconstruct it).
        float maxSourceMastery = sources.Max(p => p.MasteryScore);
        float maxSourceStreak = sources.Max(p => p.CurrentStreak);
        int maxSourceProductionInStreak = sources.Max(p => p.ProductionInStreak);
        DateTime minFirstSeen = sources.Min(p => p.FirstSeenAt);
        DateTime minCreated = sources.Min(p => p.CreatedAt);
        DateTime maxLastPracticed = sources.Max(p => p.LastPracticedAt);
        DateTime? maxLastExposed = sources
            .Where(p => p.LastExposedAt.HasValue)
            .Select(p => (DateTime?)p.LastExposedAt!.Value)
            .Max();
        DateTime? earliestMastered = sources
            .Where(p => p.MasteredAt.HasValue)
            .Select(p => (DateTime?)p.MasteredAt!.Value)
            .Min();
        var declared = sources
            .Where(p => p.IsUserDeclared)
            .OrderByDescending(p => p.UserDeclaredAt ?? DateTime.MinValue)
            .FirstOrDefault();
        // Most-recently-practiced source's spaced-repetition schedule. Used only to restore the SR
        // cadence when replay cannot reconstruct it (aggregate-only legacy history with no contexts),
        // so a merged word still resurfaces for review on time instead of going dormant.
        var scheduleSource = sources
            .Where(p => p.NextReviewDate.HasValue)
            .OrderByDescending(p => p.LastPracticedAt)
            .FirstOrDefault()
            ?? sources.OrderByDescending(p => p.LastPracticedAt).First();

        // Recalculate mastery as if all of this user's contexts had always belonged to the keeper.
        VocabularyMasteryCalculator.RecalculateInto(keeper, contexts, out _);

        // Number of active attempts the replay actually reconstructed (= keeper.TotalAttempts before
        // count reconciliation). When this is less than the summed aggregate, source history predates
        // per-attempt context tracking and replay is incomplete.
        int replayedActiveAttempts = keeper.TotalAttempts;
        bool hasPreContextHistory = summedTotal > replayedActiveAttempts;

        // Reconcile so the merge is monotonic — never lose attempts, exposures, or historical dates.
        keeper.TotalAttempts = Math.Max(keeper.TotalAttempts, summedTotal);
        keeper.CorrectAttempts = Math.Min(keeper.TotalAttempts, Math.Max(keeper.CorrectAttempts, summedCorrect));
        keeper.ExposureCount = Math.Max(keeper.ExposureCount, summedExposure);
        keeper.QuizRecognitionDemonstrations = Math.Max(keeper.QuizRecognitionDemonstrations, summedQuizRecognitionDemonstrations);
        keeper.QuizProductionDemonstrations = Math.Max(keeper.QuizProductionDemonstrations, summedQuizProductionDemonstrations);

        // For fully context-backed (modern) data the replay is exact and authoritative, so the wrong
        // answers it replays are allowed to lower mastery — that's the faithful "as if one word" result.
        // Only when there's a pre-context gap do we protect against regressing below the best source,
        // otherwise ancient words (aggregate-only, no contexts) would be wrongly reset toward Unknown.
        if (hasPreContextHistory)
        {
            keeper.MasteryScore = Math.Max(keeper.MasteryScore, maxSourceMastery);
            keeper.CurrentStreak = Math.Max(keeper.CurrentStreak, maxSourceStreak);
            keeper.ProductionInStreak = Math.Max(keeper.ProductionInStreak, maxSourceProductionInStreak);
        }

        // Replay resets the SR schedule to defaults (NextReviewDate=null, Interval=1, Ease=2.5). When it
        // reconstructed at least one active attempt, that replayed schedule reflects the most recent
        // context-backed attempt and is authoritative — leave it. When replay produced no active attempts
        // (aggregate-only legacy data, or a user-declared "Familiar" duplicate that was never quizzed) the
        // replayed schedule is still the default, so carry a real source schedule forward — otherwise the
        // merged word would have NextReviewDate=null and never become due (IsDueForReview) again.
        if (replayedActiveAttempts == 0 && scheduleSource.NextReviewDate.HasValue)
        {
            keeper.NextReviewDate = scheduleSource.NextReviewDate;
            keeper.ReviewInterval = scheduleSource.ReviewInterval;
            keeper.EaseFactor = scheduleSource.EaseFactor;
        }

        keeper.FirstSeenAt = minFirstSeen;
        keeper.CreatedAt = minCreated;
        if (maxLastPracticed > keeper.LastPracticedAt)
            keeper.LastPracticedAt = maxLastPracticed;
        if (maxLastExposed.HasValue && (!keeper.LastExposedAt.HasValue || maxLastExposed > keeper.LastExposedAt))
            keeper.LastExposedAt = maxLastExposed;
        // MasteredAt is a historical "first reached mastery" fact; keep the earliest known date.
        if (earliestMastered.HasValue && (!keeper.MasteredAt.HasValue || earliestMastered < keeper.MasteredAt))
            keeper.MasteredAt = earliestMastered;
        // Carry an explicit user declaration ("I know this word") — strong signal we must not drop.
        if (declared != null)
        {
            keeper.IsUserDeclared = true;
            keeper.UserDeclaredAt = declared.UserDeclaredAt;
            keeper.VerificationState = declared.VerificationState;
        }
        keeper.UpdatedAt = DateTime.Now;
    }
}
