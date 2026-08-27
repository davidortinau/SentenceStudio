using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory.Endpoints;

/// <summary>
/// The learner-facing memory endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Mapped separately from the rest of the coach routes so the whole surface can be added or left
/// out as one unit. When the feature flag is off the routes are still mapped but every one answers
/// 404: an endpoint that appears and disappears with configuration is harder to reason about than
/// one that consistently reports nothing to show.
/// </para>
/// <para>
/// Foreign ids, missing ids, disabled features, and expired rows all produce the same 404. That is
/// deliberate — a distinguishable 403 would confirm that a guessed id belongs to somebody.
/// </para>
/// </remarks>
public static class CoachMemoryEndpoints
{
    private const string Tag = "Coach Memory";

    /// <summary>Maps <c>/api/v1/coach/memories</c>.</summary>
    public static IEndpointRouteBuilder MapCoachMemories(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/coach/memories")
                       .RequireAuthorization()
                       .WithTags(Tag);

        group.MapGet("/", ListActive).WithName("CoachMemoriesList");
        group.MapGet("/candidates", ListCandidates).WithName("CoachMemoriesListCandidates");
        group.MapPost("/{factId}/approve", Approve).WithName("CoachMemoriesApprove");
        group.MapPost("/{factId}/reject", Reject).WithName("CoachMemoriesReject");
        group.MapPut("/{factId}", Edit).WithName("CoachMemoriesEdit");
        group.MapDelete("/{factId}", Forget).WithName("CoachMemoriesForget");
        group.MapDelete("/", ForgetAll).WithName("CoachMemoriesForgetAll");

        return app;
    }

    private static Task<IResult> ListActive(
        [FromServices] ICoachMemoryService service,
        [FromServices] ILoggerFactory loggerFactory,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
        => List(service, loggerFactory, CoachMemoryListFilter.Active, pageSize, cursor, cancellationToken);

    private static Task<IResult> ListCandidates(
        [FromServices] ICoachMemoryService service,
        [FromServices] ILoggerFactory loggerFactory,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
        => List(service, loggerFactory, CoachMemoryListFilter.Candidates, pageSize, cursor, cancellationToken);

    private static async Task<IResult> List(
        ICoachMemoryService service,
        ILoggerFactory loggerFactory,
        CoachMemoryListFilter filter,
        int? pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(CoachMemoryEndpoints));
        try
        {
            var (status, page) = await service.ListAsync(filter, pageSize, cursor, cancellationToken).ConfigureAwait(false);
            return status == CoachMemoryStatusCode.Success && page is not null
                ? Results.Ok(page)
                : ToProblem(status);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            return Fail(logger, ex, nameof(List));
        }
    }

    private static async Task<IResult> Approve(
        [FromServices] ICoachMemoryService service,
        [FromServices] ILoggerFactory loggerFactory,
        string factId,
        [FromBody] CoachMemoryApproveRequest request,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(CoachMemoryEndpoints));
        try
        {
            if (request is null)
            {
                return ToProblem(CoachMemoryStatusCode.InvalidRequest);
            }

            var (status, fact) = await service.ApproveAsync(factId, request, cancellationToken).ConfigureAwait(false);
            return status == CoachMemoryStatusCode.Success && fact is not null
                ? Results.Ok(fact)
                : ToProblem(status);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            return Fail(logger, ex, nameof(Approve));
        }
    }

    private static async Task<IResult> Reject(
        [FromServices] ICoachMemoryService service,
        [FromServices] ILoggerFactory loggerFactory,
        string factId,
        [FromBody] CoachMemoryRejectRequest request,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(CoachMemoryEndpoints));
        try
        {
            if (request is null)
            {
                return ToProblem(CoachMemoryStatusCode.InvalidRequest);
            }

            var status = await service.RejectAsync(factId, request, cancellationToken).ConfigureAwait(false);
            return status == CoachMemoryStatusCode.Success ? Results.NoContent() : ToProblem(status);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            return Fail(logger, ex, nameof(Reject));
        }
    }

    private static async Task<IResult> Edit(
        [FromServices] ICoachMemoryService service,
        [FromServices] ILoggerFactory loggerFactory,
        string factId,
        [FromBody] CoachMemoryEditRequest request,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(CoachMemoryEndpoints));
        try
        {
            if (request is null)
            {
                return ToProblem(CoachMemoryStatusCode.InvalidRequest);
            }

            var (status, fact) = await service.EditAsync(factId, request, cancellationToken).ConfigureAwait(false);
            return status == CoachMemoryStatusCode.Success && fact is not null
                ? Results.Ok(fact)
                : ToProblem(status);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            return Fail(logger, ex, nameof(Edit));
        }
    }

    private static async Task<IResult> Forget(
        [FromServices] ICoachMemoryService service,
        [FromServices] ILoggerFactory loggerFactory,
        string factId,
        [FromQuery] int? expectedVersion,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(CoachMemoryEndpoints));

        if (expectedVersion is null or < 0)
        {
            return Results.Problem(
                detail: "The 'expectedVersion' query parameter is required and must be a non-negative integer.",
                statusCode: 400,
                title: "Missing or invalid expectedVersion");
        }

        try
        {
            var status = await service.ForgetAsync(factId, expectedVersion.Value, cancellationToken).ConfigureAwait(false);
            return status == CoachMemoryStatusCode.Success ? Results.NoContent() : ToProblem(status);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            return Fail(logger, ex, nameof(Forget));
        }
    }

    private static async Task<IResult> ForgetAll(
        [FromServices] ICoachMemoryService service,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(CoachMemoryEndpoints));
        try
        {
            var (status, result) = await service.ForgetAllAsync(cancellationToken).ConfigureAwait(false);
            return status == CoachMemoryStatusCode.Success && result is not null
                ? Results.Ok(result)
                : ToProblem(status);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            return Fail(logger, ex, nameof(ForgetAll));
        }
    }

    /// <summary>
    /// Maps a store outcome to a response.
    /// </summary>
    /// <remarks>
    /// No branch echoes a value, a kind, or a learner string. A rejected value produces a 422 with
    /// a stable problem type and nothing else; the client owns the wording.
    /// </remarks>
    private static IResult ToProblem(CoachMemoryStatusCode status) => status switch
    {
        // Everything unfindable collapses to one answer.
        CoachMemoryStatusCode.NotFound or
        CoachMemoryStatusCode.NoOwner or
        CoachMemoryStatusCode.Disabled => Results.NotFound(),

        CoachMemoryStatusCode.Conflict => Results.Problem(
            title: "The saved preference changed.",
            type: CoachMemoryProblemTypes.Conflict,
            statusCode: StatusCodes.Status409Conflict),

        CoachMemoryStatusCode.ValueRejected or
        CoachMemoryStatusCode.EvidenceMismatch => Results.Problem(
            title: "That value cannot be saved as a preference.",
            type: CoachMemoryProblemTypes.ValueRejected,
            statusCode: StatusCodes.Status422UnprocessableEntity),

        CoachMemoryStatusCode.LimitReached => Results.Problem(
            title: "No room for another saved preference.",
            type: CoachMemoryProblemTypes.Conflict,
            statusCode: StatusCodes.Status409Conflict),

        CoachMemoryStatusCode.Unavailable => Results.Problem(
            title: "Saved preferences are unavailable.",
            type: CoachMemoryProblemTypes.Unavailable,
            statusCode: StatusCodes.Status503ServiceUnavailable),

        _ => Results.Problem(
            title: "The request could not be processed.",
            type: CoachMemoryProblemTypes.InvalidRequest,
            statusCode: StatusCodes.Status400BadRequest)
    };

    private static IResult Fail(ILogger logger, Exception ex, string operation)
    {
        // Type name and operation only. An exception message can carry a learner's own words.
        logger.LogError("[Coach] Memory endpoint {Operation} failed. Error={Error}", operation, ex.GetType().Name);
        return Results.Problem(
            title: "Saved preferences are unavailable.",
            type: CoachMemoryProblemTypes.Unavailable,
            statusCode: StatusCodes.Status500InternalServerError);
    }
}
