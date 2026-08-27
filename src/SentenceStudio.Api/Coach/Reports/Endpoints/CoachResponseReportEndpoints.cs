using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Api.Coach.Endpoints;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Reports.Endpoints;

/// <summary>
/// The learner's response-report surface, under
/// <c>/api/v1/coach/conversations/{conversationId}/responses</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every route here is a learner action on an authenticated request.</b> This is the same
/// boundary the write-approval routes sit on, and for the same reason: a control the model cannot
/// reach is the only kind of control whose result means anything. Nothing in the tool registry
/// names these routes, no agent holds a client for them, and the owner is derived from the
/// request scope rather than accepted from a body — so a report in the ledger is evidence that a
/// person pressed a button, not that a model decided to file one about itself.
/// </para>
/// <para>
/// Mapped unconditionally and gated per request on <c>Coach:Reports:Enabled</c>, which answers
/// 404 when off. That is deliberate: the flag can then be flipped without a redeploy, and a
/// disabled feature is indistinguishable from an unknown route rather than advertising itself
/// with a 403.
/// </para>
/// </remarks>
public static class CoachResponseReportEndpoints
{
    /// <summary>The route prefix these endpoints hang from.</summary>
    public const string RoutePrefix = "/api/v1/coach/conversations";

    /// <summary>Maps the learner's report routes.</summary>
    public static IEndpointRouteBuilder MapCoachResponseReports(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(RoutePrefix).RequireAuthorization();

        group.MapGet("/{conversationId}/responses/reported", ListReportedAsync)
            .WithName("ListReportedCoachResponses");

        group.MapPost("/{conversationId}/responses/{messageId}/report", ReportAsync)
            .WithName("ReportCoachResponse");

        return app;
    }

    /// <summary>
    /// Which of this conversation's coach responses the learner has already reported.
    /// </summary>
    /// <remarks>
    /// Answers an empty list for an unknown conversation, a foreign one, and a real one with
    /// nothing reported. A caller cannot tell those apart, which is exactly the point: a route
    /// that answered "not found" for one of them would be an existence oracle for conversation
    /// identifiers.
    /// </remarks>
    private static Task<IResult> ListReportedAsync(
        string conversationId,
        [FromServices] CoachResponseReportService reports,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => reports.ListReportedAsync(conversationId, cancellationToken),
            loggerFactory,
            "GET /api/v1/coach/conversations/{conversationId}/responses/reported");

    /// <summary>
    /// Reports one coach response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reported response is named in the path and the request it answered is named in the
    /// body, because both are needed and only one of them is the resource. The body carries no
    /// text and has nowhere to put any: the server reads the exchange out of its own encrypted
    /// ledger rather than being told what it said.
    /// </para>
    /// <para>
    /// A repeat is a success, not a conflict. Reporting the same response twice — two devices, a
    /// double press, a reload — answers 200 with
    /// <see cref="CoachResponseReportState.AlreadyReported"/>, because the learner's intent was
    /// satisfied the first time and a 409 would ask them to fix something that is not broken.
    /// </para>
    /// </remarks>
    private static Task<IResult> ReportAsync(
        string conversationId,
        string messageId,
        [FromBody] CoachResponseReportRequest request,
        [FromServices] CoachResponseReportService reports,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => reports.ReportAsync(conversationId, messageId, request, cancellationToken),
            loggerFactory,
            "POST /api/v1/coach/conversations/{conversationId}/responses/{messageId}/report");
}
