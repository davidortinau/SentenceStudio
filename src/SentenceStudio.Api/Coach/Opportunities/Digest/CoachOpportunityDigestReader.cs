using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Opportunities.Digest;

/// <summary>
/// Reads the operational digest: fixed aggregate queries, no owner scope, no evidence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The operator review surface can decrypt learner messages, so it
/// is Development-only and stays that way until this codebase has an admin authorization
/// primitive. That left Production with reporting switched on and no person able to read the
/// signal — which would have made the learner-facing promise ("Reported for review") false. This
/// reader is the honest half: everything a reviewer needs to triage, nothing that could identify
/// whose report it was.
/// </para>
/// <para>
/// <b>Why it is not an endpoint.</b> There is no route, no controller, and no service
/// registration that maps one. Adding an admin-shaped HTTP surface would have needed an
/// authorization primitive invented under time pressure — the exact trade the operator surface
/// was kept out of Production to avoid. It is invoked out-of-band by an operator who already
/// holds database credentials, which is an authorization boundary that already exists and is
/// already audited by Azure.
/// </para>
/// <para>
/// <b>Why every query is fixed.</b> Nothing here is composed from caller input except a UTC
/// instant and a bounded take. There is no raw SQL, no interpolated predicate, and no projection
/// that names an identifier column — the only column touched that could address a person is
/// <c>UserProfileId</c>, and it is reachable exclusively through <c>Distinct().Count()</c>, so
/// the strongest statement this reader can make about a learner is a number.
/// </para>
/// </remarks>
public sealed class CoachOpportunityDigestReader
{
    /// <summary>The most problems one digest will list before it declares itself truncated.</summary>
    public const int MaxLines = CoachOpportunityLimits.OperatorRollupMax;

    private readonly CoachDbContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the reader over a context and a clock.</summary>
    public CoachOpportunityDigestReader(CoachDbContext db, TimeProvider timeProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Reads the digest for everything observed at or after <paramref name="sinceUtc"/>.</summary>
    /// <param name="sinceUtc">The window's lower bound, or null for everything still retained.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<CoachOpportunityDigest> ReadAsync(
        DateTime? sinceUtc,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var grouped = await OpportunityGroups(sinceUtc)
            .Take(MaxLines + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var truncated = grouped.Count > MaxLines;
        if (truncated)
        {
            grouped = grouped.Take(MaxLines).ToList();
        }

        // Statuses are read as a second pass and reduced to a distinct set, exactly as the
        // operator rollup does: one digest line spans several learners, so "what has been decided
        // about this problem" is a set rather than a single value, and the set carries no owner.
        //
        // The window bound is applied here too. Without it a digest scoped to the last seven days
        // would report a status belonging to a bucket outside the window — a problem dismissed a
        // year ago would render as "Dismissed" against fresh occurrences the reviewer is looking
        // at precisely because they are new.
        var statuses = await StatusPairs(sinceUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var statusesByFingerprint = statuses
            .GroupBy(entry => entry.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)[.. group
                    .Select(entry => entry.Status.ToString())
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)],
                StringComparer.Ordinal);

        var lines = grouped
            .Select(group => new CoachOpportunityDigestLine(
                group.Fingerprint,
                group.Kind.ToString(),
                group.Disposition.ToString(),
                group.CapabilityCode,
                group.ToolName,
                group.FailureCode,
                group.OfferLink.ToString(),
                group.TotalOccurrences,
                group.DistinctLearners,
                group.RowCount,
                group.FirstObservedAtUtc,
                group.LastObservedAtUtc,
                statusesByFingerprint.TryGetValue(group.Fingerprint, out var found) ? found : []))
            .ToList();

        var reasons = await ReasonGroups(sinceUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var reasonLines = reasons
            .Select(group => new CoachOpportunityDigestReasonLine(
                group.Reason.ToString(),
                group.ReportCount,
                group.DistinctLearners,
                group.FirstReportedAtUtc,
                group.LastReportedAtUtc))
            .ToList();

        return new CoachOpportunityDigest(
            GeneratedAtUtc: now,
            WindowStartUtc: sinceUtc,
            WindowEndUtc: now,
            Lines: lines,
            ReportReasons: reasonLines,
            TotalReports: reasonLines.Sum(line => line.ReportCount),
            TotalOpportunityRows: lines.Sum(line => line.RowCount),
            Truncated: truncated);
    }

    /// <summary>
    /// The SQL every digest query would run, for the projection guard.
    /// </summary>
    /// <remarks>
    /// Exposed to the test assembly rather than described in a comment, so the assertion that no
    /// identifier column is projected is made against what the provider will actually execute
    /// instead of against what this file appears to say.
    /// </remarks>
    internal IReadOnlyList<string> DescribeQueries(DateTime? sinceUtc) =>
    [
        OpportunityGroups(sinceUtc).Take(MaxLines + 1).ToQueryString(),
        StatusPairs(sinceUtc).ToQueryString(),
        ReasonGroups(sinceUtc).ToQueryString()
    ];

    private IQueryable<OpportunityGroup> OpportunityGroups(DateTime? sinceUtc)
    {
        var query = _db.CoachOpportunities.AsNoTracking();

        if (sinceUtc is { } since)
        {
            query = query.Where(row => row.LastObservedAtUtc >= since);
        }

        return query
            .GroupBy(row => new
            {
                row.Fingerprint,
                row.Kind,
                row.Disposition,
                row.CapabilityCode,
                row.ToolName,
                row.FailureCode,
                row.OfferLink
            })
            .Select(group => new OpportunityGroup
            {
                Fingerprint = group.Key.Fingerprint,
                Kind = group.Key.Kind,
                Disposition = group.Key.Disposition,
                CapabilityCode = group.Key.CapabilityCode,
                ToolName = group.Key.ToolName,
                FailureCode = group.Key.FailureCode,
                OfferLink = group.Key.OfferLink,
                TotalOccurrences = group.Sum(row => row.OccurrenceCount),

                // The only path from this reader to the column that names a person, and it ends
                // in a scalar. Changing this to anything that returns rows is the one edit that
                // would turn the digest into a cross-tenant read.
                DistinctLearners = group.Select(row => row.UserProfileId).Distinct().Count(),
                RowCount = group.Count(),
                FirstObservedAtUtc = group.Min(row => row.FirstObservedAtUtc),
                LastObservedAtUtc = group.Max(row => row.LastObservedAtUtc)
            })
            .OrderByDescending(group => group.TotalOccurrences)
            .ThenBy(group => group.Fingerprint);
    }

    private IQueryable<StatusPair> StatusPairs(DateTime? sinceUtc)
    {
        var query = _db.CoachOpportunities.AsNoTracking();

        if (sinceUtc is { } since)
        {
            query = query.Where(row => row.LastObservedAtUtc >= since);
        }

        return query
            .Select(row => new StatusPair { Fingerprint = row.Fingerprint, Status = row.Status })
            .Distinct();
    }

    private IQueryable<ReasonGroup> ReasonGroups(DateTime? sinceUtc)
    {
        var query = _db.CoachResponseReports.AsNoTracking();

        if (sinceUtc is { } since)
        {
            query = query.Where(row => row.ReportedAtUtc >= since);
        }

        return query
            .GroupBy(row => row.Reason)
            .Select(group => new ReasonGroup
            {
                Reason = group.Key,
                ReportCount = group.Count(),
                DistinctLearners = group.Select(row => row.UserProfileId).Distinct().Count(),
                FirstReportedAtUtc = group.Min(row => row.ReportedAtUtc),
                LastReportedAtUtc = group.Max(row => row.ReportedAtUtc)
            })
            .OrderByDescending(group => group.ReportCount)
            .ThenBy(group => group.Reason);
    }

    /// <summary>The aggregate row the ledger query materializes. Counts and closed codes only.</summary>
    private sealed class OpportunityGroup
    {
        public string Fingerprint { get; init; } = string.Empty;
        public CoachOpportunityKind Kind { get; init; }
        public CoachOpportunityDisposition Disposition { get; init; }
        public string CapabilityCode { get; init; } = string.Empty;
        public string? ToolName { get; init; }
        public string? FailureCode { get; init; }
        public CoachOpportunityOfferLink OfferLink { get; init; }
        public int TotalOccurrences { get; init; }
        public int DistinctLearners { get; init; }
        public int RowCount { get; init; }
        public DateTime FirstObservedAtUtc { get; init; }
        public DateTime LastObservedAtUtc { get; init; }
    }

    /// <summary>A fingerprint and one status observed under it.</summary>
    private sealed class StatusPair
    {
        public string Fingerprint { get; init; } = string.Empty;
        public CoachOpportunityStatus Status { get; init; }
    }

    /// <summary>The aggregate row the report query materializes.</summary>
    private sealed class ReasonGroup
    {
        public CoachResponseReportReason Reason { get; init; }
        public int ReportCount { get; init; }
        public int DistinctLearners { get; init; }
        public DateTime FirstReportedAtUtc { get; init; }
        public DateTime LastReportedAtUtc { get; init; }
    }
}
