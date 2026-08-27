using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Coach.Reports;

/// <summary>What one report retention pass removed.</summary>
/// <param name="RowsDeleted">How many reports aged out.</param>
public readonly record struct CoachResponseReportRetentionResult(int RowsDeleted)
{
    /// <summary>True when the pass had nothing to do.</summary>
    public bool IsEmpty => RowsDeleted == 0;
}

/// <summary>
/// Ages out learner reports past the retention window.
/// </summary>
/// <remarks>
/// <para>
/// Runs inside the existing <c>CoachExpiryCleanupService</c> pass, which
/// <c>CoachCleanupRunner</c> already holds under <c>ICoachCleanupLease</c> — a PostgreSQL
/// advisory transaction lock in production. Joining that pass rather than adding a background
/// service is what keeps exactly one replica sweeping without inventing a second lease.
/// </para>
/// <para>
/// <b>Reports have no review lifecycle to protect, so every row ages out.</b> That is the
/// difference from the opportunity sweep, which spares statuses a reviewer decided something
/// about: a report is an observation the learner made, and the decision it leads to lives on the
/// ledger row it raised — which has its own, longer-lived protection. A report that has aged out
/// stops rendering as "Reported for review", and that is truthful rather than a bug: the record
/// it referred to is gone.
/// </para>
/// <para>
/// Bounded by a batch so a first run against a long-lived table cannot hold the cleanup lease —
/// and therefore the whole cleanup pass — for an unbounded time.
/// </para>
/// </remarks>
public sealed class CoachResponseReportRetentionSweep
{
    /// <summary>How many rows one retention pass may remove, so a sweep stays bounded.</summary>
    public const int RetentionBatchSize = 500;

    private readonly CoachDbContext _db;
    private readonly IOptionsMonitor<CoachResponseReportOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachResponseReportRetentionSweep> _logger;

    public CoachResponseReportRetentionSweep(
        CoachDbContext db,
        IOptionsMonitor<CoachResponseReportOptions> options,
        TimeProvider timeProvider,
        ILogger<CoachResponseReportRetentionSweep> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Removes one bounded batch of aged-out reports.</summary>
    public async Task<CoachResponseReportRetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!options.RetentionSweepEnabled)
        {
            return new CoachResponseReportRetentionResult(0);
        }

        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - options.Retention;

        var expiring = await _db.CoachResponseReports
            .Where(row => row.ReportedAtUtc < cutoff)
            .OrderBy(row => row.ReportedAtUtc)
            .Take(RetentionBatchSize)
            .Select(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expiring.Count == 0)
        {
            return new CoachResponseReportRetentionResult(0);
        }

        var deleted = await _db.CoachResponseReports
            .Where(row => expiring.Contains(row.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // A count only. No identifier, no reason, no owner.
        _logger.LogInformation(
            "[Coach] Response report retention removed {RowCount} rows older than {RetentionDays} days.",
            deleted,
            options.RetentionDays);

        return new CoachResponseReportRetentionResult(deleted);
    }
}
