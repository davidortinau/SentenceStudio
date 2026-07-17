using System.Collections.ObjectModel;

namespace SentenceStudio.Services.Progress;

// Lightweight DTOs for dashboard progress visuals
public record ResourceProgress
(
    string ResourceId,
    string Title,
    double Proficiency, // 0..1
    DateTime LastActivityUtc,
    int Attempts,
    double CorrectRate, // 0..1
    int Minutes
);

public record SkillProgress
(
    string SkillId,
    string Title,
    double Proficiency, // 0..1
    double Delta7d,     // -1..+1
    DateTime LastActivityUtc
);

public record VocabProgressSummary
(
    int New,
    int Learning,
    int Familiar,
    int Review,
    int Known,
    double SuccessRate7d // 0..1
);

public record PracticeHeatPoint(DateTime Date, int Count);

public sealed record ValidatedPlanItemProgress(
    string PlanItemId,
    int MinutesSpent,
    int EstimatedMinutes);

public enum PlanActivityType
{
    VocabularyReview,
    Reading,
    Listening,
    VideoWatching,
    Shadowing,
    Cloze,
    Translation,
    Writing,
    SceneDescription,
    Conversation,
    VocabularyGame,
    NumberDrill
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
    string? ResourceId,
    string? ResourceTitle,
    string? SkillId,
    string? SkillName,
    int? VocabDueCount,
    string? DifficultyLevel,
    int MinutesSpent = 0,  // Track actual time spent (can be in-progress or completed)
    IReadOnlyList<string>? FocusVocabularyIds = null
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
    string? SkillTitle = null,
    string? Rationale = null,
    PlanNarrative? Narrative = null,
    IReadOnlyList<string>? FocusVocabularyIds = null
);

public record PlanNarrative(
    List<PlanResourceSummary> Resources,
    VocabInsight? VocabInsight,
    string Story,
    List<string> FocusAreas
);

public record PlanResourceSummary(
    string Id,
    string Title,
    string MediaType,
    string SelectionReason
);

public record VocabInsight(
    int TotalDue,
    int ReviewCount,      // Words the user has practiced before (TotalAttempts > 0)
    int NewCount,          // Words never practiced (TotalAttempts == 0)
    float AverageMastery,  // Average MasteryScore of due words
    List<TagInsight> StrugglingCategories, // Tags where accuracy is low
    List<string> SampleStrugglingWords,    // Example words (TargetLanguageTerm) for display
    string? PatternInsight,  // e.g. "You're having trouble with time-related vocabulary"
    List<PlanPreviewWord>? PreviewWords = null
);

public record PlanPreviewWord(
    string WordId,
    string TargetTerm,
    string NativeTerm
);

public record TagInsight(
    string Tag,
    int WordCount,
    float AverageAccuracy,
    int TotalAttempts = 0
);

public record StreakInfo
(
    int CurrentStreak,
    int LongestStreak,
    DateTime? LastPracticeDate
);

public enum ActivityCategory { Input, Output }

public record ActivityLogEntry(
    string PlanItemId,
    PlanActivityType ActivityType,
    ActivityCategory Category,
    int MinutesSpent,
    int EstimatedMinutes,
    bool IsCompleted,
    DateTime? CompletedAt,
    string? ResourceTitle,
    string? SkillName,
    string TitleKey,
    string DescriptionKey
);

public record ActivityLogPlan(
    DateTime GeneratedAt,
    List<ActivityLogEntry> Items,
    int CompletedCount,
    int TotalCount,
    bool IsFullyCompleted,
    int TotalMinutesSpent,
    int TotalEstimatedMinutes,
    string? Rationale,
    string? NarrativeJson
);

public record ActivityLogDay(
    DateTime Date,
    List<ActivityLogPlan> Plans,
    int TotalMinutes,
    bool HasInput,
    bool HasOutput,
    bool AllPlansCompleted
);

public record ActivityLogWeek(
    DateTime WeekStart,
    DateTime WeekEnd,
    ActivityLogDay[] Days,
    int TotalMinutes,
    int InputMinutes,
    int OutputMinutes,
    int ActivityCount,
    int PlansCompleted,
    int PlansTotal
);

public static class ActivityCategoryMapper
{
    public static ActivityCategory Categorize(PlanActivityType type) => type switch
    {
        PlanActivityType.Reading or
        PlanActivityType.Listening or
        PlanActivityType.VideoWatching or
        PlanActivityType.VocabularyReview or
        PlanActivityType.VocabularyGame or
        PlanActivityType.NumberDrill or
        PlanActivityType.Cloze => ActivityCategory.Input,
        _ => ActivityCategory.Output
    };
}

public interface IProgressService
{
    Task<List<ResourceProgress>> GetRecentResourceProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default);
    Task<List<SkillProgress>> GetRecentSkillProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default);
    Task<SkillProgress?> GetSkillProgressAsync(string skillId, CancellationToken ct = default);
    Task<VocabProgressSummary> GetVocabSummaryAsync(DateTime fromUtc, CancellationToken ct = default);
    Task<IReadOnlyList<PracticeHeatPoint>> GetPracticeHeatAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    Task<TodaysPlan> GenerateTodaysPlanAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns today's cached plan, hydrating from the database if needed. The optional
    /// <paramref name="date"/> override exists for tests; production callers should pass
    /// <c>null</c> so the service resolves "today" via <see cref="SentenceStudio.Services.Plans.IPlanDateContext"/>.
    /// Passing a raw <c>DateTime.UtcNow.Date</c> or <c>DateTime.Today</c> drifts across midnight UTC vs. the user's local day.
    /// </summary>
    Task<TodaysPlan?> GetCachedPlanAsync(DateTime? date = null, CancellationToken ct = default);

    /// <summary>
    /// Invalidates the cached plan and deletes today's <c>DailyPlanCompletion</c> rows so the next
    /// <see cref="GenerateTodaysPlanAsync"/> regenerates fresh. Optional <paramref name="date"/> override
    /// for tests; production callers should pass <c>null</c> (see <see cref="GetCachedPlanAsync"/>).
    /// </summary>
    Task ClearCachedPlanAsync(DateTime? date = null, CancellationToken ct = default);
    Task MarkPlanItemCompleteAsync(string planItemId, int minutesSpent, CancellationToken ct = default);
    Task UpdatePlanItemProgressAsync(string planItemId, int minutesSpent, CancellationToken ct = default);

    /// <summary>
    /// Validates that a current-plan item belongs to the explicit user and matches
    /// the complete launch context before any timer state or progress is mutated.
    /// </summary>
    Task<ValidatedPlanItemProgress?> ValidatePlanItemAsync(
        string userId,
        string planItemId,
        PlanActivityType expectedActivityType,
        string? resourceId,
        string? skillId,
        IReadOnlyCollection<string>? vocabularyWordIds,
        CancellationToken ct = default);

    /// <summary>
    /// Updates a current-plan item only for the explicit user. Returns false when
    /// the item is missing, stale, or belongs to another profile.
    /// </summary>
    Task<bool> UpdatePlanItemProgressAsync(
        string userId,
        string planItemId,
        int minutesSpent,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a new ad-hoc ("choose my own") activity completion record and returns its synthetic PlanItemId.
    /// Use the returned id with <see cref="UpdatePlanItemProgressAsync"/> / <see cref="IActivityTimerService"/>
    /// so unplanned practice sessions show up in the Activity Log alongside plan items.
    /// PlanItemIds produced here are prefixed with <c>adhoc-</c> and are filtered out of the dashboard's "today's plan".
    /// </summary>
    Task<string> StartAdHocSessionAsync(PlanActivityType activityType, string? resourceId, string? skillId, int estimatedMinutes = 10, CancellationToken ct = default);

    /// <summary>
    /// Creates an ad-hoc activity only when every supplied resource, skill, and vocabulary word
    /// belongs to or is reachable by the same active user. Invalid mixed-owner tuples are refused
    /// and return an empty plan item ID.
    /// </summary>
    Task<string> StartAdHocSessionAsync(
        PlanActivityType activityType,
        string? resourceId,
        string? skillId,
        IReadOnlyCollection<string>? vocabularyWordIds,
        int estimatedMinutes = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Creates an ad-hoc activity for the explicit user without consulting mutable
    /// active-profile preferences.
    /// </summary>
    Task<string> StartAdHocSessionAsync(
        string userId,
        PlanActivityType activityType,
        string? resourceId,
        string? skillId,
        IReadOnlyCollection<string>? vocabularyWordIds,
        int estimatedMinutes = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a zero-progress ad-hoc row created by a timer start that lost its
    /// generation race before the caller accepted the session.
    /// </summary>
    Task<bool> DiscardAdHocSessionAsync(
        string userId,
        string planItemId,
        CancellationToken ct = default);

    Task<List<ActivityLogWeek>> GetActivityLogAsync(DateTime fromUtc, DateTime toUtc, ActivityCategory? filter = null, CancellationToken ct = default);
}
