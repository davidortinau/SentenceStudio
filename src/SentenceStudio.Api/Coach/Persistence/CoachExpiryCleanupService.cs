using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>What one cleanup pass removed.</summary>
/// <param name="ExpiredSessionsDeleted">Expired sessions removed, with their conversation state and pending suggestions.</param>
/// <param name="RevisionsDeleted">Revision audit rows past the retention window.</param>
/// <param name="UsageRowsDeleted">Usage counter rows past the retention window.</param>
/// <param name="OpportunitiesDeleted">Undecided opportunity ledger rows past their retention window.</param>
public sealed record CoachCleanupResult(
    int ExpiredSessionsDeleted,
    int RevisionsDeleted,
    int UsageRowsDeleted,
    int OpportunitiesDeleted = 0)
{
    /// <summary>True when the pass removed nothing.</summary>
    public bool IsEmpty =>
        ExpiredSessionsDeleted == 0
        && RevisionsDeleted == 0
        && UsageRowsDeleted == 0
        && OpportunitiesDeleted == 0;
}

/// <summary>
/// Removes coach data past its retention window.
/// </summary>
/// <remarks>
/// This service is intentionally <b>not</b> wired to a hosted background service yet. It
/// is written to be callable by a future scheduled host (or an admin endpoint) so the
/// schedule decision stays separate from the deletion logic. It only ever deletes coach
/// tables — it never touches learner learning data, and it never undoes Today's Plan.
/// </remarks>
public sealed class CoachExpiryCleanupService
{
    private readonly CoachDbContext _db;
    private readonly CoachPersistenceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachExpiryCleanupService> _logger;
    private readonly Cleanup.ICoachExpiredSessionFilter _expiredSessionFilter;

    /// <summary>
    /// The opportunity ledger's retention pass, when this host has one.
    /// </summary>
    /// <remarks>
    /// Optional so every existing hand-constructed call site keeps working. A host without one
    /// simply does not age out opportunity rows, which is the same behaviour as before the table
    /// existed — the sweep is a retention policy, not something the session cleanup needs in
    /// order to be correct.
    /// </remarks>
    private readonly Opportunities.CoachOpportunityRetentionSweep? _opportunityRetention;

    /// <summary>
    /// The learner-report retention pass, when this host has one.
    /// </summary>
    /// <remarks>
    /// Optional for the same reason the opportunity sweep is: a host without one simply does not
    /// age out report rows, which is the behaviour that existed before the table did.
    /// </remarks>
    private readonly Reports.CoachResponseReportRetentionSweep? _reportRetention;

    public CoachExpiryCleanupService(
        CoachDbContext db,
        IOptions<CoachPersistenceOptions> options,
        TimeProvider timeProvider,
        ILogger<CoachExpiryCleanupService> logger,
        Cleanup.ICoachExpiredSessionFilter? expiredSessionFilter = null,
        Opportunities.CoachOpportunityRetentionSweep? opportunityRetention = null,
        Reports.CoachResponseReportRetentionSweep? reportRetention = null)
    {
        _db = db;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _opportunityRetention = opportunityRetention;
        _reportRetention = reportRetention;

        // Optional with a safe default so existing call sites keep working; the filter is a
        // retention policy, not a dependency the deletion logic needs in order to be correct.
        _expiredSessionFilter = expiredSessionFilter ?? new Cleanup.CheckpointOnlyExpiredSessionFilter();
    }

    /// <summary>Runs one bounded cleanup pass.</summary>
    public async Task<CoachCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var batch = Math.Max(1, _options.CleanupBatchSize);

        var expiredCandidates = await _db.CoachSessions
            .Where(s => s.ExpiresAt <= now)
            .OrderBy(s => s.ExpiresAt)
            .Take(batch)
            .ToListAsync(cancellationToken);

        // Expiry decides which checkpoints are *candidates*; the filter decides which of those
        // this job is allowed to remove. Rows held back are simply reconsidered next pass.
        var expiredSessions = expiredCandidates.Count == 0
            ? expiredCandidates
            : (IReadOnlyList<CoachSession>)await _expiredSessionFilter
                .SelectDeletableAsync(expiredCandidates, cancellationToken);

        if (expiredSessions.Count > 0)
        {
            _db.CoachSessions.RemoveRange(expiredSessions);
        }

        var revisionCutoff = now - _options.RevisionRetention;
        var staleRevisions = await _db.CoachPlanRevisions
            .Where(r => r.CreatedAt <= revisionCutoff)
            .OrderBy(r => r.CreatedAt)
            .Take(batch)
            .ToListAsync(cancellationToken);

        if (staleRevisions.Count > 0)
        {
            _db.CoachPlanRevisions.RemoveRange(staleRevisions);
        }

        var usageCutoff = DateOnly.FromDateTime(now - _options.UsageRetention);
        var staleUsage = await _db.CoachUsages
            .Where(u => u.LocalDate < usageCutoff)
            .OrderBy(u => u.LocalDate)
            .Take(batch)
            .ToListAsync(cancellationToken);

        if (staleUsage.Count > 0)
        {
            _db.CoachUsages.RemoveRange(staleUsage);
        }

        if (expiredSessions.Count + staleRevisions.Count + staleUsage.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "[Coach] Cleanup removed {Sessions} expired sessions, {Revisions} revisions, {Usage} usage rows.",
                expiredSessions.Count, staleRevisions.Count, staleUsage.Count);
        }

        // Last, and on its own statement rather than through the tracker. The opportunity ledger
        // has no relationship to the rows above and its own retention window is much longer, so a
        // failure here must not roll back a session expiry that already succeeded — and a first
        // pass over a long-lived table must not enlarge the SaveChanges above into an unbounded
        // one.
        var opportunities = 0;
        if (_opportunityRetention is not null)
        {
            var swept = await _opportunityRetention.RunAsync(cancellationToken).ConfigureAwait(false);
            opportunities = swept.RowsDeleted;
        }

        // Reports age out on their own switch and their own window, after the ledger pass and for
        // the same reason it runs last: both are bounded batches on tables that can be long-lived,
        // and neither may enlarge the SaveChanges above into an unbounded one.
        if (_reportRetention is not null)
        {
            await _reportRetention.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        return new CoachCleanupResult(
            expiredSessions.Count, staleRevisions.Count, staleUsage.Count, opportunities);
    }
}
