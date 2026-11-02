using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Application.Compatibility;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Endpoints;

public static class CoachAGUIEndpoints
{
    public static IEndpointRouteBuilder MapCoachAGUI(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/coach/agent", RunAsync)
            .RequireAuthorization()
            .WithName("RunCoachAgent");
        return app;
    }

    private static async Task<IResult> RunAsync(
        RunAgentInput input,
        CoachCompatibilitySessionService sessions,
        ICoachConversationService conversations,
        IOptions<JsonOptions> jsonOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(input.ThreadId) || input.ThreadId.Length > 128
                || string.IsNullOrWhiteSpace(input.RunId) || input.RunId.Length > 128
                || input.Tools is { Count: > 0 } || input.State is not { ValueKind: JsonValueKind.Object } payload)
            {
                return InvalidInput();
            }

            var state = payload.Deserialize<CoachAgentState>(jsonOptions.Value.SerializerOptions);
            if (state is null || (state.Turn is null) == (state.DurableTurn is null)
                || state.Result is not null || state.Operation is not null)
            {
                return InvalidInput();
            }

            // The thread id is a locator, never authority. Client history, tool declarations and
            // shared state must not replace the server's owned ledger or its validated intent.
            if (state.DurableTurn is not null)
            {
                var owned = await conversations.GetAsync(input.ThreadId, cancellationToken);
                if (!owned.IsOk)
                {
                    return CoachEndpointExecution.ToProblem(owned.Status, owned.ProblemType, owned.Detail);
                }
            }
            else
            {
                var owned = await sessions.GetSessionAsync(input.ThreadId, cancellationToken);
                if (!owned.IsOk)
                {
                    return CoachEndpointExecution.ToProblem(owned.Status, owned.ProblemType, owned.Detail);
                }
            }

            return TypedResults.ServerSentEvents(StreamAsync(
                input, state, sessions, conversations, jsonOptions.Value.SerializerOptions,
                loggerFactory, cancellationToken));
        }
        catch (JsonException)
        {
            return InvalidInput();
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
            CoachEndpointExecution.LogFailure(loggerFactory, "POST /api/v1/coach/agent", ex);
            return Results.Problem(detail: "The coach request failed.", statusCode: 500);
        }
    }

    private static IResult InvalidInput() => CoachEndpointExecution.ToProblem(
        CoachOperationStatus.InvalidInput, CoachProblemTypes.InvalidTurnInput,
        "Supply exactly one coach turn and an owned thread identifier. Client tools are not supported.");

    private static async IAsyncEnumerable<BaseEvent> StreamAsync(
        RunAgentInput input,
        CoachAgentState state,
        CoachCompatibilitySessionService sessions,
        ICoachConversationService conversations,
        JsonSerializerOptions jsonOptions,
        ILoggerFactory loggerFactory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId };

        CoachAgentState? completed = null;
        RunErrorEvent? error = null;
        try
        {
            if (state.DurableTurn is { } durable)
            {
                var result = await conversations.SubmitTurnAsync(input.ThreadId, durable, cancellationToken);
                if (result.IsOk && result.Value is { } operation)
                {
                    completed = new CoachAgentState { Operation = operation, Result = operation.Result };
                }
                else
                {
                    error = Failure(result.ProblemType, result.Detail);
                }
            }
            else
            {
                var result = await sessions.SubmitTurnAsync(input.ThreadId, state.Turn!, cancellationToken);
                if (result.IsOk && result.Value is { } turn)
                {
                    completed = new CoachAgentState { Result = turn };
                }
                else
                {
                    error = Failure(result.ProblemType, result.Detail);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CoachEndpointExecution.LogFailure(loggerFactory, "POST /api/v1/coach/agent stream", ex);
            error = Failure(null, "The coach request failed.");
        }

        if (error is not null)
        {
            yield return error;
            yield break;
        }

        // Only validated, embargo-checked application output crosses the UI boundary. Streaming
        // the agent's raw structured JSON or tool results here would bypass those safeguards.
        yield return new StateSnapshotEvent
        {
            Snapshot = JsonSerializer.SerializeToElement(completed, jsonOptions)
        };
        foreach (var message in completed?.Result?.Messages ?? [])
        {
            if (message.Role != CoachMessageRole.Coach || string.IsNullOrEmpty(message.Text))
            {
                continue;
            }

            yield return new TextMessageStartEvent { MessageId = message.MessageId, Role = "assistant" };
            yield return new TextMessageContentEvent { MessageId = message.MessageId, Delta = message.Text };
            yield return new TextMessageEndEvent { MessageId = message.MessageId };
        }
        yield return new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId };
    }

    private static RunErrorEvent Failure(string? code, string? detail) => new()
    {
        Code = code ?? CoachProblemTypes.Unavailable,
        Message = detail ?? "The coach turn did not complete."
    };
}
