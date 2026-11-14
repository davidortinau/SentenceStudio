using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Tools.ProgressCountRepair;

/// <summary>A progress row whose counts miss answers its history records, and the row rebuilt from them.</summary>
public sealed record RepairChange(VocabularyProgress Before, VocabularyProgress After, int ActiveAnswers);

public static class RepairPlanner
{
    /// <summary>The columns <see cref="VocabularyProgressFold.FoldInto"/> may change, as the database names them.</summary>
    public static readonly string[] RepairedColumns =
    [
        "TotalAttempts", "CorrectAttempts", "CurrentStreak", "ProductionInStreak", "MasteryScore",
        "ReviewInterval", "EaseFactor", "NextReviewDate", "MasteredAt", "ExposureCount", "LastExposedAt",
        "QuizRecognitionDemonstrations", "QuizProductionDemonstrations", "LastPracticedAt", "FirstSeenAt",
        "CreatedAt", "IsUserDeclared", "UserDeclaredAt", "VerificationState", "UpdatedAt",
    ];

    /// <summary>
    /// For one owner: every progress row whose history records more answers (active contexts) than
    /// it counts, folded as duplicate merge folds a word into itself: its contexts replayed in order,
    /// then reconciled so counts, dates, declarations and mastery never go below the stored row.
    /// Other users' rows, and rows whose counts already cover their answers, are left alone.
    /// </summary>
    public static IReadOnlyList<RepairChange> Plan(string owner,
        IEnumerable<VocabularyProgress> progress, IEnumerable<VocabularyLearningContext> contexts,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("An owner is required.", nameof(owner));
        var history = contexts
            .GroupBy(context => context.VocabularyProgressId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var changes = new List<RepairChange>();
        foreach (var row in progress)
        {
            if (!string.Equals(row.UserId, owner, StringComparison.Ordinal)
                || !history.TryGetValue(row.Id, out var answers))
                continue;
            var active = answers.Count(context => !VocabularyMasteryCalculator.IsPassive(context));
            if (active <= row.TotalAttempts)
                continue;
            var repaired = Copy(row);
            VocabularyProgressFold.FoldInto(repaired, [row], answers);
            // Mastery set outside an answer (confirming a known word, a declaration, a merge) is not
            // in the history, so a replay can rate a word below its stored row (1.0 after one counted
            // answer replays to 0.15). The repair restores lost answers; it keeps the stored mastery,
            // streak and review schedule when they are higher.
            if (repaired.MasteryScore < row.MasteryScore)
            {
                repaired.MasteryScore = row.MasteryScore;
                repaired.CurrentStreak = Math.Max(repaired.CurrentStreak, row.CurrentStreak);
                repaired.ProductionInStreak = Math.Max(repaired.ProductionInStreak, row.ProductionInStreak);
                repaired.NextReviewDate = row.NextReviewDate;
                repaired.ReviewInterval = row.ReviewInterval;
                repaired.EaseFactor = row.EaseFactor;
            }
            repaired.UpdatedAt = now;
            changes.Add(new RepairChange(row, repaired, active));
        }
        return changes;
    }

    /// <summary>The <see cref="RepairedColumns"/> whose values differ between the two rows.</summary>
    public static IReadOnlyList<string> ChangedColumns(VocabularyProgress before, VocabularyProgress after) =>
        RepairedColumns.Where(column => !Equals(Value(before, column), Value(after, column))).ToArray();

    public static object? Value(VocabularyProgress row, string column) =>
        typeof(VocabularyProgress).GetProperty(column)!.GetValue(row);

    private static VocabularyProgress Copy(VocabularyProgress row)
    {
        var copy = new VocabularyProgress();
        foreach (var property in typeof(VocabularyProgress).GetProperties())
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(copy, property.GetValue(row));
        return copy;
    }
}
