using System.Globalization;
using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Extensions.AI;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Wire;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebApp.Coach;

public sealed class CoachAGUITransport(IHttpClientFactory httpClientFactory) : ICoachTurnTransport
{
    public async Task<CoachTurnResponse> SubmitTurnAsync(
        string sessionId, CoachTurnRequest request, CancellationToken cancellationToken)
    {
        var state = await RunAsync(sessionId, new CoachAgentState { Turn = request },
            request.ClientTurnId, cancellationToken);
        return state.Result ?? throw new InvalidDataException("The coach stream contained no turn result.");
    }

    public async Task<CoachTurnOperationDto> SubmitConversationTurnAsync(
        string conversationId, CoachConversationTurnRequest request, CancellationToken cancellationToken)
    {
        var state = await RunAsync(conversationId, new CoachAgentState { DurableTurn = request },
            request.OperationId, cancellationToken);
        return state.Operation ?? throw new InvalidDataException("The coach stream contained no operation result.");
    }

    private async Task<CoachAgentState> RunAsync(
        string threadId, CoachAgentState state, string? runId, CancellationToken cancellationToken)
    {
        using var http = httpClientFactory.CreateClient("coach-agui");
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            WireHeaders.ClientProtocolVersion, WireProtocolVersion.Current.ToString(CultureInfo.InvariantCulture));
        using var client = new AGUIChatClient(new AGUIChatClientOptions(http, "/api/v1/coach/agent"));
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput
            {
                ThreadId = threadId,
                RunId = runId ?? Guid.NewGuid().ToString("N"),
                State = JsonSerializer.SerializeToElement(state, WireJson.Client)
            }
        };
        CoachAgentState? result = null;
        var finished = false;
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, state.Turn?.Text ?? state.DurableTurn?.Turn.Text ?? "")],
            options, cancellationToken))
        {
            if (update.RawRepresentation is RunErrorEvent error)
            {
                throw new CoachApiException(HttpStatusCode.BadRequest, error.Code, "Coach run failed", error.Message);
            }
            if (update.RawRepresentation is StateSnapshotEvent snapshot)
            {
                result = snapshot.Snapshot.Deserialize<CoachAgentState>(WireJson.Client);
            }
            if (update.RawRepresentation is RunFinishedEvent)
            {
                finished = true;
            }
        }
        if (!finished)
        {
            throw new InvalidDataException("The coach stream ended before the run completed.");
        }
        return result ?? throw new InvalidDataException("The coach stream ended without authoritative state.");
    }
}
