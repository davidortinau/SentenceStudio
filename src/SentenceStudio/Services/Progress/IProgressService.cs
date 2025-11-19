using System.Collections.ObjectModel;

namespace SentenceStudio.Services.Progress;

// Lightweight DTOs for dashboard progress visuals
public record ResourceProgress
(
    int ResourceId,
    string Title,
    double Proficiency, // 0..1
    DateTime LastActivityUtc,
    int Attempts,
    double CorrectRate, // 0..1
    int Minutes
);

public record SkillProgress
(
    int SkillId,
    string Title,
    double Proficiency, // 0..1
    double Delta7d,     // -1..+1
    DateTime LastActivityUtc
);

public record VocabProgressSummary
(
    int New,
    int Learning,
    int Review,
    int Known,
    double SuccessRate7d // 0..1
);

public record PracticeHeatPoint(DateTime Date, int Count);

public enum PlanActivityType
{
    VocabularyReview,
    Reading,
    Listening,
    VideoWatching,
    Shadowing,
    Cloze,
    Translation,
    Conversation,
    VocabularyGame
}

public record DailyPlanItem
(
    string Id,
    string TitleKey,
    string DescriptionKey,
    PlanActivityType ActivityType,
    int EstimatedMinutes,
    int Priority,
    bool IsCompleted,
    DateTime? CompletedAt,
    string Route,
    Dictionary<string, object>? RouteParameters,
    int? ResourceId,
    string? ResourceTitle,
    int? SkillId,
    string? SkillName,
    int? VocabDueCount,
    string? DifficultyLevel,
    int MinutesSpent = 0  // Track actual time spent (can be in-progress or completed)
);

public record TodaysPlan
(
    DateTime GeneratedForDate,
    List<DailyPlanItem> Items,
    int EstimatedTotalMinutes,
    int CompletedCount,
    int TotalCount,
    double CompletionPercentage,
    StreakInfo Streak,
    string? ResourceTitles = null,
    string? SkillTitle = null
);

public record StreakInfo
(
    int CurrentStreak,
    int LongestStreak,
    DateTime? LastPracticeDate
);

public interface IProgressService
{
    Task<List<ResourceProgress>> GetRecentResourceProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default);
    Task<List<SkillProgress>> GetRecentSkillProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default);
    Task<SkillProgress?> GetSkillProgressAsync(int skillId, CancellationToken ct = default);
    Task<VocabProgressSummary> GetVocabSummaryAsync(DateTime fromUtc, CancellationToken ct = default);
    Task<IReadOnlyList<PracticeHeatPoint>> GetPracticeHeatAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    Task<TodaysPlan> GenerateTodaysPlanAsync(CancellationToken ct = default);
    Task<TodaysPlan?> GetCachedPlanAsync(DateTime date, CancellationToken ct = default);
    Task MarkPlanItemCompleteAsync(string planItemId, int minutesSpent, CancellationToken ct = default);
    Task UpdatePlanItemProgressAsync(string planItemId, int minutesSpent, CancellationToken ct = default);
}
