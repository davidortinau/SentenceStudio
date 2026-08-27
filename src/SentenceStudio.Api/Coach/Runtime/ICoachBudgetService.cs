using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Runtime;

/// <summary>
/// Token and cost accounting for one coach run.
/// </summary>
/// <param name="InputTokens">Prompt tokens consumed by the run.</param>
/// <param name="OutputTokens">Completion tokens produced by the run.</param>
/// <param name="EstimatedCostUsd">Estimated cost in USD, computed by the caller from model pricing.</param>
public readonly record struct CoachRunUsage(long InputTokens, long OutputTokens, decimal EstimatedCostUsd)
{
    /// <summary>A zero-usage record, for runs that stopped before any model call.</summary>
    public static CoachRunUsage None => new(0, 0, 0m);

    /// <summary>Adds two usage records.</summary>
    public CoachRunUsage Add(CoachRunUsage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        EstimatedCostUsd + other.EstimatedCostUsd);
}

/// <summary>
/// Accumulated counters for one budget period (a user-local day or ISO week).
/// Mirrors the columns planned for the PostgreSQL <c>CoachUsage</c> row.
/// </summary>
/// <param name="Runs">Runs started in the period. A cancelled run still counts.</param>
/// <param name="InputTokens">Prompt tokens recorded in the period.</param>
/// <param name="OutputTokens">Completion tokens recorded in the period.</param>
/// <param name="EstimatedCostUsd">Estimated cost recorded in the period.</param>
public readonly record struct CoachUsageCounters(
    int Runs,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCostUsd)
{
    /// <summary>An empty period.</summary>
    public static CoachUsageCounters Empty => new(0, 0, 0, 0m);
}

/// <summary>
/// A point-in-time view of one learner's coach budget.
/// </summary>
public sealed record CoachBudgetSnapshot
{
    /// <summary>The user-local day this snapshot describes.</summary>
    public required DateOnly DayKey { get; init; }

    /// <summary>The user-local ISO week key, in <c>yyyy-Www</c> form.</summary>
    public required string WeekKey { get; init; }

    /// <summary>Counters for <see cref="DayKey"/>.</summary>
    public required CoachUsageCounters Day { get; init; }

    /// <summary>Counters for <see cref="WeekKey"/>.</summary>
    public required CoachUsageCounters Week { get; init; }

    /// <summary>The configured daily run cap in force when the snapshot was taken.</summary>
    public required int MaxRunsPerDay { get; init; }

    /// <summary>The configured weekly run cap in force when the snapshot was taken.</summary>
    public required int MaxRunsPerWeek { get; init; }

    /// <summary>True when a run for this learner is currently in flight.</summary>
    public required bool HasActiveRun { get; init; }

    /// <summary>Runs left today, floored at zero.</summary>
    public int RunsRemainingToday => Math.Max(0, MaxRunsPerDay - Day.Runs);

    /// <summary>Runs left this week, floored at zero.</summary>
    public int RunsRemainingThisWeek => Math.Max(0, MaxRunsPerWeek - Week.Runs);
}

/// <summary>
/// A held concurrency slot for one in-flight coach run.
/// </summary>
/// <remarks>
/// The slot is released when the lease is disposed, whichever way the run ends: success, failure,
/// timeout, or cancellation. Always dispose the lease — an <c>await using</c> is the intended usage.
/// </remarks>
public interface ICoachRunLease : IAsyncDisposable
{
    /// <summary>The server-generated id for this run. Safe to use as a correlation value.</summary>
    Guid RunId { get; }

    /// <summary>True once the concurrency slot has been released.</summary>
    bool IsReleased { get; }

    /// <summary>
    /// Adds token and cost usage to the learner's day and week counters. May be called more than
    /// once for a run; values accumulate. Calling after release is a no-op so a late completion
    /// callback cannot corrupt a new period.
    /// </summary>
    ValueTask RecordUsageAsync(CoachRunUsage usage, CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a run-slot request.
/// </summary>
public sealed record CoachRunLeaseResult
{
    /// <summary>True when a slot was granted and <see cref="Lease"/> is non-null.</summary>
    public required bool Acquired { get; init; }

    /// <summary>
    /// The typed stop reason when the request was denied — always
    /// <see cref="CoachStopReason.ConcurrencyLimit"/> or <see cref="CoachStopReason.RateLimit"/>.
    /// Null when <see cref="Acquired"/> is true.
    /// </summary>
    public CoachStopReason? DeniedReason { get; init; }

    /// <summary>The granted lease, or null when denied.</summary>
    public ICoachRunLease? Lease { get; init; }

    /// <summary>The budget as it stands after the request was evaluated.</summary>
    public required CoachBudgetSnapshot Snapshot { get; init; }
}

/// <summary>
/// Enforces per-learner coach budgets: one concurrent run, a daily run cap, a weekly run cap, and
/// token/cost accounting.
/// </summary>
/// <remarks>
/// <para>
/// The interface is deliberately period-key driven rather than clock driven. Callers pass the
/// learner's local date (the API already resolves one per request through <c>IPlanDateContext</c>),
/// so a store-backed implementation can map straight onto the planned PostgreSQL <c>CoachUsage</c>
/// row keyed by user profile id plus day and ISO-week keys. Nothing in this contract assumes the
/// state lives in process.
/// </para>
/// <para>
/// Every operation is asynchronous and cancellable for the same reason: the Stage 1 implementation
/// completes synchronously, but a database-backed one will not.
/// </para>
/// </remarks>
public interface ICoachBudgetService
{
    /// <summary>
    /// Reads the learner's current budget without reserving anything.
    /// </summary>
    ValueTask<CoachBudgetSnapshot> GetSnapshotAsync(
        string userProfileId,
        DateOnly userLocalDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to reserve the learner's single run slot and charge one run against the daily and
    /// weekly caps.
    /// </summary>
    /// <remarks>
    /// The run is charged at acquisition, not at completion, so repeated cancellation cannot be used
    /// to bypass the cap. The concurrency slot, by contrast, is released as soon as the lease is
    /// disposed.
    /// </remarks>
    ValueTask<CoachRunLeaseResult> TryStartRunAsync(
        string userProfileId,
        DateOnly userLocalDate,
        CancellationToken cancellationToken = default);
}
