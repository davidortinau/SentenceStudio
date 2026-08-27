using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Endpoints;

/// <summary>
/// The durable conversation surface at <c>/api/v1/coach/conversations</c>.
/// </summary>
/// <remarks>
/// <para>
/// These routes are the permanent home of coach history. The older <c>/sessions</c> routes remain
/// as compatibility aliases for one release: a session is a 24-hour checkpoint, and a checkpoint
/// expiring must not read to a learner as the conversation never happening.
/// </para>
/// <para>
/// Like the rest of the coach surface, these handlers translate and nothing else. Ownership,
/// idempotency, leases, and cancellation all live in <see cref="ICoachConversationService"/>, so
/// a route can never be the thing that decides who may read what.
/// </para>
/// </remarks>
public static class CoachConversationEndpoints
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapCoachConversations(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/coach/conversations").RequireAuthorization();

        group.MapPost("/", CreateAsync).WithName("CreateCoachConversation");
        group.MapGet("/", ListAsync).WithName("ListCoachConversations");
        group.MapGet("/{conversationId}", GetAsync).WithName("GetCoachConversation");
        group.MapGet("/{conversationId}/messages", GetMessagesAsync).WithName("GetCoachConversationMessages");
        group.MapPatch("/{conversationId}", UpdateAsync).WithName("UpdateCoachConversation");
        group.MapPost("/{conversationId}/turns", SubmitTurnAsync).WithName("SubmitCoachConversationTurn");

        group.MapGet("/{conversationId}/operations/{operationId}", GetOperationAsync)
            .WithName("GetCoachTurnOperation");

        group.MapPost("/{conversationId}/operations/{operationId}/cancel", CancelOperationAsync)
            .WithName("CancelCoachTurnOperation");

        // The write-approval surface. Every route here is a learner action on an authenticated
        // request: this is the boundary the model cannot cross, which is what makes a proposal
        // safe to produce in the first place.
        group.MapGet("/{conversationId}/writes/{operationId}", GetWriteAsync)
            .WithName("GetCoachWriteOperation");

        group.MapGet("/{conversationId}/writes/{operationId}/receipt", GetWriteReceiptAsync)
            .WithName("GetCoachWriteReceipt");

        group.MapPost("/{conversationId}/writes/{operationId}/accept", AcceptWriteAsync)
            .WithName("AcceptCoachWriteOperation");

        group.MapPost("/{conversationId}/writes/{operationId}/confirmation", IssueWriteConfirmationAsync)
            .WithName("IssueCoachWriteConfirmation");

        group.MapPost("/{conversationId}/writes/{operationId}/confirm", ConfirmWriteAsync)
            .WithName("ConfirmCoachWriteOperation");

        group.MapPost("/{conversationId}/writes/{operationId}/reject", RejectWriteAsync)
            .WithName("RejectCoachWriteOperation");

        group.MapPost("/{conversationId}/writes/{operationId}/undo", UndoWriteAsync)
            .WithName("UndoCoachWriteOperation");

        group.MapDelete("/{conversationId}", DeleteAsync).WithName("DeleteCoachConversation");
        group.MapGet("/{conversationId}/export", ExportAsync).WithName("ExportCoachConversation");

        return app;
    }

    private static Task<IResult> GetWriteAsync(
        string conversationId,
        string operationId,
        [FromServices] ICoachWriteApprovalService writes,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => writes.GetAsync(conversationId, operationId, cancellationToken),
            loggerFactory,
            "GET /api/v1/coach/conversations/{conversationId}/writes/{operationId}");

    private static Task<IResult> GetWriteReceiptAsync(
        string conversationId,
        string operationId,
        [FromServices] ICoachWriteApprovalService writes,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => writes.GetReceiptAsync(conversationId, operationId, cancellationToken),
            loggerFactory,
            "GET /api/v1/coach/conversations/{conversationId}/writes/{operationId}/receipt");

    /// <summary>
    /// The learner accepts a soft write. This is the acceptance the proposal was waiting for.
    /// </summary>
    /// <remarks>
    /// There is no request body, and that is deliberate. The operation already holds the arguments
    /// it was proposed with; letting the caller restate them here would create a second version of
    /// the truth and a window where the accepted thing is not the previewed thing.
    /// </remarks>
    private static Task<IResult> AcceptWriteAsync(
        string conversationId,
        string operationId,
        [FromServices] ICoachWriteApprovalService writes,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => writes.AcceptAsync(conversationId, operationId, cancellationToken),
            loggerFactory,
            "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/accept");

    /// <summary>
    /// Mints the one-use secret a protected write needs.
    /// </summary>
    /// <remarks>
    /// The response carries the only copy of the secret; the server keeps a digest. Because this
    /// route is reachable only by the authenticated learner and never by a tool, holding the
    /// secret is itself evidence that the learner asked.
    /// </remarks>
    private static Task<IResult> IssueWriteConfirmationAsync(
        string conversationId,
        string operationId,
        [FromServices] ICoachWriteApprovalService writes,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => writes.IssueConfirmationAsync(conversationId, operationId, cancellationToken),
            loggerFactory,
            "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/confirmation");

    /// <summary>
    /// Carries out a protected write, given the one-use secret minted for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The secret arrives as a request header rather than a body field. Two reasons, and the
    /// second is the one that decided it. A header keeps the value out of the shared contracts
    /// assembly, where the embargo scanner refuses any member that names a credential — a rule
    /// with no exceptions, and one worth keeping that way. It also keeps the value out of request
    /// bodies, which are the part of a request most likely to be logged, replayed in a trace, or
    /// retained by an intermediary.
    /// </para>
    /// <para>
    /// Nothing else is accepted here. Restating the arguments would create a second version of the
    /// truth and a window in which the confirmed change differs from the previewed one; the
    /// server already holds the canonical arguments the secret was bound to.
    /// </para>
    /// </remarks>
    private static Task<IResult> ConfirmWriteAsync(
        string conversationId,
        string operationId,
        [FromHeader(Name = CoachWriteHeaders.Confirmation)] string? confirmation,
        [FromServices] ICoachWriteApprovalService writes,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => writes.ConfirmAsync(conversationId, operationId, confirmation, cancellationToken),
            loggerFactory,
            "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/confirm");

    private static Task<IResult> RejectWriteAsync(
        string conversationId,
        string operationId,
        [FromServices] ICoachWriteApprovalService writes,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => writes.RejectAsync(conversationId, operationId, cancellationToken),
            loggerFactory,
            "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/reject");

    private static Task<IResult> UndoWriteAsync(
        string conversationId,
        string operationId,
        [FromServices] ICoachWriteApprovalService writes,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => writes.UndoAsync(conversationId, operationId, cancellationToken),
            loggerFactory,
            "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/undo");

    private static Task<IResult> CreateAsync(
        StartCoachConversationRequest? request,
        HttpContext http,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => conversations.CreateAsync(
                WithHeaderKey(request ?? new StartCoachConversationRequest(), http),
                cancellationToken),
            loggerFactory,
            "POST /api/v1/coach/conversations");

    private static Task<IResult> ListAsync(
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => conversations.ListAsync(pageSize, cursor, cancellationToken),
            loggerFactory,
            "GET /api/v1/coach/conversations");

    private static Task<IResult> GetAsync(
        string conversationId,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => conversations.GetAsync(conversationId, cancellationToken),
            loggerFactory,
            "GET /api/v1/coach/conversations/{id}");

    private static Task<IResult> GetMessagesAsync(
        string conversationId,
        [FromQuery] int? pageSize,
        [FromQuery] string? before,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => conversations.GetMessagesAsync(conversationId, pageSize, before, cancellationToken),
            loggerFactory,
            "GET /api/v1/coach/conversations/{id}/messages");

    private static Task<IResult> UpdateAsync(
        string conversationId,
        UpdateCoachConversationRequest? request,
        HttpContext http,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => conversations.UpdateAsync(
                conversationId,
                WithIfMatch(request ?? new UpdateCoachConversationRequest(), http),
                cancellationToken),
            loggerFactory,
            "PATCH /api/v1/coach/conversations/{id}");

    private static Task<IResult> SubmitTurnAsync(
        string conversationId,
        CoachConversationTurnRequest? request,
        HttpContext http,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => request is null
                ? Task.FromResult(CoachOperationResult<CoachTurnOperationDto>.Problem(
                    CoachOperationStatus.InvalidInput,
                    CoachProblemTypes.InvalidTurnInput,
                    "A turn body is required."))
                : conversations.SubmitTurnAsync(conversationId, WithHeaderKey(request, http), cancellationToken),
            loggerFactory,
            "POST /api/v1/coach/conversations/{id}/turns");

    private static Task<IResult> GetOperationAsync(
        string conversationId,
        string operationId,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => conversations.GetOperationAsync(conversationId, operationId, cancellationToken),
            loggerFactory,
            "GET /api/v1/coach/conversations/{id}/operations/{operationId}");

    private static Task<IResult> CancelOperationAsync(
        string conversationId,
        string operationId,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => conversations.CancelOperationAsync(conversationId, operationId, cancellationToken),
            loggerFactory,
            "POST /api/v1/coach/conversations/{id}/operations/{operationId}/cancel");

    private static Task<IResult> DeleteAsync(
        string conversationId,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => conversations.DeleteAsync(conversationId, cancellationToken),
            loggerFactory,
            "DELETE /api/v1/coach/conversations/{id}",
            noContentOnSuccess: true);

    /// <summary>
    /// Streams one owned conversation as JSON or Markdown.
    /// </summary>
    /// <remarks>
    /// The response is written straight from the database cursor. Nothing is buffered to a temp
    /// file and no server-side export job exists, so an abandoned download leaves no residue and
    /// there is no export artifact for a later request to read someone else's history out of.
    /// </remarks>
    private static async Task<IResult> ExportAsync(
        string conversationId,
        [FromQuery] CoachExportFormat? format,
        [FromServices] ICoachConversationService conversations,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var opened = await conversations.OpenExportAsync(conversationId, cancellationToken);
            if (!opened.IsOk || opened.Value is null)
            {
                return CoachEndpointExecution.ToProblem(opened.Status, opened.ProblemType, opened.Detail);
            }

            var export = opened.Value;
            var markdown = format == CoachExportFormat.Markdown;

            return Results.Stream(
                async stream =>
                {
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                    if (markdown)
                    {
                        await WriteMarkdownAsync(writer, export, cancellationToken);
                    }
                    else
                    {
                        await WriteJsonAsync(writer, export, cancellationToken);
                    }
                },
                markdown ? "text/markdown; charset=utf-8" : "application/json; charset=utf-8",
                $"coach-conversation-{conversationId}.{(markdown ? "md" : "json")}");
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            var facts = Telemetry.CoachExceptionSanitizer.Describe(ex);
            loggerFactory.CreateLogger("Coach").LogError(
                "GET /api/v1/coach/conversations/{{id}}/export failed. Category={FailureCategory} InnerDepth={InnerDepth}",
                facts.Category,
                facts.InnerDepth);

            return Results.Problem(detail: "The coach export failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task WriteJsonAsync(
        StreamWriter writer,
        CoachConversationExport export,
        CancellationToken cancellationToken)
    {
        var header = CoachHistoryProjection.ToConversation(export.Conversation, hasActiveCheckpoint: false);

        await writer.WriteAsync("{\"conversation\":");
        await writer.WriteAsync(JsonSerializer.Serialize(header, ExportJson));
        await writer.WriteAsync(",\"messages\":[");

        var first = true;
        await foreach (var message in export.Messages.WithCancellation(cancellationToken))
        {
            if (!first)
            {
                await writer.WriteAsync(',');
            }

            first = false;
            await writer.WriteAsync(
                JsonSerializer.Serialize(CoachHistoryProjection.ToHistoryMessage(message), ExportJson));

            // Flush per message rather than per buffer: an export of a long conversation should
            // start arriving immediately, not after the whole thread has been read.
            await writer.FlushAsync(cancellationToken);
        }

        await writer.WriteAsync("]}");
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task WriteMarkdownAsync(
        StreamWriter writer,
        CoachConversationExport export,
        CancellationToken cancellationToken)
    {
        var header = CoachHistoryProjection.ToConversation(export.Conversation, hasActiveCheckpoint: false);

        await writer.WriteLineAsync($"# {header.Title}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"_Started {header.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC_");
        await writer.WriteLineAsync();

        await foreach (var message in export.Messages.WithCancellation(cancellationToken))
        {
            var dto = CoachHistoryProjection.ToHistoryMessage(message);
            var speaker = dto.Message.Role == CoachMessageRole.Learner ? "You" : "Coach";

            await writer.WriteLineAsync($"### {speaker} — {dto.Message.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync(
                dto.IsReadable && !string.IsNullOrWhiteSpace(dto.Message.Text)
                    ? dto.Message.Text
                    : "_(this message could not be read)_");
            await writer.WriteLineAsync();
            await writer.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Lets the idempotency key arrive in the standard <c>Idempotency-Key</c> header as well as
    /// in the body, so a generic HTTP client can retry safely without knowing the coach's shape.
    /// The body wins when both are present; disagreement is the client's to resolve, not ours.
    /// </summary>
    private static StartCoachConversationRequest WithHeaderKey(
        StartCoachConversationRequest request, HttpContext http) =>
        string.IsNullOrWhiteSpace(request.IdempotencyKey) && HeaderKey(http) is { } key
            ? request with { IdempotencyKey = key }
            : request;

    private static CoachConversationTurnRequest WithHeaderKey(
        CoachConversationTurnRequest request, HttpContext http) =>
        string.IsNullOrWhiteSpace(request.IdempotencyKey) && HeaderKey(http) is { } key
            ? request with { IdempotencyKey = key }
            : request;

    /// <summary>
    /// Accepts the expected state version from an <c>If-Match</c> header as well as the body, so
    /// the route behaves like the conditional request clients already understand.
    /// </summary>
    private static UpdateCoachConversationRequest WithIfMatch(
        UpdateCoachConversationRequest request, HttpContext http)
    {
        if (request.ExpectedStateVersion is not null)
        {
            return request;
        }

        var raw = http.Request.Headers.IfMatch.ToString().Trim().Trim('"', 'W', '/');
        return long.TryParse(raw, out var version)
            ? request with { ExpectedStateVersion = version }
            : request;
    }

    private static string? HeaderKey(HttpContext http)
    {
        var value = http.Request.Headers["Idempotency-Key"].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
