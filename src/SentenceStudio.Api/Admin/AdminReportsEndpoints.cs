using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Admin;

/// <summary>
/// The feedback inbox: the Sam answers learners flagged and the reports they filed to GitHub.
/// </summary>
/// <remarks>
/// <para>
/// These replace the Development-only <c>/api/v1/coach/operator/opportunities</c> routes, which
/// showed a signed-in developer their own rows. An admin sees every learner's flagged answers
/// (the Captain's decision, 2026-11-15 and 2026-11-16).
/// </para>
/// <para>
/// The lists and the detail carry ids, codes, counts and times. Two routes return learner
/// content: the flagged question and answer, and a GitHub report's saved screen state. Both are
/// POSTs, so they cannot be linked, prefetched or replayed from history; an admin needs the
/// password again for them, and an agent token needs
/// <see cref="AdminTokenPermissions.LearnerContent"/>.
/// </para>
/// </remarks>
internal static class AdminReportsEndpoints
{
    private const string Prefix = "/reports";

    public static RouteGroupBuilder MapAdminReportsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet($"{Prefix}/new-count", GetNewCountAsync)
            .WithName("GetAdminNewReportCount")
            .WithoutAdminAudit("A count of new flagged answers for the sidebar; names no learner and no report.");

        group.MapGet($"{Prefix}/sam", ListSamAsync)
            .WithName("ListAdminSamReports")
            .WithAdminAction(AdminActions.ReportsSamList)
            .AllowAdminToken(AdminTokenPermissions.Reports);

        group.MapGet($"{Prefix}/sam/{{reportId}}", GetSamAsync)
            .WithName("GetAdminSamReport")
            .WithAdminAction(AdminActions.ReportsSamRead, AdminTargetKinds.Report, "reportId")
            .AllowAdminToken(AdminTokenPermissions.Reports);

        group.MapPost($"{Prefix}/sam/{{reportId}}/exchange", OpenSamExchangeAsync)
            .WithName("OpenAdminSamExchange")
            .WithAdminAction(AdminActions.ReportsSamExchangeRead, AdminTargetKinds.Report, "reportId")
            .AllowAdminToken(AdminTokenPermissions.LearnerContent)
            .RequireAdminStepUp();

        group.MapPost($"{Prefix}/sam/{{reportId}}/triage", TriageSamAsync)
            .WithName("TriageAdminSamReport")
            .WithAdminAction(AdminActions.ReportsSamTriage, AdminTargetKinds.Report, "reportId")
            .AllowAdminToken(AdminTokenPermissions.Triage);

        group.MapGet($"{Prefix}/github", ListGitHubAsync)
            .WithName("ListAdminGitHubReports")
            .WithAdminAction(AdminActions.ReportsGitHubList)
            .AllowAdminToken(AdminTokenPermissions.Reports);

        group.MapPost($"{Prefix}/github/screen-states/{{reference}}", ReadScreenStateAsync)
            .WithName("ReadAdminScreenState")
            .WithAdminAction(AdminActions.ReportsScreenStateRead, AdminTargetKinds.Report, "reference")
            .AllowAdminToken(AdminTokenPermissions.LearnerContent)
            .RequireAdminStepUp();

        return group;
    }

    private static async Task<IResult> GetNewCountAsync(
        [FromServices] AdminReportsService reports, CancellationToken cancellationToken) =>
        Results.Ok(new AdminNewReportCount(await reports.CountNewAsync(cancellationToken)));

    private static async Task<IResult> ListSamAsync(
        HttpContext http,
        [FromServices] AdminReportsService reports,
        [FromServices] ApplicationDbContext db,
        CancellationToken cancellationToken,
        [FromQuery] string? status = null,
        [FromQuery] string? take = null)
    {
        // Bound as text and parsed here, so a malformed value is this route's own 400 in every
        // environment rather than the framework's binding failure.
        CoachOpportunityStatus? wanted = null;
        if (status is not null)
        {
            if (!AdminReportCodes.TryParseStatus(status, out var parsed))
            {
                return Invalid(http, "status must be new, reviewed, accepted, deferred or dismissed.");
            }

            wanted = parsed;
        }

        if (!TryParseTake(take, out var pageSize))
        {
            return Invalid(http, $"take must be 1 to {AdminReportsService.MaxPageSize}.");
        }

        var (items, newCount, unlisted, truncated) = await reports.ListSamAsync(wanted, pageSize, cancellationToken);
        var learners = await NamesForAsync(http, db, [.. items.Select(i => i.LearnerId)], cancellationToken);
        return Results.Ok(new AdminSamReportList(items, learners, newCount, unlisted, truncated));
    }

    private static async Task<IResult> GetSamAsync(
        string reportId,
        HttpContext http,
        [FromServices] AdminReportsService reports,
        [FromServices] ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        if (await reports.GetSamAsync(reportId, cancellationToken) is not { } found)
        {
            return Results.NotFound();
        }

        var learners = await NamesForAsync(http, db, [found.Report.LearnerId], cancellationToken);
        return Results.Ok(new AdminSamReportDetail(
            found.Report, learners.FirstOrDefault(), found.Flags, found.FlagsTruncated));
    }

    private static async Task<IResult> OpenSamExchangeAsync(
        string reportId,
        HttpContext http,
        [FromServices] AdminReportsService reports,
        CancellationToken cancellationToken) =>
        ToResult(http, await reports.OpenSamExchangeAsync(reportId, cancellationToken));

    private static async Task<IResult> TriageSamAsync(
        string reportId,
        HttpContext http,
        [FromServices] AdminReportsService reports,
        CancellationToken cancellationToken)
    {
        if (await AdminRequestBody.ReadAsync<AdminSamTriageRequest>(http, cancellationToken) is not { } request)
        {
            return Invalid(http, "The body must be JSON: {\"status\": \"reviewed\", \"issueNumber\": 123}.");
        }

        if (!AdminReportCodes.TryParseStatus(request.Status, out var status))
        {
            return Invalid(http, "status must be new, reviewed, accepted, deferred or dismissed.");
        }

        if (request.IssueNumber is < 1 or > AdminReportsService.MaxIssueNumber)
        {
            return Invalid(http, $"issueNumber must be 1 to {AdminReportsService.MaxIssueNumber}, or left out.");
        }

        return ToResult(http, await reports.TriageSamAsync(reportId, status, request.IssueNumber, cancellationToken));
    }

    private static async Task<IResult> ListGitHubAsync(
        HttpContext http,
        [FromServices] AdminReportsService reports,
        [FromServices] ApplicationDbContext db,
        CancellationToken cancellationToken,
        [FromQuery] string? take = null)
    {
        if (!TryParseTake(take, out var pageSize))
        {
            return Invalid(http, $"take must be 1 to {AdminReportsService.MaxPageSize}.");
        }

        var (items, truncated) = await reports.ListGitHubAsync(pageSize, cancellationToken);
        var learners = await NamesForAsync(http, db, [.. items.Select(i => i.LearnerId)], cancellationToken);
        return Results.Ok(new AdminGitHubReportList(items, learners, truncated));
    }

    private static async Task<IResult> ReadScreenStateAsync(
        string reference,
        [FromServices] AdminReportsService reports,
        CancellationToken cancellationToken) =>
        await reports.ReadScreenStateAsync(reference, cancellationToken) is { } state
            ? Results.Ok(state)
            : Results.NotFound();

    /// <summary>
    /// Names and emails for an admin's page. An agent token gets none: it works from ids, and an
    /// address it never held cannot end up in an issue it writes.
    /// </summary>
    private static async Task<IReadOnlyList<AdminLearnerSummary>> NamesForAsync(
        HttpContext http, ApplicationDbContext db, IReadOnlyList<string> learnerIds, CancellationToken cancellationToken) =>
        http.GetAdminActor().Kind == AdminActorKind.Admin
            ? await AdminUsageEndpoints.NamesAsync(db, [.. learnerIds.Distinct(StringComparer.Ordinal)], cancellationToken)
            : [];

    private static bool TryParseTake(string? take, out int pageSize)
    {
        pageSize = AdminReportsService.DefaultPageSize;
        return take is null
            || (int.TryParse(take, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                && pageSize >= 1 && pageSize <= AdminReportsService.MaxPageSize);
    }

    private static IResult Invalid(HttpContext http, string detail) =>
        AdminRefusal.Create(http, StatusCodes.Status400BadRequest, AdminFailureCodes.InvalidRequest, detail);

    private static IResult ToResult<T>(HttpContext http, AdminReportResult<T> result) => result.Outcome switch
    {
        AdminReportOutcome.Ok => Results.Ok(result.Value),

        // The same answer for an id that names nothing and one that names a row no learner flagged.
        AdminReportOutcome.NotFound => Results.NotFound(),

        AdminReportOutcome.TransitionRefused => AdminRefusal.Create(
            http, StatusCodes.Status409Conflict, AdminFailureCodes.TransitionRefused,
            "An accepted report keeps its status. Read the report again before deciding."),

        AdminReportOutcome.Conflict => AdminRefusal.Create(
            http, StatusCodes.Status409Conflict, AdminFailureCodes.Conflict,
            "The report changed while this was being saved. Read it again and decide again."),

        AdminReportOutcome.KeyRingNotDurable => AdminRefusal.Create(
            http, StatusCodes.Status409Conflict, AdminFailureCodes.KeyRingNotDurable,
            "This host cannot read stored Sam messages reliably, because its key ring is not durable."),

        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
}
