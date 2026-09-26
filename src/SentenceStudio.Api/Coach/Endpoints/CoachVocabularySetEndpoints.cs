using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Application.AppOperations;

namespace SentenceStudio.Api.Coach.Endpoints;

/// <summary>The neutral application operation used to approve a complete Coach vocabulary set.</summary>
public static class CoachVocabularySetEndpoints
{
    public static IEndpointRouteBuilder MapCoachVocabularySets(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/api/v1/application/vocabulary-sets/approve",
                ApproveAsync)
            .RequireAuthorization()
            .WithName("ApproveCoachVocabularySet");

        return app;
    }

    private static async Task<IResult> ApproveAsync(
        ApproveCoachVocabularySetRequest? request,
        [FromServices] CoachVocabularySetApplicationService service,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        try
        {
            var result = await service.ApproveAsync(request, cancellationToken).ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new ProblemDetails
            {
                Title = "Invalid vocabulary proposal",
                Detail = exception.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
        catch (ApplicationOperationConflictException exception)
        {
            return Results.Conflict(new ProblemDetails
            {
                Title = "Vocabulary proposal unavailable",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
        catch (CoachVocabularySetApplicationService.CoachVocabularySetInProgressException exception)
        {
            return Results.Conflict(new ProblemDetails
            {
                Type = CoachProblemTypes.RunInProgress,
                Title = "Vocabulary proposal in progress",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }
}
