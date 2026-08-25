using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>What one retention pass removed.</summary>
/// <param name="RowsDeleted">How many rows aged out.</param>
public readonly record struct CoachOpportunityRetentionResult(int RowsDeleted)
{
    /// <summary>True when the pass had nothing to do.</summary>
    public bool IsEmpty => RowsDeleted == 0;
}

/// <summary>
/// Ages out ledger rows that nobody decided anything about.
/// </summary>
/// <remarks>
/// <para>
/// Runs inside the existing <c>CoachExpiryCleanupService</c> pass, which
/// <c>CoachCleanupRunner</c> already holds under <c>ICoachCleanupLease</c> — a PostgreSQL
/// advisory transaction lock in production. Joining that pass rather than adding a background
/// service is what keeps exactly one replica sweeping without inventing a second lease.
/// </para>
/// <para>
/// <b>Only <see cref="CoachOpportunityStatus.New"/> and
/// <see cref="CoachOpportunityStatus.Dismissed"/> age out.</b>
/// <see cref="CoachOpportunityStatus.Reviewed"/>,
/// <see cref="CoachOpportunityStatus.Accepted"/>, and
/// <see cref="CoachOpportunityStatus.Deferred"/> all survive, because each is a decision somebody
/// made and a retention policy that deleted decisions would quietly erase the reason a spec
/// exists. <c>Reviewed</c> belongs with them: a reviewer who read a row and has not decided yet
/// has done work, and deleting it silently returns the problem to the pool as though nobody had
/// ever looked — the same review then happens again on a fresh row. A dismissed problem that
/// keeps recurring keeps refreshing its own <c>LastObservedAtUtc</c>, so it stays visible for as
/// long as it is still happening.
/// </para>
/// <para>
/// The set is taken from <see cref="CoachOpportunityReviewTransitions.RetentionEligible"/> rather
/// than restated here, so the retention rule and the transition rule that protects it can never
/// drift apart — a status that this sweep deletes but that the transition policy does not treat
/// as retention-eligible would let a reviewer walk a decided row into a silent delete.
/// </para>
/// </remarks>
public sealed class CoachOpportunityRetentionSweep
{
    private readonly CoachDbContext _db;
    private readonly IOptionsMonitor<CoachOpportunityOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachOpportunityRetentionSweep> _logger;

    public CoachOpportunityRetentionSweep(
        CoachDbContext db,
        IOptionsMonitor<CoachOpportunityOptions> options,
        TimeProvider timeProvider,
        ILogger<CoachOpportunityRetentionSweep> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Removes one bounded batch of aged-out rows.</summary>
    public async Task<CoachOpportunityRetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!options.RetentionSweepEnabled)
        {
            return new CoachOpportunityRetentionResult(0);
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - options.Retention;

        // The one shared definition of "retention-eligible". Materialized to a local so the
        // provider translates it to an IN (...) rather than closing over a static property.
        var eligible = CoachOpportunityReviewTransitions.RetentionEligible;

        // Bounded by a batch so a first run against a long-lived table cannot hold the cleanup
        // lease — and therefore the whole cleanup pass — for an unbounded time.
        var expiring = await _db.CoachOpportunities
            .Where(row => row.LastObservedAtUtc < cutoff && eligible.Contains(row.Status))
            .OrderBy(row => row.LastObservedAtUtc)
            .Take(CoachOpportunityLimits.RetentionBatchSize)
            .Select(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expiring.Count == 0)
        {
            return new CoachOpportunityRetentionResult(0);
        }

        var deleted = await _db.CoachOpportunities
            .Where(row => expiring.Contains(row.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // A count only. No identifier, no capability code, no owner.
        _logger.LogInformation(
            "[Coach] Opportunity retention removed {RowCount} rows older than {RetentionDays} days.",
            deleted,
            options.RetentionDays);

        return new CoachOpportunityRetentionResult(deleted);
    }
}
