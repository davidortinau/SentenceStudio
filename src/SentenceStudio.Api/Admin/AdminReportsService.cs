using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Security.DataProtection;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Admin;

/// <summary>How a request against one report ended.</summary>
internal enum AdminReportOutcome
{
    Ok = 0,

    /// <summary>No such report, or it is not one a learner's flag created.</summary>
    NotFound,

    /// <summary>The status asked for cannot follow the one the report has.</summary>
    TransitionRefused,

    /// <summary>The report changed while this request was deciding.</summary>
    Conflict,

    /// <summary>This host cannot decrypt stored messages reliably.</summary>
    KeyRingNotDurable
}

internal readonly record struct AdminReportResult<T>(AdminReportOutcome Outcome, T? Value)
{
    public static AdminReportResult<T> Ok(T value) => new(AdminReportOutcome.Ok, value);

    public static AdminReportResult<T> Fail(AdminReportOutcome outcome) => new(outcome, default);
}

/// <summary>
/// The feedback inbox: the Sam answers learners flagged, for every learner, and the reports they
/// filed to GitHub.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only what a learner's own flag created.</b> Every query here is fixed to ledger rows of kind
/// <see cref="CoachOpportunityKind.UserReportedResponse"/>. Rows the server recorded about its
/// own refusals are not listed, cannot be triaged here and can never be opened: an id that names
/// one is answered as though it named nothing.
/// </para>
/// <para>
/// <b>Not scoped to the caller.</b> Unlike every learner-facing service, this one reads across
/// learners, which is what an admin is for. It is reachable only through the admin group, whose
/// policy and audit log stand in front of it. Each read of a message is scoped to the learner the
/// report belongs to, taken from the report's own row.
/// </para>
/// </remarks>
internal sealed class AdminReportsService(
    CoachDbContext coachDb,
    FeedbackDbContext feedbackDb,
    ICoachMessageStore messages,
    TimeProvider time,
    ILogger<AdminReportsService> logger,
    CoachKeyRingPlan? keyRingPlan = null)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>How many flags one report's detail lists.</summary>
    public const int MaxFlagsListed = 50;

    /// <summary>How many flagged exchanges one request opens.</summary>
    public const int MaxExchangesOpened = 10;

    /// <summary>No real issue number is larger; a bigger one is a mistake, not an issue.</summary>
    public const int MaxIssueNumber = 9_999_999;

    private IQueryable<CoachOpportunity> FlaggedRows => coachDb.CoachOpportunities
        .Where(row => row.Kind == CoachOpportunityKind.UserReportedResponse
            && row.Disposition == CoachOpportunityDisposition.Product);

    // ------------------------------------------------------------------ Sam: read

    public async Task<int> CountNewAsync(CancellationToken cancellationToken) =>
        await FlaggedRows.CountAsync(row => row.Status == CoachOpportunityStatus.New, cancellationToken);

    public async Task<(List<AdminSamReportRow> Items, int NewCount, int UnlistedFlagCount, bool Truncated)> ListSamAsync(
        CoachOpportunityStatus? status, int take, CancellationToken cancellationToken)
    {
        var query = FlaggedRows.AsNoTracking();
        if (status is { } wanted)
        {
            query = query.Where(row => row.Status == wanted);
        }

        // One more than asked for, to know whether anything was left out.
        var rows = await query
            .OrderByDescending(row => row.LastObservedAtUtc)
            .ThenBy(row => row.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        var truncated = rows.Count > take;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        // A flag is recorded before its inbox row, and the row is allowed to fail. Such a flag is
        // in no list, so the list says how many there are rather than passing for complete.
        var unlisted = await coachDb.CoachResponseReports.CountAsync(r => r.OpportunityId == null, cancellationToken);

        return ([.. rows.Select(Project)], await CountNewAsync(cancellationToken), unlisted, truncated);
    }

    public async Task<(AdminSamReportRow Report, List<AdminSamReportFlag> Flags, bool FlagsTruncated)?> GetSamAsync(
        string id, CancellationToken cancellationToken)
    {
        var row = await FlaggedRows.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var flags = await FlagsOf(row)
            .OrderByDescending(r => r.ReportedAtUtc)
            .ThenBy(r => r.Id)
            .Take(MaxFlagsListed + 1)
            .ToListAsync(cancellationToken);

        var truncated = flags.Count > MaxFlagsListed;
        if (truncated)
        {
            flags.RemoveAt(flags.Count - 1);
        }

        return (Project(row), [.. flags.Select(ProjectFlag)], truncated);
    }

    // ------------------------------------------------------------------ Sam: triage

    /// <summary>Sets the report's status and its linked issue, both as given.</summary>
    /// <remarks>
    /// The status follows the ledger's own rule (<see cref="CoachOpportunityReviewTransitions"/>):
    /// nothing leaves <c>accepted</c>, because an accepted report is one something else now points
    /// at, and a status the retention sweep ages out would delete it.
    /// </remarks>
    public async Task<AdminReportResult<AdminSamReportRow>> TriageSamAsync(
        string id, CoachOpportunityStatus status, int? issueNumber, CancellationToken cancellationToken)
    {
        var row = await FlaggedRows.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null)
        {
            return AdminReportResult<AdminSamReportRow>.Fail(AdminReportOutcome.NotFound);
        }

        if (!CoachOpportunityReviewTransitions.IsAllowed(row.Status, status))
        {
            return AdminReportResult<AdminSamReportRow>.Fail(AdminReportOutcome.TransitionRefused);
        }

        row.Status = status;
        row.LinkedIssueNumber = issueNumber;
        row.ReviewedAtUtc = time.GetUtcNow().UtcDateTime;
        row.Version++;

        try
        {
            await coachDb.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another triage, an opened exchange or a new flag on the same row got there first.
            // Nothing was written; the caller reads the report again and decides again.
            coachDb.ChangeTracker.Clear();
            return AdminReportResult<AdminSamReportRow>.Fail(AdminReportOutcome.Conflict);
        }

        return AdminReportResult<AdminSamReportRow>.Ok(Project(row));
    }

    // ------------------------------------------------------------------ Sam: the flagged exchange

    /// <summary>
    /// Reads the question and answer behind each flag on one report. The only place a Sam
    /// conversation leaves the coach's store for anyone but its learner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The messages are found by the pointers the learner's flag stored, and read through the
    /// coach's own message store as the learner the report belongs to. Nothing else in the
    /// conversation is read, and nothing read is written anywhere.
    /// </para>
    /// <para>
    /// The count on the report goes up before anything is decrypted. If that write fails the
    /// request fails and nothing is read.
    /// </para>
    /// </remarks>
    public async Task<AdminReportResult<AdminSamExchangeResponse>> OpenSamExchangeAsync(
        string id, CancellationToken cancellationToken)
    {
        // Before the row is read, so a host that cannot decrypt is no way to learn which ids exist.
        if (keyRingPlan is null || !keyRingPlan.IsDurable)
        {
            logger.LogWarning(
                "A flagged exchange was not opened: the Data Protection key ring is not durable. {KeyRing}",
                keyRingPlan?.Describe() ?? "Mode=HostDefault");
            return AdminReportResult<AdminSamExchangeResponse>.Fail(AdminReportOutcome.KeyRingNotDurable);
        }

        var row = await FlaggedRows.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null)
        {
            return AdminReportResult<AdminSamExchangeResponse>.Fail(AdminReportOutcome.NotFound);
        }

        var pointers = await FlagsOf(row)
            .OrderByDescending(r => r.ReportedAtUtc)
            .ThenBy(r => r.Id)
            .Take(MaxExchangesOpened + 1)
            .Select(r => new ExchangePointer(
                r.ConversationId, r.RequestMessageId, r.RequestMessageSequence,
                r.CoachMessageId, r.CoachMessageSequence, r.ReportedAtUtc))
            .ToListAsync(cancellationToken);

        var truncated = pointers.Count > MaxExchangesOpened;
        if (truncated)
        {
            pointers.RemoveAt(pointers.Count - 1);
        }

        // The flags are pruned after 180 days and a decided report is kept. With none left, the
        // report's own pointers still name the first exchange flagged.
        if (pointers.Count == 0 && HasExchange(row))
        {
            pointers.Add(new ExchangePointer(
                row.ConversationId!, row.EvidenceMessageId, row.EvidenceMessageSequence,
                row.EvidenceOfferMessageId, row.EvidenceOfferMessageSequence, FlaggedAtUtc: null));
        }

        var now = time.GetUtcNow().UtcDateTime;
        var counted = await coachDb.CoachOpportunities
            .Where(r => r.Id == row.Id)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(r => r.EvidenceRevealCount, r => r.EvidenceRevealCount + 1)
                    .SetProperty(r => r.EvidenceLastRevealedAtUtc, (DateTime?)now)
                    .SetProperty(r => r.Version, r => r.Version + 1),
                cancellationToken);
        if (counted == 0)
        {
            // Erased between the read and the count (the learner deleted their account).
            return AdminReportResult<AdminSamExchangeResponse>.Fail(AdminReportOutcome.NotFound);
        }

        var exchanges = new List<AdminSamExchange>(pointers.Count);
        foreach (var pointer in pointers)
        {
            var (question, answer) = await ReadAsync(row, pointer, cancellationToken);
            exchanges.Add(new AdminSamExchange(pointer.FlaggedAtUtc, question, answer));
        }

        var state = pointers.Count == 0
            ? AdminReportCodes.ExchangeNotApplicable
            : exchanges.Any(e => e.Question is not null || e.Answer is not null)
                ? AdminReportCodes.ExchangeAvailable
                : AdminReportCodes.ExchangeUnavailable;

        // Ids, a count and a code. Nothing a learner or Sam wrote reaches the log.
        logger.LogInformation(
            "Flagged exchange opened. ReportId={ReportId} Exchanges={ExchangeCount} State={State}",
            row.Id, exchanges.Count, state);

        return AdminReportResult<AdminSamExchangeResponse>.Ok(
            new AdminSamExchangeResponse(row.Id, state, exchanges, truncated, row.EvidenceRevealCount + 1));
    }

    private sealed record ExchangePointer(
        string ConversationId,
        string? QuestionId,
        long? QuestionSequence,
        string? AnswerId,
        long? AnswerSequence,
        DateTime? FlaggedAtUtc);

    private async Task<(string? Question, string? Answer)> ReadAsync(
        CoachOpportunity row, ExchangePointer pointer, CancellationToken cancellationToken)
    {
        if (!CoachOwner.TryCreate(row.UserProfileId, row.TenantId, out var owner))
        {
            return (null, null);
        }

        long[] sequences =
        [
            .. new[] { pointer.QuestionSequence, pointer.AnswerSequence }
                .Where(sequence => sequence.HasValue)
                .Select(sequence => sequence!.Value)
        ];
        if (sequences.Length == 0)
        {
            return (null, null);
        }

        var page = await messages.GetRangeAsync(
            owner, pointer.ConversationId, sequences.Min(), sequences.Max(), cancellationToken);

        // A conversation the learner deleted is not an error here: the report still stands
        // without its exchange, and the answer says the exchange is unavailable.
        if (page.Status != CoachHistoryStatus.Success)
        {
            return (null, null);
        }

        // Matched by id, never by position: the range can hold other messages of the same turn.
        var question = page.Items.FirstOrDefault(m => string.Equals(m.Id, pointer.QuestionId, StringComparison.Ordinal));
        var answer = page.Items.FirstOrDefault(m => string.Equals(m.Id, pointer.AnswerId, StringComparison.Ordinal));
        return (question?.Payload?.Text, answer?.Payload?.Text);
    }

    // ------------------------------------------------------------------ GitHub reports

    public async Task<(List<AdminGitHubReportRow> Items, bool Truncated)> ListGitHubAsync(
        int take, CancellationToken cancellationToken)
    {
        var submissions = await feedbackDb.FeedbackSubmissions.AsNoTracking()
            .OrderByDescending(s => s.CreatedAtUtc)
            .ThenBy(s => s.Jti)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        var truncated = submissions.Count > take;
        if (truncated)
        {
            submissions.RemoveAt(submissions.Count - 1);
        }

        var jtis = submissions.Select(s => s.Jti).ToList();
        var references = jtis.Count == 0
            ? []
            : await feedbackDb.FeedbackScreenSnapshots.AsNoTracking()
                .Where(snapshot => jtis.Contains(snapshot.Jti))
                .ToDictionaryAsync(snapshot => snapshot.Jti, snapshot => snapshot.Reference, cancellationToken);

        // The token nonce that keys a submission is not returned: nothing an admin does needs it.
        var items = submissions
            .Select(s => new AdminGitHubReportRow(
                AdminReportCodes.Of(s.Status),
                s.IssueNumber,
                s.IssueUrl,
                s.IssueTitle,
                s.FailureCode,
                s.UserProfileId,
                AdminReportCodes.Describe(s.Platform),
                s.AppVersion,
                AdminReportCodes.Describe(s.RouteCategory),
                s.CreatedAtUtc,
                references.GetValueOrDefault(s.Jti)))
            .ToList();

        return (items, truncated);
    }

    /// <summary>The private screen state saved with one GitHub report, or null when there is none.</summary>
    public async Task<AdminScreenStateResponse?> ReadScreenStateAsync(string reference, CancellationToken cancellationToken)
    {
        var snapshot = await feedbackDb.FeedbackScreenSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Reference == reference, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        // Stored by the normaliser, which only ever writes JSON. One that does not parse is a
        // fault to surface, not an empty answer.
        using var document = JsonDocument.Parse(snapshot.ContextJson);
        return new AdminScreenStateResponse(
            snapshot.Reference, snapshot.UserProfileId, snapshot.CreatedAtUtc, document.RootElement.Clone());
    }

    // ------------------------------------------------------------------ shapes

    private IQueryable<CoachResponseReport> FlagsOf(CoachOpportunity row) =>
        coachDb.CoachResponseReports.AsNoTracking()
            .Where(r => r.UserProfileId == row.UserProfileId && r.OpportunityId == row.Id);

    private static bool HasExchange(CoachOpportunity row) =>
        !string.IsNullOrWhiteSpace(row.ConversationId)
        && (!string.IsNullOrWhiteSpace(row.EvidenceMessageId) || !string.IsNullOrWhiteSpace(row.EvidenceOfferMessageId));

    private static AdminSamReportRow Project(CoachOpportunity row) =>
        new(row.Id,
            row.UserProfileId,
            AdminReportCodes.Of(row.Status),
            AdminReportCodes.Of(CoachOpportunityCapabilityCodes.ReportReasonOf(row.CapabilityCode)),
            row.OccurrenceCount,
            row.FirstObservedAtUtc,
            row.LastObservedAtUtc,
            row.FailureCode,
            AdminReportCodes.Describe(row.StopReason),
            row.LinkedIssueNumber,
            IssueUrlOf(row.LinkedIssueNumber),
            HasExchange(row),
            row.EvidenceRevealCount,
            row.EvidenceLastRevealedAtUtc,
            row.ReviewedAtUtc);

    private static AdminSamReportFlag ProjectFlag(CoachResponseReport flag) =>
        new(flag.ReportedAtUtc,
            AdminReportCodes.Of(flag.Reason),
            AdminReportCodes.Describe(flag.ResponseKind),
            AdminReportCodes.Describe(flag.TurnStatus),
            flag.TurnAttemptCount,
            flag.TurnErrorCode,
            flag.InvokedToolNames,
            AdminReportCodes.Describe(flag.WriteStatus),
            flag.WriteFailureCode,
            AdminReportCodes.Describe((CoachGroundingStage?)flag.GroundingStage),
            flag.GroundingRefused,
            flag.GroundingAltered,
            flag.GroundingRepairSuppressed,
            flag.GroundingFindingCount,
            flag.GroundingRuleCodes,
            AdminReportCodes.Describe((CoachLimitationCode?)flag.GroundingLimitationCode));

    /// <summary>Where a linked issue lives: the repository the app files its own reports to.</summary>
    internal static string? IssueUrlOf(int? issueNumber) =>
        issueNumber is { } number ? $"https://github.com/{FeedbackIssueFiler.GitHubRepo}/issues/{number}" : null;
}
