using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Security.DataProtection;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>Why an operator request was refused.</summary>
public enum CoachOpportunityOperatorStatus
{
    /// <summary>The request succeeded.</summary>
    Success = 0,

    /// <summary>
    /// The surface is off, the caller is outside the cohort, or the row does not exist. All of
    /// them answer 404: the operator surface never confirms that something exists but is
    /// off-limits, which is the rule the coach routes already follow.
    /// </summary>
    NotAvailable = 1,

    /// <summary>The request body was missing, malformed, or failed a bound.</summary>
    InvalidRequest = 2,

    /// <summary>The reveal was refused because the row belongs to another learner.</summary>
    /// <remarks>
    /// Answered to the caller as a <b>404, not a 403</b>, exactly like
    /// <see cref="NotAvailable"/>. A 403 here would confirm that this identifier names a real row
    /// owned by somebody else, which is the existence oracle every other refusal on this surface
    /// is shaped to avoid. The distinct member survives so the service can log the cross-owner
    /// case for what it is; only the wire representation is collapsed.
    /// </remarks>
    CrossOwnerRefused = 3,

    /// <summary>
    /// The reveal was refused because this host's Data Protection key ring is ephemeral.
    /// </summary>
    EphemeralKeyRing = 4,

    /// <summary>
    /// The review asked for a status transition the lifecycle refuses — walking a decided row
    /// back into a retention-eligible status. See <see cref="CoachOpportunityReviewTransitions"/>.
    /// </summary>
    TransitionRefused = 5
}

/// <summary>An operator result and why it turned out that way.</summary>
public readonly record struct CoachOpportunityOperatorResult<T>(
    CoachOpportunityOperatorStatus Status,
    T? Value)
{
    /// <summary>True when the request succeeded.</summary>
    public bool IsOk => Status == CoachOpportunityOperatorStatus.Success;

    /// <summary>Builds a success result.</summary>
    public static CoachOpportunityOperatorResult<T> Ok(T value) =>
        new(CoachOpportunityOperatorStatus.Success, value);

    /// <summary>Builds a failure result.</summary>
    public static CoachOpportunityOperatorResult<T> Fail(CoachOpportunityOperatorStatus status) =>
        new(status, default);
}

/// <summary>
/// Reads, reviews, and — only on an explicit acknowledged request — reveals the ledger.
/// </summary>
/// <remarks>
/// <para>
/// Every read on this service is either <b>owner-scoped</b> or a <b>content-free aggregate</b>.
/// The rollup groups by fingerprint and returns <c>COUNT(DISTINCT UserProfileId)</c> — a number,
/// never an identifier — so a reviewer can see that three learners hit the same gap without the
/// surface ever being able to say which three.
/// </para>
/// <para>
/// The gates are enforced by the endpoint layer (route mapping, environment, flag, cohort) and by
/// this service (owner match, acknowledgement literal, key-ring durability). Both halves fail
/// closed and neither is sufficient alone.
/// </para>
/// </remarks>
public sealed class CoachOpportunityOperatorService
{
    private readonly CoachDbContext _db;
    private readonly ICoachMessageStore? _messages;
    private readonly IUserScopeProvider _userScope;
    private readonly IOptionsMonitor<CoachOpportunityOptions> _options;
    private readonly IOptionsMonitor<CoachOptions> _coachOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachOpportunityOperatorService> _logger;
    private readonly CoachKeyRingPlan? _keyRingPlan;

    public CoachOpportunityOperatorService(
        CoachDbContext db,
        IUserScopeProvider userScope,
        IOptionsMonitor<CoachOpportunityOptions> options,
        IOptionsMonitor<CoachOptions> coachOptions,
        TimeProvider timeProvider,
        ILogger<CoachOpportunityOperatorService> logger,
        ICoachMessageStore? messages = null,
        CoachKeyRingPlan? keyRingPlan = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _userScope = userScope ?? throw new ArgumentNullException(nameof(userScope));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _coachOptions = coachOptions ?? throw new ArgumentNullException(nameof(coachOptions));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _messages = messages;
        _keyRingPlan = keyRingPlan;
    }

    /// <summary>
    /// The surface flag plus the cohort check.
    /// </summary>
    /// <remarks>
    /// <b>The <c>__dev_all__</c> sentinel is not honoured here.</b> That sentinel exists so a
    /// developer can use the coach product without enumerating themselves; it must not also open
    /// a screen that can decrypt learner messages. Being in the cohort has to be a deliberate
    /// entry in <c>Coach:AllowedUserProfileIds</c>.
    /// </remarks>
    public bool IsCallerAuthorized()
    {
        if (!_options.CurrentValue.OperatorSurface.Enabled)
        {
            return false;
        }

        if (!_userScope.TryGetUserProfileId(out var userProfileId)
            || string.IsNullOrWhiteSpace(userProfileId))
        {
            return false;
        }

        return _coachOptions.CurrentValue.IsInCohort(userProfileId, allowDevelopmentSentinel: false);
    }

    /// <summary>Lists content-free rows, newest first. Aggregate-only rows are excluded.</summary>
    /// <remarks>
    /// <para>
    /// Excluded because they are, by construction, not individually reviewable: they carry no
    /// conversation, no turn, and no pointers, so a row in a triage list would be a line a
    /// reviewer can do nothing with. Their signal lives in <see cref="RollupAsync"/>.
    /// </para>
    /// <para>
    /// <b>There is deliberately no <c>disposition</c> filter.</b> The only two values are
    /// <c>Product</c> and <c>AggregateOnly</c>, and this method already fixes the first: a filter
    /// parameter would be a control whose only settings are "what you already have" and "nothing
    /// at all". Filter by <paramref name="status"/>, <paramref name="kind"/>, or
    /// <paramref name="capabilityCode"/> instead.
    /// </para>
    /// </remarks>
    public async Task<CoachOpportunityOperatorResult<CoachOpportunityPageDto>> ListAsync(
        CoachOpportunityStatus? status,
        CoachOpportunityKind? kind,
        string? capabilityCode,
        DateTime? since,
        int skip,
        int take,
        CancellationToken cancellationToken = default,
        CoachGroundingReportFilter groundingFilter = default)
    {
        if (!IsCallerAuthorized())
        {
            return CoachOpportunityOperatorResult<CoachOpportunityPageDto>.Fail(
                CoachOpportunityOperatorStatus.NotAvailable);
        }

        if (capabilityCode is not null && !CoachOpportunityCapabilityCodes.IsKnown(capabilityCode))
        {
            // A filter value outside the closed set can only be a typo or a probe. Refusing it
            // keeps the query's inputs closed rather than letting an arbitrary string reach a
            // WHERE clause.
            return CoachOpportunityOperatorResult<CoachOpportunityPageDto>.Fail(
                CoachOpportunityOperatorStatus.InvalidRequest);
        }

        take = Math.Clamp(
            take <= 0 ? CoachOpportunityLimits.OperatorPageSize : take,
            1,
            CoachOpportunityLimits.OperatorPageSizeMax);
        skip = Math.Max(0, skip);

        var query = _db.CoachOpportunities
            .AsNoTracking()
            .Where(row => row.Disposition == CoachOpportunityDisposition.Product);

        if (status is { } statusValue)
        {
            query = query.Where(row => row.Status == statusValue);
        }

        if (kind is { } kindValue)
        {
            query = query.Where(row => row.Kind == kindValue);
        }

        if (capabilityCode is not null)
        {
            query = query.Where(row => row.CapabilityCode == capabilityCode);
        }

        if (since is { } sinceValue)
        {
            query = query.Where(row => row.LastObservedAtUtc >= sinceValue);
        }

        if (!groundingFilter.IsEmpty)
        {
            // Applied as an EXISTS against the row's own reports rather than as a join, so a row
            // with many reports still contributes one boolean and the page stays proportional to
            // the page rather than to the reports.
            //
            // Every filter value was validated before it reached here, so the closed sets stay
            // closed and an arbitrary string never reaches a WHERE clause. A row with no reports
            // never matches — which is the fail-closed reading and, deliberately, exactly what a
            // row whose reports do not match looks like from outside.
            var reports = _db.CoachResponseReports.AsNoTracking();

            if (groundingFilter.Stage is { } stage)
            {
                reports = reports.Where(report => report.GroundingStage == stage);
            }

            if (groundingFilter.Refused is { } refused)
            {
                reports = reports.Where(report => report.GroundingRefused == refused);
            }

            if (groundingFilter.LimitationCode is { } limitation)
            {
                reports = reports.Where(report => report.GroundingLimitationCode == limitation);
            }

            if (groundingFilter.RuleCode is { Length: > 0 } ruleCode)
            {
                // The column is an ordinal-sorted comma-joined list of whole member names, so a
                // containment test on the bare name cannot half-match another member: no declared
                // rule name is a substring of another.
                reports = reports.Where(report =>
                    report.GroundingRuleCodes != null && report.GroundingRuleCodes.Contains(ruleCode));
            }

            query = query.Where(row => reports.Any(report => report.OpportunityId == row.Id));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await query
            .OrderByDescending(row => row.LastObservedAtUtc)
            .ThenBy(row => row.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return CoachOpportunityOperatorResult<CoachOpportunityPageDto>.Ok(
            new CoachOpportunityPageDto(
                // Explicitly one-argument, so the optional report parameter cannot bind Select's
                // index overload. The list deliberately carries no report facts: it is a triage
                // surface, and a per-row join would make loading it proportional to the reports
                // rather than to the page.
                [.. rows.Select(row => Project(row))],
                total,
                skip,
                take));
    }

    /// <summary>
    /// The safe cross-learner aggregate: one line per problem, counts only.
    /// </summary>
    /// <remarks>
    /// Includes aggregate-only rows, because this is where their signal belongs. The projection
    /// contains no owner identifier of any kind — <c>DistinctLearners</c> is the only trace of
    /// who was affected, and it is a number.
    /// </remarks>
    public async Task<CoachOpportunityOperatorResult<IReadOnlyList<CoachOpportunityRollupDto>>> RollupAsync(
        DateTime? since,
        CancellationToken cancellationToken = default)
    {
        if (!IsCallerAuthorized())
        {
            return CoachOpportunityOperatorResult<IReadOnlyList<CoachOpportunityRollupDto>>.Fail(
                CoachOpportunityOperatorStatus.NotAvailable);
        }

        var query = _db.CoachOpportunities.AsNoTracking();

        if (since is { } sinceValue)
        {
            query = query.Where(row => row.LastObservedAtUtc >= sinceValue);
        }

        var grouped = await query
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
            .Select(group => new
            {
                group.Key,
                TotalOccurrences = group.Sum(row => row.OccurrenceCount),
                DistinctLearners = group.Select(row => row.UserProfileId).Distinct().Count(),
                RowCount = group.Count(),
                FirstObservedAtUtc = group.Min(row => row.FirstObservedAtUtc),
                LastObservedAtUtc = group.Max(row => row.LastObservedAtUtc)
            })
            .OrderByDescending(group => group.TotalOccurrences)
            .Take(CoachOpportunityLimits.OperatorRollupMax)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var fingerprints = grouped.Select(group => group.Key.Fingerprint).ToList();

        // Statuses are read separately and reduced to a distinct set. A rollup line spans several
        // learners, so "what has been decided about this problem" is a set of statuses rather
        // than one, and the set carries no owner.
        //
        // The same `since` bound is applied here as above, and that is not incidental: without it
        // this query reads every row that ever carried the fingerprint, so a rollup windowed to
        // the last seven days would report statuses belonging to rows outside the window. A
        // problem dismissed a year ago and recurring now would render as "Dismissed" against
        // fresh occurrences the reviewer is looking at precisely because they are new, which
        // inverts the decision the window was opened to support.
        var statusQuery = _db.CoachOpportunities
            .AsNoTracking()
            .Where(row => fingerprints.Contains(row.Fingerprint));

        if (since is { } statusSince)
        {
            statusQuery = statusQuery.Where(row => row.LastObservedAtUtc >= statusSince);
        }

        var statuses = await statusQuery
            .Select(row => new { row.Fingerprint, row.Status })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byFingerprint = statuses
            .GroupBy(entry => entry.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)[.. group.Select(entry => entry.Status.ToString()).Order(StringComparer.Ordinal)],
                StringComparer.Ordinal);

        var result = grouped
            .Select(group => new CoachOpportunityRollupDto(
                group.Key.Fingerprint,
                group.Key.Kind.ToString(),
                group.Key.Disposition.ToString(),
                group.Key.CapabilityCode,
                group.Key.ToolName,
                group.Key.FailureCode,
                group.Key.OfferLink.ToString(),
                group.TotalOccurrences,
                group.DistinctLearners,
                group.RowCount,
                group.FirstObservedAtUtc,
                group.LastObservedAtUtc,
                byFingerprint.TryGetValue(group.Key.Fingerprint, out var found) ? found : []))
            .ToList();

        return CoachOpportunityOperatorResult<IReadOnlyList<CoachOpportunityRollupDto>>.Ok(result);
    }

    /// <summary>Reads one content-free row.</summary>
    public async Task<CoachOpportunityOperatorResult<CoachOpportunityRowDto>> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!IsCallerAuthorized())
        {
            return CoachOpportunityOperatorResult<CoachOpportunityRowDto>.Fail(
                CoachOpportunityOperatorStatus.NotAvailable);
        }

        var row = await _db.CoachOpportunities
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return CoachOpportunityOperatorResult<CoachOpportunityRowDto>.Fail(
                CoachOpportunityOperatorStatus.NotAvailable);
        }

        // Only a learner report has report facts, and only the detail view pays for them. Looked
        // up by the row this report raised rather than by the message pointers, so a row whose
        // conversation has since been deleted still resolves.
        var (facts, rollup) = row.Kind == CoachOpportunityKind.UserReportedResponse
            ? await ReadReportFactsAsync(row, cancellationToken).ConfigureAwait(false)
            : (null, null);

        return CoachOpportunityOperatorResult<CoachOpportunityRowDto>.Ok(Project(row, facts, rollup));
    }

    /// <summary>
    /// The closed-code turn facts behind one report, plus what else landed on the same row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A ledger row is per (learner, problem, UTC day), and a report is per response.</b> A
    /// learner who reports three different answers as "incorrect" on the same day produces three
    /// reports and one ledger row, so "the report behind this row" is not well defined by the row
    /// alone. Selecting the earliest and attaching its stop reason, attempt count, tool list, and
    /// write outcome to the row reads as authoritative and is not: those facts describe one turn,
    /// and the row summarises three.
    /// </para>
    /// <para>
    /// The facts are therefore taken from the report naming the <em>same response this row's
    /// evidence points at</em> — the one an operator would decrypt if they revealed evidence — so
    /// the two halves of the detail card describe the same turn. A single report on the row is
    /// unambiguous and is used directly. When neither holds, the facts are <b>omitted</b> and
    /// <see cref="CoachOpportunityReportRollupDto.FactsAreForTheReportedResponse"/> says so;
    /// borrowing another turn's facts to fill the block would be worse than an empty block,
    /// because an empty block is legible as missing.
    /// </para>
    /// <para>
    /// Null on both is a normal answer: a report written while the ledger was unavailable carries
    /// no opportunity id, and a report aged out by retention is simply gone. Neither is an error,
    /// and the detail card renders the ledger row without the extra blocks.
    /// </para>
    /// </remarks>
    private async Task<(CoachOpportunityReportFactsDto? Facts, CoachOpportunityReportRollupDto? Rollup)>
        ReadReportFactsAsync(
            CoachOpportunity row,
            CancellationToken cancellationToken)
    {
        var linked = _db.CoachResponseReports
            .AsNoTracking()
            .Where(entity => entity.OpportunityId == row.Id);

        // Counts and closed reason codes. Grouped in the database so a row that somehow collected
        // an unbounded number of reports still costs one small result set to summarise.
        var reasons = await linked
            .GroupBy(entity => entity.Reason)
            .Select(group => new
            {
                Reason = group.Key,
                ReportCount = group.Count(),
                ResponseCount = group.Select(entity => entity.CoachMessageId).Distinct().Count(),
                FirstReportedAtUtc = group.Min(entity => entity.ReportedAtUtc),
                LastReportedAtUtc = group.Max(entity => entity.ReportedAtUtc)
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (reasons.Count == 0)
        {
            return (null, null);
        }

        var reportCount = reasons.Sum(entry => entry.ReportCount);

        // The report naming the response this row's evidence points at. Read by that pointer
        // rather than by time, which is the whole correction: the evidence pointer and the turn
        // facts must describe the same response or the card is quietly mixing two turns.
        CoachResponseReport? representative;

        if (!string.IsNullOrWhiteSpace(row.EvidenceOfferMessageId))
        {
            representative = await linked
                .Where(entity => entity.CoachMessageId == row.EvidenceOfferMessageId)
                .OrderBy(entity => entity.ReportedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            // No fallback, deliberately. A row that names a response and has no surviving report
            // for it — retention prunes reports at 180 days while a Reviewed or Accepted row is
            // kept forever — must render an empty facts block, not the facts of whichever other
            // report is still there. An empty block is legible as missing; a borrowed one is not.
        }
        else
        {
            // Defensive: a learner report always carries both pointers, so this branch is for a
            // row that lost them. With nothing to tie against, one report is unambiguous and more
            // than one is not.
            representative = reportCount == 1
                ? await linked.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }

        var rollup = new CoachOpportunityReportRollupDto(
            reportCount,
            reasons.Sum(entry => entry.ResponseCount),
            [.. reasons
                .OrderByDescending(entry => entry.ReportCount)
                .ThenBy(entry => entry.Reason)
                .Select(entry => new CoachOpportunityReportReasonCountDto(
                    entry.Reason.ToString(),
                    entry.ReportCount))],
            reasons.Min(entry => entry.FirstReportedAtUtc),
            reasons.Max(entry => entry.LastReportedAtUtc),
            representative is not null,
            await CountGroundingRulesAsync(linked, cancellationToken).ConfigureAwait(false));

        var facts = representative is null
            ? null
            : new CoachOpportunityReportFactsDto(
                representative.Reason.ToString(),
                representative.ResponseKind.ToString(),
                representative.TurnStatus?.ToString(),
                representative.TurnAttemptCount,
                representative.TurnErrorCode,
                representative.InvokedToolNames,
                representative.WriteStatus?.ToString(),
                representative.WriteFailureCode,
                representative.ReportedAtUtc,

                // Ordinals become names here and nowhere else, so an undefined value renders as
                // null rather than as a number a reviewer would read as meaningful.
                GroundingStage: NameOrNull<Validation.Claims.CoachGroundingStage>(
                    representative.GroundingStage),
                GroundingRefused: representative.GroundingRefused,
                GroundingAltered: representative.GroundingAltered,
                GroundingRepairSuppressed: representative.GroundingRepairSuppressed,
                GroundingFindingCount: representative.GroundingFindingCount,
                GroundingRuleCodes: representative.GroundingRuleCodes,
                GroundingLimitationCode: NameOrNull<SentenceStudio.Contracts.Coach.CoachLimitationCode>(
                    representative.GroundingLimitationCode));

        return (facts, rollup);
    }

    /// <summary>
    /// The name of a closed enum member, or null for an absent or undefined ordinal.
    /// </summary>
    /// <remarks>
    /// Undefined renders as null rather than as the number. A reviewer reading "7" beside a rule
    /// they cannot name learns nothing and may believe the value means something; an empty cell is
    /// legible as absent.
    /// </remarks>
    private static string? NameOrNull<TEnum>(int? ordinal) where TEnum : struct, Enum =>
        ordinal is { } value && Enum.IsDefined((TEnum)(object)value)
            ? ((TEnum)(object)value).ToString()
            : null;

    /// <summary>
    /// How many reports on this row carried each closed grounding rule code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counted in memory over the bounded set of rule-code strings rather than in the database,
    /// because the column holds a comma-joined list and splitting it server-side would be a
    /// per-row string operation the query planner cannot help with. The set is bounded by the
    /// number of reports on one ledger row, which the reason rollup above already reads.
    /// </para>
    /// <para>
    /// Only names the enum still declares are counted. A row written by a build that had a tenth
    /// rule is skipped rather than surfaced as an unnameable token.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<CoachOpportunityGroundingRuleCountDto>> CountGroundingRulesAsync(
        IQueryable<CoachResponseReport> linked,
        CancellationToken cancellationToken)
    {
        var encoded = await linked
            .Where(entity => entity.GroundingRuleCodes != null)
            .Select(entity => entity.GroundingRuleCodes!)
            .Take(CoachOpportunityLimits.OperatorPageSizeMax)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (encoded.Count == 0)
        {
            return Array.Empty<CoachOpportunityGroundingRuleCountDto>();
        }

        var counts = new Dictionary<Validation.Claims.CoachClaimRuleCode, int>();

        foreach (var list in encoded)
        {
            foreach (var token in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<Validation.Claims.CoachClaimRuleCode>(token, out var rule)
                    || !Enum.IsDefined(rule))
                {
                    continue;
                }

                counts[rule] = counts.GetValueOrDefault(rule) + 1;
            }
        }

        return
        [
            .. counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .Select(pair => new CoachOpportunityGroundingRuleCountDto(pair.Key.ToString(), pair.Value))
        ];
    }

    /// <summary>Records a review decision and renders the paste-ready markdown block.</summary>
    /// <remarks>
    /// The requested status is checked against
    /// <see cref="CoachOpportunityReviewTransitions.IsAllowed"/> before anything is mutated:
    /// an <see cref="CoachOpportunityStatus.Accepted"/> row cannot be walked back into a
    /// retention-eligible status, because the retention sweep would then delete a decision
    /// something downstream already points at.
    /// </remarks>
    public async Task<CoachOpportunityOperatorResult<CoachOpportunityReviewResponse>> ReviewAsync(
        string id,
        CoachOpportunityReviewRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!IsCallerAuthorized())
        {
            return CoachOpportunityOperatorResult<CoachOpportunityReviewResponse>.Fail(
                CoachOpportunityOperatorStatus.NotAvailable);
        }

        if (request is null
            || !Enum.IsDefined(request.Status)
            || (request.ReviewerNoteCode is { } note && !Enum.IsDefined(note))
            || !request.IsLinkedSpecPathValid)
        {
            return CoachOpportunityOperatorResult<CoachOpportunityReviewResponse>.Fail(
                CoachOpportunityOperatorStatus.InvalidRequest);
        }

        var row = await _db.CoachOpportunities
            .FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return CoachOpportunityOperatorResult<CoachOpportunityReviewResponse>.Fail(
                CoachOpportunityOperatorStatus.NotAvailable);
        }

        // The lifecycle gate. Refused before anything is mutated, so a rejected transition leaves
        // the row — including its note code and spec path — exactly as it was.
        if (!CoachOpportunityReviewTransitions.IsAllowed(row.Status, request.Status))
        {
            _logger.LogWarning(
                "[Coach] An opportunity review was refused: {Current} cannot move to {Requested}. " +
                "RestoresRetentionEligibility={Restores}",
                row.Status,
                request.Status,
                CoachOpportunityReviewTransitions.WouldRestoreRetentionEligibility(
                    row.Status, request.Status));

            // The change tracker holds no modification yet, but the row was read tracked; clearing
            // keeps a later save on this scope from picking up an incidental fixup.
            _db.ChangeTracker.Clear();

            return CoachOpportunityOperatorResult<CoachOpportunityReviewResponse>.Fail(
                CoachOpportunityOperatorStatus.TransitionRefused);
        }

        row.Status = request.Status;
        row.ReviewerNoteCode = request.ReviewerNoteCode;
        row.LinkedSpecPath = string.IsNullOrWhiteSpace(request.LinkedSpecPath)
            ? null
            : request.LinkedSpecPath.Trim();
        row.ReviewedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        row.Version++;

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two reviewers, or a reveal racing a review. The row carries a concurrency token so
            // neither write is lost silently; answering "invalid request" tells the caller to
            // re-read rather than surfacing a 500 that looks like a broken server.
            _db.ChangeTracker.Clear();
            _logger.LogInformation(
                "[Coach] An opportunity review lost a concurrency race. Kind={Kind}", row.Kind);

            return CoachOpportunityOperatorResult<CoachOpportunityReviewResponse>.Fail(
                CoachOpportunityOperatorStatus.InvalidRequest);
        }

        var rollup = await SummarizeAsync(row.Fingerprint, cancellationToken).ConfigureAwait(false);

        return CoachOpportunityOperatorResult<CoachOpportunityReviewResponse>.Ok(
            new CoachOpportunityReviewResponse(
                Project(row),
                CoachOpportunityMarkdown.Render(row, rollup)));
    }

    /// <summary>
    /// Reveals the two encrypted messages behind one Product row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only path on this surface that returns learner text, and it is guarded six ways: the
    /// four surface gates, a Product disposition, non-null pointers, the literal acknowledgement,
    /// an owner match (unless the Development-only cross-owner switch is on), and a durable key
    /// ring.
    /// </para>
    /// <para>
    /// <b>The key-ring refusal is not pedantry.</b> Decrypting history against an ephemeral ring
    /// is how rows become permanently unreadable after a restart; refusing to try is the honest
    /// answer, and it keeps this surface from being the thing that reports "unavailable" for
    /// messages that are actually fine on a correctly configured host.
    /// </para>
    /// <para>
    /// <b>Nothing revealed is written back.</b> The row's counter and timestamp change; its
    /// content-free columns do not.
    /// </para>
    /// </remarks>
    public async Task<CoachOpportunityOperatorResult<CoachOpportunityEvidenceResponse>> RevealEvidenceAsync(
        string id,
        CoachOpportunityEvidenceRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!IsCallerAuthorized())
        {
            return CoachOpportunityOperatorResult<CoachOpportunityEvidenceResponse>.Fail(
                CoachOpportunityOperatorStatus.NotAvailable);
        }

        if (request is null
            || !string.Equals(
                request.Acknowledgement,
                CoachOpportunityLimits.EvidenceRevealAcknowledgement,
                StringComparison.Ordinal))
        {
            return CoachOpportunityOperatorResult<CoachOpportunityEvidenceResponse>.Fail(
                CoachOpportunityOperatorStatus.InvalidRequest);
        }

        // Refused before the row is even loaded, so an ephemeral host cannot be used to probe
        // which opportunity identifiers exist.
        if (_keyRingPlan is null || !_keyRingPlan.IsDurable)
        {
            _logger.LogWarning(
                "[Coach] An opportunity evidence reveal was refused: the Data Protection key ring " +
                "is not durable. {KeyRing}",
                _keyRingPlan?.Describe() ?? "Mode=HostDefault");

            return CoachOpportunityOperatorResult<CoachOpportunityEvidenceResponse>.Fail(
                CoachOpportunityOperatorStatus.EphemeralKeyRing);
        }

        var row = await _db.CoachOpportunities
            .FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (row is null || row.Disposition != CoachOpportunityDisposition.Product)
        {
            return CoachOpportunityOperatorResult<CoachOpportunityEvidenceResponse>.Fail(
                CoachOpportunityOperatorStatus.NotAvailable);
        }

        _userScope.TryGetUserProfileId(out var callerId);
        var crossOwner = !string.Equals(callerId, row.UserProfileId, StringComparison.Ordinal);

        if (crossOwner && !_options.CurrentValue.OperatorSurface.AllowCrossOwnerEvidence)
        {
            _logger.LogWarning(
                "[Coach] A cross-owner opportunity evidence reveal was refused. Kind={Kind} " +
                "Capability={CapabilityCode}",
                row.Kind,
                row.CapabilityCode);

            return CoachOpportunityOperatorResult<CoachOpportunityEvidenceResponse>.Fail(
                CoachOpportunityOperatorStatus.CrossOwnerRefused);
        }

        if (string.IsNullOrWhiteSpace(row.ConversationId)
            || (string.IsNullOrWhiteSpace(row.EvidenceMessageId)
                && string.IsNullOrWhiteSpace(row.EvidenceOfferMessageId)))
        {
            return CoachOpportunityOperatorResult<CoachOpportunityEvidenceResponse>.Ok(
                new CoachOpportunityEvidenceResponse(
                    row.Id, CoachOpportunityEvidenceState.NotApplicable, null, null, crossOwner,
                    row.EvidenceRevealCount));
        }

        var (learnerText, coachText) = await ResolveAsync(row, cancellationToken).ConfigureAwait(false);
        var state = learnerText is null && coachText is null
            ? CoachOpportunityEvidenceState.Unavailable
            : CoachOpportunityEvidenceState.Available;

        row.EvidenceRevealCount++;
        row.EvidenceLastRevealedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        row.Version++;

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The reveal already read the messages, so refusing now would be dishonest — the
            // content left the store either way. The counter is what lost the race, so the
            // reveal is reported and the miscount is logged rather than hidden.
            _db.ChangeTracker.Clear();
            _logger.LogWarning(
                "[Coach] An opportunity evidence reveal could not record its own audit counter. " +
                "OpportunityId={OpportunityId} CrossOwner={CrossOwner}",
                row.Id,
                crossOwner);
        }

        // Content-free. The counter is on the row that was read, so the audit and the thing
        // audited cannot drift apart, and no learner text reaches the log.
        _logger.LogInformation(
            "[Coach] Opportunity evidence revealed. OpportunityId={OpportunityId} Kind={Kind} " +
            "CrossOwner={CrossOwner} RevealCount={RevealCount} State={EvidenceState}",
            row.Id,
            row.Kind,
            crossOwner,
            row.EvidenceRevealCount,
            state);

        return CoachOpportunityOperatorResult<CoachOpportunityEvidenceResponse>.Ok(
            new CoachOpportunityEvidenceResponse(
                row.Id, state, learnerText, coachText, crossOwner, row.EvidenceRevealCount));
    }

    /// <summary>The rollup line for one fingerprint, used to render a decision record.</summary>
    private async Task<CoachOpportunityRollupDto?> SummarizeAsync(
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var rows = await _db.CoachOpportunities
            .AsNoTracking()
            .Where(row => row.Fingerprint == fingerprint)
            .Select(row => new
            {
                row.UserProfileId,
                row.Kind,
                row.Disposition,
                row.CapabilityCode,
                row.ToolName,
                row.FailureCode,
                row.OfferLink,
                row.OccurrenceCount,
                row.FirstObservedAtUtc,
                row.LastObservedAtUtc,
                row.Status
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return null;
        }

        var first = rows[0];

        return new CoachOpportunityRollupDto(
            fingerprint,
            first.Kind.ToString(),
            first.Disposition.ToString(),
            first.CapabilityCode,
            first.ToolName,
            first.FailureCode,
            first.OfferLink.ToString(),
            rows.Sum(row => row.OccurrenceCount),
            rows.Select(row => row.UserProfileId).Distinct(StringComparer.Ordinal).Count(),
            rows.Count,
            rows.Min(row => row.FirstObservedAtUtc),
            rows.Max(row => row.LastObservedAtUtc),
            [.. rows.Select(row => row.Status.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Reads the two pointed-at messages through the owner-scoped, encrypted message store.
    /// </summary>
    /// <remarks>
    /// The owner is built from the <b>row's</b> <c>UserProfileId</c>, so the content protector's
    /// purpose chain does the enforcement rather than this method's own filter. A pointer copied
    /// onto another row simply fails to decrypt.
    /// </remarks>
    private async Task<(string? LearnerText, string? CoachText)> ResolveAsync(
        CoachOpportunity row,
        CancellationToken cancellationToken)
    {
        if (_messages is null || string.IsNullOrWhiteSpace(row.ConversationId))
        {
            return (null, null);
        }

        if (!CoachOwner.TryCreate(row.UserProfileId, row.TenantId, out var owner))
        {
            return (null, null);
        }

        var sequences = new[] { row.EvidenceMessageSequence, row.EvidenceOfferMessageSequence }
            .Where(sequence => sequence.HasValue)
            .Select(sequence => sequence!.Value)
            .ToList();

        if (sequences.Count == 0)
        {
            return (null, null);
        }

        var page = await _messages
            .GetRangeAsync(owner, row.ConversationId, sequences.Min(), sequences.Max(), cancellationToken)
            .ConfigureAwait(false);

        if (page.Status != CoachHistoryStatus.Success)
        {
            // Fails closed and quietly. A deleted conversation is not an error on this surface —
            // the ledger row is still a valid product signal without its evidence.
            return (null, null);
        }

        var learner = page.Items.FirstOrDefault(item =>
            string.Equals(item.Id, row.EvidenceMessageId, StringComparison.Ordinal));
        var coach = page.Items.FirstOrDefault(item =>
            string.Equals(item.Id, row.EvidenceOfferMessageId, StringComparison.Ordinal));

        return (learner?.Payload?.Text, coach?.Payload?.Text);
    }

    private static CoachOpportunityRowDto Project(
        CoachOpportunity row,
        CoachOpportunityReportFactsDto? report = null,
        CoachOpportunityReportRollupDto? reportRollup = null) =>
        new(row.Id,
            row.Kind.ToString(),
            row.Disposition.ToString(),
            row.Surface.ToString(),
            row.CapabilityCode,
            row.ToolName,
            row.RiskClass?.ToString(),
            row.FailureCode,
            row.StopReason?.ToString(),
            row.OfferLink.ToString(),
            row.Fingerprint,
            row.DedupBucketDate,
            row.OccurrenceCount,
            row.FirstObservedAtUtc,
            row.LastObservedAtUtc,
            row.Status.ToString(),
            row.ReviewedAtUtc,
            row.ReviewerNoteCode?.ToString(),
            row.LinkedSpecPath,
            // A boolean, not an identifier. A listing has no use for a message id, and a response
            // that carried one would be a place for it to be copied somewhere less careful.
            !string.IsNullOrWhiteSpace(row.EvidenceMessageId)
                || !string.IsNullOrWhiteSpace(row.EvidenceOfferMessageId),
            row.EvidenceRevealCount,
            row.EvidenceLastRevealedAtUtc,
            row.SchemaVersion,
            report,
            reportRollup);
}
