using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.Compatibility;
using SentenceStudio.Api.Coach.Memory.Endpoints;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Endpoints;

/// <summary>
/// The authenticated <c>/api/v1/coach</c> surface.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints are a thin translation layer: they resolve nothing, decide nothing, and write
/// nothing. Every rule lives in <see cref="ICoachSessionService"/>.
/// </para>
/// <para>
/// The group uses the host's existing authentication, so a missing or invalid token returns
/// the same 401 as every other route. Once authenticated, a learner who is outside the cohort,
/// or a learner asking about a session they do not own, gets 404 — the coach never confirms
/// that something exists but is off-limits.
/// </para>
/// </remarks>
public static class CoachEndpoints
{
    public static IEndpointRouteBuilder MapCoach(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/coach").RequireAuthorization();

        group.MapGet("/availability", GetAvailabilityAsync)
            .WithName("GetCoachAvailability");

        group.MapPost("/sessions", StartSessionAsync)
            .WithName("StartCoachSession");

        group.MapGet("/sessions/{sessionId}", GetSessionAsync)
            .WithName("GetCoachSession");

        group.MapPost("/sessions/{sessionId}/turns", SubmitTurnAsync)
            .WithName("SubmitCoachTurn");

        group.MapPost("/sessions/{sessionId}/suggestions/{suggestionId}/accept", AcceptSuggestionAsync)
            .WithName("AcceptCoachSuggestion");

        group.MapPost("/sessions/{sessionId}/suggestions/{suggestionId}/reject", RejectSuggestionAsync)
            .WithName("RejectCoachSuggestion");

        group.MapPost("/sessions/{sessionId}/undo", UndoAsync)
            .WithName("UndoCoachRevision");

        // Stop. Without this the learner can abandon a slow turn in the UI but the run keeps
        // holding their single concurrency slot until it times out.
        group.MapPost("/sessions/{sessionId}/cancel", CancelAsync)
            .WithName("CancelCoachRun");

        group.MapDelete("/sessions/{sessionId}", DeleteSessionAsync)
            .WithName("DeleteCoachSession");

        // Durable history lives on its own resource. The /sessions routes above stay for one
        // release as aliases over the same conversation, so an in-flight client keeps working.
        app.MapCoachConversations();

        // Learner memory is its own resource for the same reason: approving what Sam remembers
        // about you is a separate decision from accepting a plan change, and a shared route would
        // eventually let one gesture stand for both.
        app.MapCoachMemories();

        return app;
    }

    private static async Task<IResult> GetAvailabilityAsync(
        [FromServices] ICoachSessionService coach,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await coach.GetAvailabilityAsync(cancellationToken);

            // Availability is the one route that answers instead of 404-ing when the coach is
            // off: the client needs a definite "no entry point" without treating it as an error.
            return result.IsOk
                ? Results.Ok(result.Value)
                : Results.Ok(new CoachAvailabilityResponse
                {
                    IsAvailable = false,
                    State = CoachAvailabilityState.Disabled
                });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, "GET /api/v1/coach/availability", ex);
            return Results.Problem(detail: "Failed to read coach availability.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<IResult> StartSessionAsync(
        StartCoachSessionRequest? request,
        [FromServices] CoachCompatibilitySessionService coach,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => coach.StartSessionAsync(request ?? new StartCoachSessionRequest(), cancellationToken),
            loggerFactory, "POST /api/v1/coach/sessions");

    private static Task<IResult> GetSessionAsync(
        string sessionId,
        [FromServices] CoachCompatibilitySessionService coach,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => coach.GetSessionAsync(sessionId, cancellationToken),
            loggerFactory, "GET /api/v1/coach/sessions/{id}");

    private static Task<IResult> SubmitTurnAsync(
        string sessionId,
        CoachTurnRequest? request,
        [FromServices] CoachCompatibilitySessionService coach,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => coach.SubmitTurnAsync(
                sessionId,
                request ?? new CoachTurnRequest { InputKind = CoachTurnInputKind.Text },
                cancellationToken),
            loggerFactory, "POST /api/v1/coach/sessions/{id}/turns");

    private static Task<IResult> AcceptSuggestionAsync(
        string sessionId,
        string suggestionId,
        CoachSuggestionDecisionRequest? request,
        [FromServices] CoachCompatibilitySessionService coach,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => coach.AcceptSuggestionAsync(
                sessionId, suggestionId, request ?? new CoachSuggestionDecisionRequest(), cancellationToken),
            loggerFactory, "POST /api/v1/coach/sessions/{id}/suggestions/{id}/accept");

    private static Task<IResult> RejectSuggestionAsync(
        string sessionId,
        string suggestionId,
        CoachSuggestionDecisionRequest? request,
        [FromServices] CoachCompatibilitySessionService coach,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => coach.RejectSuggestionAsync(
                sessionId, suggestionId, request ?? new CoachSuggestionDecisionRequest(), cancellationToken),
            loggerFactory, "POST /api/v1/coach/sessions/{id}/suggestions/{id}/reject");

    private static Task<IResult> UndoAsync(
        string sessionId,
        CoachUndoRequest? request,
        [FromServices] CoachCompatibilitySessionService coach,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        CoachEndpointExecution.ExecuteAsync(
            () => coach.UndoAsync(sessionId, request ?? new CoachUndoRequest(), cancellationToken),
            loggerFactory, "POST /api/v1/coach/sessions/{id}/undo");

    private static async Task<IResult> CancelAsync(
        string sessionId,
        [FromServices] CoachCompatibilitySessionService coach,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await coach.CancelAsync(sessionId, cancellationToken);
            return result.IsOk ? Results.NoContent() : CoachEndpointExecution.ToProblem(result.Status, result.ProblemType, result.Detail);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, "POST /api/v1/coach/sessions/{id}/cancel", ex);
            return Results.Problem(detail: "Failed to stop the coach run.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DeleteSessionAsync(
        string sessionId,
        [FromServices] CoachCompatibilitySessionService coach,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await coach.DeleteSessionAsync(sessionId, cancellationToken);
            return result.IsOk ? Results.NoContent() : CoachEndpointExecution.ToProblem(result.Status, result.ProblemType, result.Detail);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, "DELETE /api/v1/coach/sessions/{id}", ex);
            return Results.Problem(detail: "Failed to delete the coach session.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

}
