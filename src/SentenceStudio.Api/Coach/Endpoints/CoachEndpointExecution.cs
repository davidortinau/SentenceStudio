using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Endpoints;

/// <summary>
/// The shared translation from a coach application result to an HTTP response.
/// </summary>
/// <remarks>
/// Both the session routes and the conversation routes go through here, so a status can never
/// mean 404 on one surface and 409 on the other. Divergence in that mapping is exactly how a
/// client ends up treating "someone else owns this" as a retryable error.
/// </remarks>
internal static class CoachEndpointExecution
{
    internal static async Task<IResult> ExecuteAsync<T>(
        Func<Task<CoachOperationResult<T>>> operation,
        ILoggerFactory loggerFactory,
        string route,
        bool noContentOnSuccess = false)
    {
        try
        {
            var result = await operation();
            if (!result.IsOk)
            {
                return ToProblem(result.Status, result.ProblemType, result.Detail);
            }

            // A delete has nothing worth reading back. Answering 204 keeps a repeated delete and
            // a first delete identical on the wire, which is what makes retrying one safe.
            return noContentOnSuccess ? Results.NoContent() : Results.Ok(result.Value);
        }
        catch (UnauthorizedAccessException)
        {
            // Authenticated but no user_profile_id claim: a typed 401, never a 500.
            return Results.Unauthorized();
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            // Shape only. This is the last catch on every coach route, so a provider or model
            // failure that escaped the turn runner would land here; passing the exception object
            // to the logger would write its message, inner chain, and Data — which is where
            // prompt and learner text live. See CoachExceptionSanitizer.
            LogFailure(loggerFactory, route, ex);

            return Results.Problem(detail: "The coach request failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Writes the content-free shape of a route failure.
    /// </summary>
    /// <remarks>
    /// The one place a coach route failure is written, so a handler that does its own
    /// <c>try/catch</c> instead of going through <see cref="ExecuteAsync{T}"/> cannot quietly
    /// reintroduce <c>LogError(ex, ...)</c>. The exception object never reaches the logger:
    /// <see cref="Exception.ToString"/> concatenates the message, the inner chain, and
    /// <see cref="Exception.Data"/>, and on a coach route those carry prompt text, learner text,
    /// and model output. See <see cref="CoachExceptionSanitizer"/>.
    /// </remarks>
    internal static void LogFailure(ILoggerFactory loggerFactory, string route, Exception ex)
    {
        var facts = CoachExceptionSanitizer.Describe(ex);
        loggerFactory.CreateLogger("Coach").LogError(
            "{Route} failed. Category={FailureCategory} ProviderStatus={ProviderStatus} " +
            "ProviderCode={ProviderErrorCode} InnerDepth={InnerDepth}",
            route,
            facts.Category,
            facts.ProviderStatus,
            facts.ProviderErrorCode,
            facts.InnerDepth);
    }

    internal static IResult ToProblem(CoachOperationStatus status, string? problemType, string? detail)
    {
        var (code, title) = status switch
        {
            // Feature off, outside the cohort, no plan to edit, or someone else's session:
            // all indistinguishable from "there is nothing here".
            CoachOperationStatus.Unavailable => (StatusCodes.Status404NotFound, "Coach unavailable"),
            CoachOperationStatus.SessionNotFound => (StatusCodes.Status404NotFound, "Coach session not found"),
            CoachOperationStatus.SessionExpired => (StatusCodes.Status404NotFound, "Coach session expired"),
            CoachOperationStatus.SuggestionNotFound => (StatusCodes.Status404NotFound, "Suggestion not pending"),

            CoachOperationStatus.PlanChangedElsewhere => (StatusCodes.Status409Conflict, "Plan changed elsewhere"),
            CoachOperationStatus.RunInProgress => (StatusCodes.Status409Conflict, "Coach run in progress"),

            // The learner withdrew this turn. That is a conflict with what they asked for, not a
            // fault: without a case here it would surface as a 500 and look like a broken server.
            CoachOperationStatus.RunCancelled => (StatusCodes.Status409Conflict, "Coach run cancelled"),

            CoachOperationStatus.InvalidInput => (StatusCodes.Status422UnprocessableEntity, "Invalid coach input"),
            CoachOperationStatus.InvalidConstraint => (StatusCodes.Status422UnprocessableEntity, "Invalid constraint"),
            CoachOperationStatus.NoFeasiblePlan => (StatusCodes.Status422UnprocessableEntity, "No feasible plan"),
            CoachOperationStatus.NothingToUndo => (StatusCodes.Status422UnprocessableEntity, "Nothing to undo"),

            CoachOperationStatus.RateLimited => (StatusCodes.Status429TooManyRequests, "Coach limit reached"),
            CoachOperationStatus.ModelUnavailable => (StatusCodes.Status503ServiceUnavailable, "Coach model unavailable"),

            _ => (StatusCodes.Status500InternalServerError, "Coach request failed")
        };

        return Results.Problem(
            type: problemType,
            title: title,
            detail: detail,
            statusCode: code);
    }
}
