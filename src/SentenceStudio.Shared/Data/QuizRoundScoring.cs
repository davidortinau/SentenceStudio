using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

/// <summary>
/// Rule 6 of docs/activity-session-format.md, shared by every client: a quiz run whose summary
/// another client saved is completed only when one of the round's answers was scored for the
/// learner since the run started. The scoring client's learning records sync with the run.
/// </summary>
public static class QuizRoundScoring
{
    public const string Activity = "VocabularyQuiz";

    /// <summary>Clients stamp learning records with their own clocks; allow for drift against the run's start.</summary>
    public static readonly TimeSpan ClientClockDrift = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when a quiz answer on one of <paramref name="roundWordIds"/> was scored for
    /// <paramref name="userId"/> since <paramref name="runStartedAt"/> (less the clock drift).
    /// No user or no words means no scored answer.
    /// </summary>
    public static Task<bool> WasScoredSinceAsync(
        ApplicationDbContext db, string userId, IEnumerable<string> roundWordIds,
        DateTime runStartedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var words = roundWordIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (string.IsNullOrWhiteSpace(userId) || words.Length == 0)
            return Task.FromResult(false);

        var since = AsUtc(runStartedAt) - ClientClockDrift;
        return db.VocabularyLearningContexts.AsNoTracking()
            .Where(context => context.Activity == Activity && context.LearnedAt >= since)
            .Join(db.VocabularyProgresses.AsNoTracking()
                    .Where(progress => progress.UserId == userId
                        && words.Contains(progress.VocabularyWordId)),
                context => context.VocabularyProgressId, progress => progress.Id,
                (context, _) => context.Id)
            .AnyAsync(cancellationToken);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value
    };
}
