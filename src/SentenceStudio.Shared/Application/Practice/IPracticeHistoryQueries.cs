namespace SentenceStudio.Application.Practice;

/// <summary>What one logged plan item says about how a learner spent a day.</summary>
public sealed record PracticeCompletionFacts(
    string? ActivityType,
    int MinutesSpent,
    bool IsCompleted,
    DateTime Date);

/// <summary>One item on a day's plan, as the plan itself describes it.</summary>
public sealed record PlanItemFacts(
    string? ActivityType,
    bool IsCompleted,
    int EstimatedMinutes,
    int MinutesSpent);

/// <summary>The generation metadata for a day's plan.</summary>
public sealed record DailyPlanFacts(string? Strategy);

/// <summary>
/// Reads what a learner has actually done: the plans generated for them, the items logged against
/// those plans, and the activity attempts recorded outside them.
/// </summary>
/// <remarks>
/// <para>
/// This is the read side of <c>DailyPlan</c>, <c>DailyPlanCompletion</c>, and <c>UserActivity</c>.
/// It exists because those three tables had no owner that a multi-tenant host could call. The
/// writes live in <c>IPlanService</c>, and the app's own progress reporting lives in
/// <c>ProgressService</c> — but <c>ProgressService</c> resolves the learner from a device
/// preference and memoises per-user results in a process-wide cache, both of which are correct on
/// a single-learner device and wrong on a server that serves everyone. Rather than reach for it
/// and inherit that shape, the aggregation a server can safely run lives here, where the learner
/// is always an argument and nothing is remembered between calls.
/// </para>
/// <para>
/// Every method fails closed: an empty learner identifier reads nothing and returns the empty
/// answer for its type, never an unfiltered query.
/// </para>
/// </remarks>
public interface IPracticeHistoryQueries
{
    /// <summary>
    /// Returns the learner's logged plan items with a date in
    /// <c>[startUtcInclusive, endUtcExclusive)</c>.
    /// </summary>
    Task<IReadOnlyList<PracticeCompletionFacts>> GetCompletionsInRangeAsync(
        string userProfileId,
        DateTime startUtcInclusive,
        DateTime endUtcExclusive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the learner's recorded activity attempts created in
    /// <c>[startUtcInclusive, endUtcExclusive)</c>.
    /// </summary>
    Task<int> CountActivityAttemptsAsync(
        string userProfileId,
        DateTime startUtcInclusive,
        DateTime endUtcExclusive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns, for each resource the learner has ever practised against, the date of the most
    /// recent session. Resources never practised are simply absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetResourceLastUsedAsync(
        string userProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the date the learner last practised against one resource, or <c>null</c> when they
    /// never have.
    /// </summary>
    Task<DateTime?> GetResourceLastUsedAsync(
        string userProfileId,
        string resourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the plan generated for the learner on <paramref name="planDateUtc"/> — the UTC
    /// instant that opens their local day — or <c>null</c> when no plan exists for it.
    /// </summary>
    /// <remarks>
    /// At most one plan exists per learner per date: the store holds a uniqueness constraint on
    /// that pair, so the "most recently generated" ordering inside the implementation is a
    /// defensive tiebreak over a set that cannot contain two rows. It is kept because the cost is
    /// nil and a future relaxation of the constraint would otherwise silently make this method
    /// non-deterministic; it is documented because a reader who assumes several plans a day are
    /// normal will write a caller that handles a case the schema forbids.
    /// </remarks>
    Task<DailyPlanFacts?> GetPlanForDateAsync(
        string userProfileId,
        DateTime planDateUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the learner's logged items for one plan date.</summary>
    Task<IReadOnlyList<PlanItemFacts>> GetPlanItemsForDateAsync(
        string userProfileId,
        DateTime planDateUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the UTC timestamp of the learner's most recent recorded practice — the latest
    /// across plan-item completions and free-form activity attempts. Returns <c>null</c> when the
    /// learner has never practised.
    /// </summary>
    Task<DateTime?> GetLastPracticeUtcAsync(
        string userProfileId,
        CancellationToken cancellationToken = default);
}
