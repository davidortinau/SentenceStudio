using System.Net;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// What a client does when the server says the conversation is already busy with the turn this
/// client is holding.
/// </summary>
/// <remarks>
/// <para>
/// The server keeps one writer per conversation, and it keeps it for as long as that writer is
/// working. A resend of the same turn — a dropped response, a reconnected circuit, a learner
/// pressing the button again — therefore gets a conflict rather than a result, and gets it
/// precisely <em>because</em> the turn is alive and being answered.
/// </para>
/// <para>
/// Reporting that as a failed turn would be the wrong answer twice over: the learner is told
/// their message failed at the moment it is succeeding, and the affordance they are offered is
/// the one that used to take the conversation over. Since the client already holds the operation
/// id, the honest response is to watch the turn it started.
/// </para>
/// </remarks>
public class CoachDurableTurnConflictTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        return (new CoachWorkspaceState(client, new CoachConversationDirectory(client)), client);
    }

    [Fact]
    public async Task A_resend_of_a_running_turn_waits_for_it_instead_of_reporting_a_failure()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        // The first attempt's reply never arrives, so the turn is left pending on the client and
        // running on the server.
        client.OnSubmitConversationTurn = (_, _) => throw new HttpRequestException("dropped");
        await Ask(state, "Can we do more listening?");

        var operationId = state.PendingOperationId;
        operationId.Should().NotBeNull();

        // The resend is refused: the server is still working on this exact turn.
        client.OnSubmitConversationTurn = (_, _) => throw new CoachApiException(
            HttpStatusCode.Conflict, CoachProblemTypes.RunInProgress, title: null, detail: null);

        var polls = 0;
        client.OnGetConversationOperation = (id, polled) =>
        {
            polls++;

            // Still running on the first look, finished on the next: the client has to keep
            // watching rather than treat "busy" as an ending.
            return polls == 1
                ? Running(id, polled)
                : Completed(id, polled);
        };

        await state.RetryDurableTurnAsync();

        state.State.Should().Be(CoachUiState.Ready, "the turn the client was waiting on finished");
        state.HasRecoverableTurn.Should().BeFalse();
        state.PendingOperationId.Should().BeNull("the turn settled, so there is nothing left pending");
        polls.Should().BeGreaterThan(0, "the conflict has to be resolved by watching the operation");
    }

    [Fact]
    public async Task A_conflict_from_someone_elses_turn_is_still_reported()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        client.OnSubmitConversationTurn = (_, _) => throw new HttpRequestException("dropped");
        await Ask(state, "Can we do more listening?");

        client.OnSubmitConversationTurn = (_, _) => throw new CoachApiException(
            HttpStatusCode.Conflict, CoachProblemTypes.RunInProgress, title: null, detail: null);

        // The conversation is busy with a turn this client does not own, so there is no operation
        // of ours to wait on. Waiting anyway would hang on somebody else's work.
        client.OnGetConversationOperation = (_, _) => null;

        await state.RetryDurableTurnAsync();

        // The conflict is reported rather than swallowed, and the handles survive it, so asking
        // again resumes this turn instead of starting a second one.
        state.State.Should().Be(CoachUiState.Incomplete);
        state.PendingOperationId.Should().NotBeNull();
        client.GetConversationOperationCalls.Should().BeGreaterThan(0, "the client checked whether the busy turn was its own");
    }

    [Fact]
    public async Task A_conflict_about_a_turn_that_already_finished_is_reported_rather_than_polled_forever()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        client.OnSubmitConversationTurn = (_, _) => throw new HttpRequestException("dropped");
        await Ask(state, "Can we do more listening?");

        client.OnSubmitConversationTurn = (_, _) => throw new CoachApiException(
            HttpStatusCode.Conflict, CoachProblemTypes.RunInProgress, title: null, detail: null);

        client.OnGetConversationOperation = (id, polled) => new CoachTurnOperationDto
        {
            OperationId = polled,
            ConversationId = id,
            State = CoachTurnOperationState.Cancelled,
            Messages = Array.Empty<CoachHistoryMessageDto>(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await state.RetryDurableTurnAsync();

        // A terminal operation is not something to wait for. The conflict stands, and the learner
        // is left with a turn they can act on rather than a spinner that never ends.
        state.State.Should().Be(CoachUiState.Incomplete);
        state.PendingOperationId.Should().NotBeNull();
    }

    private static CoachTurnOperationDto Running(string conversationId, string operationId) => new()
    {
        OperationId = operationId,
        ConversationId = conversationId,
        State = CoachTurnOperationState.Running,
        Messages = Array.Empty<CoachHistoryMessageDto>(),
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow
    };

    private static CoachTurnOperationDto Completed(string conversationId, string operationId) => new()
    {
        OperationId = operationId,
        ConversationId = conversationId,
        State = CoachTurnOperationState.Completed,
        Result = CoachStateMachineTests.Turn(),
        Messages = Array.Empty<CoachHistoryMessageDto>(),
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow
    };

    private static async Task Ask(CoachWorkspaceState state, string text)
    {
        state.Draft = text;
        await state.SendDraftAsync();
    }
}
