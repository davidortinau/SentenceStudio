using SentenceStudio.Models;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Minimal <see cref="IProgressService"/> for rendering the plan canvas.
/// </summary>
/// <remarks>
/// The canvas calls exactly one member, <see cref="GetCachedPlanAsync"/>, to join resource titles
/// onto plan items. Everything else throws rather than returning a plausible empty value: a test
/// that starts depending on another member should fail loudly instead of silently rendering
/// against invented data.
/// </remarks>
internal sealed class StubProgressService : IProgressService
{
    /// <summary>The plan the canvas will see. Null means the learner has no cached plan.</summary>
    public TodaysPlan? CachedPlan { get; set; }

    public Task<TodaysPlan?> GetCachedPlanAsync(DateTime? date = null, CancellationToken ct = default) =>
        Task.FromResult(CachedPlan);

    private static NotSupportedException Unused([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new($"{member} is not used by the components under test.");

    public Task<List<ResourceProgress>> GetRecentResourceProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default) => throw Unused();
    public Task<List<SkillProgress>> GetRecentSkillProgressAsync(DateTime fromUtc, int max = 3, CancellationToken ct = default) => throw Unused();
    public Task<SkillProgress?> GetSkillProgressAsync(string skillId, CancellationToken ct = default) => throw Unused();
    public Task<VocabProgressSummary> GetVocabSummaryAsync(DateTime fromUtc, CancellationToken ct = default) => throw Unused();
    public Task<IReadOnlyList<PracticeHeatPoint>> GetPracticeHeatAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => throw Unused();
    public Task<TodaysPlan> GenerateTodaysPlanAsync(CancellationToken ct = default) => throw Unused();
    public Task ClearCachedPlanAsync(DateTime? date = null, CancellationToken ct = default) => throw Unused();
    public Task MarkPlanItemCompleteAsync(string planItemId, int minutesSpent, CancellationToken ct = default) => throw Unused();
    public Task UpdatePlanItemProgressAsync(string planItemId, int minutesSpent, CancellationToken ct = default) => throw Unused();
    public Task<ValidatedPlanItemProgress?> ValidatePlanItemAsync(string userId, string planItemId, PlanActivityType activityType, string? resourceId, string? skillId, IReadOnlyCollection<string>? vocabularyWordIds, CancellationToken ct = default) => throw Unused();
    public Task<bool> UpdatePlanItemProgressAsync(string userId, string planItemId, int minutesSpent, CancellationToken ct = default) => throw Unused();
    public Task<string> StartAdHocSessionAsync(PlanActivityType activityType, string? resourceId, string? skillId, int estimatedMinutes = 10, CancellationToken ct = default) => throw Unused();
    public Task<string> StartAdHocSessionAsync(PlanActivityType activityType, string? resourceId, string? skillId, IReadOnlyCollection<string>? vocabularyWordIds, int estimatedMinutes = 10, CancellationToken ct = default) => throw Unused();
    public Task<string> StartAdHocSessionAsync(string userId, PlanActivityType activityType, string? resourceId, string? skillId, IReadOnlyCollection<string>? vocabularyWordIds, int estimatedMinutes = 10, CancellationToken ct = default) => throw Unused();
    public Task<bool> DiscardAdHocSessionAsync(string userId, string planItemId, CancellationToken ct = default) => throw Unused();
    public Task<List<ActivityLogWeek>> GetActivityLogAsync(DateTime fromUtc, DateTime toUtc, ActivityCategory? category = null, CancellationToken ct = default) => throw Unused();
}
