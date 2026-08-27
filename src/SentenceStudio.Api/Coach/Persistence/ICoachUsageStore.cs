namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>Daily and weekly coach usage totals for one learner.</summary>
/// <param name="RunCount">Completed coach runs in the window.</param>
/// <param name="InputTokens">Prompt tokens in the window.</param>
/// <param name="OutputTokens">Completion tokens in the window.</param>
/// <param name="EstimatedCostUsd">Estimated cost in the window.</param>
public sealed record CoachUsageTotals(int RunCount, long InputTokens, long OutputTokens, decimal EstimatedCostUsd)
{
    /// <summary>An empty window. Returned whenever the caller owns no usage rows.</summary>
    public static CoachUsageTotals Empty { get; } = new(0, 0, 0, 0m);

    /// <summary>Prompt plus completion tokens.</summary>
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Owned access to coach run/token/cost counters. Separate from
/// <see cref="ICoachSessionStore"/> because usage outlives any single session and is
/// read by rate limiting before a session exists.
/// </summary>
/// <remarks>
/// Same ownership contract as the session store: <c>userProfileId</c> comes from the
/// trusted caller, every query filters on it, and an empty id logs a warning and returns
/// zeroed totals rather than reading across tenants.
/// </remarks>
public interface ICoachUsageStore
{
    /// <summary>Adds one run's usage to the learner's counters for a learner-local date.</summary>
    Task<CoachUsage?> RecordRunAsync(
        string userProfileId,
        DateOnly localDate,
        long inputTokens,
        long outputTokens,
        decimal estimatedCostUsd,
        CancellationToken cancellationToken = default);

    /// <summary>Totals for one learner-local date.</summary>
    Task<CoachUsageTotals> GetDailyTotalsAsync(string userProfileId, DateOnly localDate, CancellationToken cancellationToken = default);

    /// <summary>Totals for the ISO week containing the supplied learner-local date.</summary>
    Task<CoachUsageTotals> GetWeeklyTotalsAsync(string userProfileId, DateOnly localDate, CancellationToken cancellationToken = default);
}
