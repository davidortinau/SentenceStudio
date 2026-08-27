using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Runtime;

/// <summary>
/// Stage 1, single-instance <see cref="ICoachBudgetService"/> backed by a process-local dictionary.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This implementation is not distributed-safe and must not be presented as one.</strong>
/// Counters and the one-run-per-learner slot live in this process only. Two API replicas each
/// grant their own slot and each keep their own run counts, so the effective cap is
/// <c>replicas × configured cap</c>. It is fit for the internal single-instance dogfood in Stage 1
/// and nothing beyond it.
/// </para>
/// <para>
/// The interface it implements is the part meant to survive: it is keyed by user profile id plus a
/// user-local day and ISO-week key, which maps directly onto the planned PostgreSQL
/// <c>CoachUsage</c> row. Replacing this class with a store-backed one — atomic
/// <c>INSERT ... ON CONFLICT DO UPDATE</c> for the counters and a row lock or advisory lock for the
/// run slot — requires no change at the call sites.
/// </para>
/// <para>
/// Dictionary keys are SHA-256 hashes of the profile id. That is a defence-in-depth measure for
/// process dumps and debugger inspection only; it is not authentication and it is not anonymity.
/// Hashed or not, these values are never used as telemetry tags.
/// </para>
/// </remarks>
public sealed class InMemoryCoachBudgetService : ICoachBudgetService
{
    /// <summary>
    /// Grace period added to the configured run timeout before an in-flight run slot is treated as
    /// abandoned and reclaimed. Without this, a crashed run would lock a learner out until restart.
    /// </summary>
    public static readonly TimeSpan AbandonedRunGrace = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, LearnerBudget> _budgets = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<CoachOptions> _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the Stage 1 budget service.</summary>
    /// <param name="options">Live coach options, so a cap change applies without a restart.</param>
    /// <param name="timeProvider">Clock used for abandoned-run reclaim. Injected so tests control it.</param>
    public InMemoryCoachBudgetService(IOptionsMonitor<CoachOptions> options, TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public ValueTask<CoachBudgetSnapshot> GetSnapshotAsync(
        string userProfileId,
        DateOnly userLocalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileId);
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options.CurrentValue;
        var budget = GetOrCreate(userProfileId);

        lock (budget.Gate)
        {
            RollPeriods(budget, userLocalDate);
            ReclaimAbandonedRun(budget, options);
            return ValueTask.FromResult(Snapshot(budget, options));
        }
    }

    /// <inheritdoc />
    public ValueTask<CoachRunLeaseResult> TryStartRunAsync(
        string userProfileId,
        DateOnly userLocalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileId);
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options.CurrentValue;
        var budget = GetOrCreate(userProfileId);

        lock (budget.Gate)
        {
            RollPeriods(budget, userLocalDate);
            ReclaimAbandonedRun(budget, options);

            if (budget.ActiveRunId is not null)
            {
                return ValueTask.FromResult(Denied(budget, options, CoachStopReason.ConcurrencyLimit));
            }

            if (budget.Day.Runs >= options.MaxRunsPerDay || budget.Week.Runs >= options.MaxRunsPerWeek)
            {
                return ValueTask.FromResult(Denied(budget, options, CoachStopReason.RateLimit));
            }

            // Charge the run at acquisition. A cancelled run still consumes its daily/weekly slot,
            // so cancel-and-retry cannot be used to walk past the cap.
            budget.Day = budget.Day with { Runs = budget.Day.Runs + 1 };
            budget.Week = budget.Week with { Runs = budget.Week.Runs + 1 };

            var runId = Guid.NewGuid();
            budget.ActiveRunId = runId;
            budget.ActiveRunStartedUtc = _timeProvider.GetUtcNow();

            var lease = new RunLease(budget, runId);

            return ValueTask.FromResult(new CoachRunLeaseResult
            {
                Acquired = true,
                DeniedReason = null,
                Lease = lease,
                Snapshot = Snapshot(budget, options)
            });
        }
    }

    /// <summary>Computes the ISO-8601 week key (<c>yyyy-Www</c>) for a user-local date.</summary>
    public static string GetWeekKey(DateOnly userLocalDate)
    {
        var date = userLocalDate.ToDateTime(TimeOnly.MinValue);
        var year = ISOWeek.GetYear(date);
        var week = ISOWeek.GetWeekOfYear(date);
        return string.Create(CultureInfo.InvariantCulture, $"{year:D4}-W{week:D2}");
    }

    private LearnerBudget GetOrCreate(string userProfileId)
        => _budgets.GetOrAdd(HashUserKey(userProfileId), static _ => new LearnerBudget());

    /// <summary>
    /// Hashes a profile id for use as an internal dictionary key. Not a security boundary — it only
    /// keeps the raw identifier out of long-lived in-process state.
    /// </summary>
    private static string HashUserKey(string userProfileId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userProfileId)));

    private static void RollPeriods(LearnerBudget budget, DateOnly userLocalDate)
    {
        var weekKey = GetWeekKey(userLocalDate);

        if (budget.DayKey != userLocalDate)
        {
            budget.DayKey = userLocalDate;
            budget.Day = CoachUsageCounters.Empty;
        }

        if (!string.Equals(budget.WeekKey, weekKey, StringComparison.Ordinal))
        {
            budget.WeekKey = weekKey;
            budget.Week = CoachUsageCounters.Empty;
        }
    }

    private void ReclaimAbandonedRun(LearnerBudget budget, CoachOptions options)
    {
        if (budget.ActiveRunId is null || budget.ActiveRunStartedUtc is not { } startedUtc)
        {
            return;
        }

        var maxHold = options.RequestTimeout + AbandonedRunGrace;
        if (_timeProvider.GetUtcNow() - startedUtc >= maxHold)
        {
            budget.ActiveRunId = null;
            budget.ActiveRunStartedUtc = null;
        }
    }

    private static CoachRunLeaseResult Denied(LearnerBudget budget, CoachOptions options, CoachStopReason reason)
        => new()
        {
            Acquired = false,
            DeniedReason = reason,
            Lease = null,
            Snapshot = Snapshot(budget, options)
        };

    private static CoachBudgetSnapshot Snapshot(LearnerBudget budget, CoachOptions options)
        => new()
        {
            DayKey = budget.DayKey,
            WeekKey = budget.WeekKey,
            Day = budget.Day,
            Week = budget.Week,
            MaxRunsPerDay = options.MaxRunsPerDay,
            MaxRunsPerWeek = options.MaxRunsPerWeek,
            HasActiveRun = budget.ActiveRunId is not null
        };

    private static void Release(LearnerBudget budget, Guid runId)
    {
        lock (budget.Gate)
        {
            // Only the owning run may clear the slot. A late release from a reclaimed run must not
            // cancel a newer run's slot.
            if (budget.ActiveRunId == runId)
            {
                budget.ActiveRunId = null;
                budget.ActiveRunStartedUtc = null;
            }
        }
    }

    private static void AddUsage(LearnerBudget budget, CoachRunUsage usage)
    {
        lock (budget.Gate)
        {
            budget.Day = Accumulate(budget.Day, usage);
            budget.Week = Accumulate(budget.Week, usage);
        }
    }

    private static CoachUsageCounters Accumulate(CoachUsageCounters counters, CoachRunUsage usage)
        => counters with
        {
            InputTokens = counters.InputTokens + usage.InputTokens,
            OutputTokens = counters.OutputTokens + usage.OutputTokens,
            EstimatedCostUsd = counters.EstimatedCostUsd + usage.EstimatedCostUsd
        };

    private sealed class LearnerBudget
    {
        public object Gate { get; } = new();
        public DateOnly DayKey { get; set; }
        public string WeekKey { get; set; } = string.Empty;
        public CoachUsageCounters Day { get; set; } = CoachUsageCounters.Empty;
        public CoachUsageCounters Week { get; set; } = CoachUsageCounters.Empty;
        public Guid? ActiveRunId { get; set; }
        public DateTimeOffset? ActiveRunStartedUtc { get; set; }
    }

    private sealed class RunLease : ICoachRunLease
    {
        private readonly LearnerBudget _budget;
        private int _released;

        public RunLease(LearnerBudget budget, Guid runId)
        {
            _budget = budget;
            RunId = runId;
        }

        public Guid RunId { get; }

        public bool IsReleased => Volatile.Read(ref _released) == 1;

        public ValueTask RecordUsageAsync(CoachRunUsage usage, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsReleased)
            {
                // A late completion callback must not write into a period the run no longer owns.
                return ValueTask.CompletedTask;
            }

            AddUsage(_budget, usage);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Release(_budget, RunId);
            }

            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
