using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Which client method a turn actually reaches, and what survives to the next process.
/// </summary>
/// <remarks>
/// These exist because of a live failure: the workspace opened durable history, created a
/// conversation row, and then posted every turn to the legacy session route. The server was left
/// holding a conversation with no messages, and a learner who reloaded found an empty thread they
/// had just been talking in. Nothing about the durable machinery was broken — the entry point
/// simply never selected it. So these tests assert the route by name, not the state flags, and
/// they read the transcript back through a second workspace rather than trusting the first one's
/// in-memory copy.
/// </remarks>
public class CoachDurableTurnRoutingTests
{
    private static (CoachWorkspaceState State, CoachConversationDirectory Directory, FakeCoachApiClient Client)
        Create(bool durable = true)
    {
        var client = new FakeCoachApiClient
        {
            DurableHistoryAvailable = durable,
            Availability = new CoachAvailabilityResponse
            {
                IsAvailable = true,
                State = CoachAvailabilityState.Available,
                CanEditPlan = true,
                IsDurableHistoryAvailable = durable,
                IsMemoryAvailable = false
            }
        };

        var directory = new CoachConversationDirectory(client);
        return (new CoachWorkspaceState(client, directory), directory, client);
    }

    /// <summary>A second workspace over the same server, standing in for a process restart.</summary>
    private static CoachWorkspaceState Restart(FakeCoachApiClient client)
        => new(client, new CoachConversationDirectory(client));

    // ---------------------------------------------------------------- entry selects durable

    [Fact]
    public async Task OpenAsync_StartsADurableConversationWhenHistoryIsAvailable()
    {
        var (state, _, client) = Create();

        await state.OpenAsync(CoachPresentation.Overlay);

        state.IsDurableHistoryEnabled.Should().BeTrue();
        state.ConversationId.Should().NotBeNullOrEmpty();
        client.CreateConversationCalls.Should().Be(1);
    }

    [Fact]
    public async Task OpenAsync_StaysOnTheSessionRouteWhenHistoryIsOff()
    {
        var (state, _, client) = Create(durable: false);

        await state.OpenAsync(CoachPresentation.Overlay);

        state.IsDurableHistoryEnabled.Should().BeFalse();
        state.ConversationId.Should().BeNull();
        client.CreateConversationCalls.Should().Be(0);
        client.StartSessionCalls.Should().Be(1);
    }

    [Fact]
    public async Task ReopeningWithoutAnIdStaysInTheSameConversation()
    {
        var (state, _, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);
        var first = state.ConversationId;

        // A re-render, a second navigation, or any "make sure this is open" call. Creating a new
        // thread here is how a learner ends up talking into an empty conversation while the one
        // they were reading sits abandoned.
        await state.OpenAsync(CoachPresentation.Overlay);

        state.ConversationId.Should().Be(first);
        client.CreateConversationCalls.Should().Be(1);
    }

    [Fact]
    public async Task StartingANewConversationAfterAResetCreatesAnotherOne()
    {
        var (state, _, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);
        var first = state.ConversationId;

        state.Reset();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.ConversationId.Should().NotBe(first);
        client.CreateConversationCalls.Should().Be(2);
    }

    [Fact]
    public async Task ADurableDeepLinkStillLearnsWhetherThePlanCanBeEdited()
    {
        var (state, _, _) = Create();

        await state.OpenAsync(CoachPresentation.Overlay);

        // Availability carries CanEditPlan. Opening straight into a conversation without reading
        // it would show plan affordances to a learner who has no plan.
        state.Availability.Should().NotBeNull();
        state.CanEditPlan.Should().BeTrue();
    }

    // ---------------------------------------------------------------- route selection

    [Fact]
    public async Task ADurableTurnPostsToTheConversationRouteAndNeverToTheSessionRoute()
    {
        var (state, _, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Draft = "Durable turn.";
        await state.SendDraftAsync();

        client.SubmitConversationTurnCalls.Should().Be(1);

        // The exact defect this file was written for. A durable conversation that receives its
        // turns on the session route records nothing, and says nothing about it.
        client.SubmitTurnCalls.Should().Be(0, "durable mode must never fall back to /sessions");
    }

    [Fact]
    public async Task ALegacyTurnPostsToTheSessionRouteAndNeverToTheConversationRoute()
    {
        var (state, _, client) = Create(durable: false);
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Draft = "Legacy turn.";
        await state.SendDraftAsync();

        client.SubmitTurnCalls.Should().Be(1);
        client.SubmitConversationTurnCalls.Should().Be(0, "the flag is off, so there is no conversation to post to");
    }

    [Fact]
    public async Task ADurableTurnCarriesAnOperationIdAndAnIdempotencyKey()
    {
        var (state, _, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Draft = "Handles please.";
        await state.SendDraftAsync();

        var submitted = client.SubmittedConversationTurns.Should().ContainSingle().Subject;
        submitted.OperationId.Should().NotBeNullOrEmpty();
        submitted.IdempotencyKey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RetryingTheSameTurnReusesBothHandlesSoTheServerCanReplayIt()
    {
        var (state, _, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var attempts = 0;
        client.OnSubmitConversationTurn = (_, request) =>
        {
            attempts++;
            return attempts == 1
                ? throw new HttpRequestException("lost")
                : new CoachTurnOperationDto
                {
                    OperationId = request.OperationId,
                    ConversationId = state.ConversationId!,
                    State = CoachTurnOperationState.Completed,
                    Result = CoachStateMachineTests.Turn(),
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
        };

        state.Draft = "Retry me.";
        await state.SendDraftAsync();
        await state.RetryLastAsync();

        client.SubmittedConversationTurns.Should().HaveCount(2);
        client.SubmittedConversationTurns[1].OperationId
            .Should().Be(client.SubmittedConversationTurns[0].OperationId);
        client.SubmittedConversationTurns[1].IdempotencyKey
            .Should().Be(client.SubmittedConversationTurns[0].IdempotencyKey, "new handles would make a retry a second turn");
    }

    [Fact]
    public async Task CancellingADurableRunCancelsTheOperationOnTheServer()
    {
        var (state, _, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var gate = new TaskCompletionSource();

        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            gate.TrySetResult();
            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Pending,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        client.OnGetConversationOperation = (conversationId, operationId) => new CoachTurnOperationDto
        {
            OperationId = operationId,
            ConversationId = conversationId,
            State = CoachTurnOperationState.Cancelled,
            CancelRequested = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        state.Draft = "Cancel me.";
        var run = state.SendDraftAsync();
        await gate.Task;
        await state.CancelRunAsync();
        await run;

        // Stopping the local request would leave the turn running on the server and its answer
        // arriving into a conversation the learner believes they stopped.
        client.CancelConversationTurnCalls.Should().BeGreaterThan(0);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public async Task ADurableTurnIsStoredAndComesBackAfterAProcessRestart()
    {
        var (state, _, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);
        var conversationId = state.ConversationId!;

        state.Draft = "Remember this.";
        await state.SendDraftAsync();

        // The server's ledger, not the workspace's copy. This is the assertion the live failure
        // would have caught: the row existed and the message list was empty.
        client.ConversationMessages[conversationId].Should().NotBeEmpty();
        client.ConversationMessages[conversationId]
            .Should().Contain(m => m.Message.Text == "Remember this.");

        var restarted = Restart(client);
        await restarted.OpenAsync(CoachPresentation.Overlay, conversationId);

        restarted.ConversationId.Should().Be(conversationId);
        restarted.Timeline.Should().Contain(entry => entry.ReadableText() == "Remember this.");
        restarted.Timeline.Should().Contain(entry => entry.ReadableText() == "Sam replies.");
    }

    [Fact]
    public async Task ACompletedTurnThatCarriesNoMessagesIsReconciledFromTheLedger()
    {
        var (state, _, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);
        var conversationId = state.ConversationId!;

        // A server that answers with a result but leaves the transcript to the ledger. Building
        // the timeline from the reply alone would leave the screen disagreeing with a reload.
        client.Seed(conversationId, CoachMessageRole.Learner, "Canonical learner line.");
        client.Seed(conversationId, CoachMessageRole.Coach, "Canonical Sam line.");

        client.OnSubmitConversationTurn = (_, request) => new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = conversationId,
            State = CoachTurnOperationState.Completed,
            Result = CoachStateMachineTests.Turn(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        state.Draft = "Say something.";
        await state.SendDraftAsync();

        state.Timeline.Should().Contain(entry => entry.ReadableText() == "Canonical Sam line.");
        client.GetConversationMessagesCalls.Should().BeGreaterThan(1, "the ledger is read back when the operation carried no messages");
    }

    // ---------------------------------------------------------------- addressability

    [Fact]
    public async Task TheEntryIdNamesTheConversationInDurableMode()
    {
        var (state, _, _) = Create();

        await state.OpenAsync(CoachPresentation.Overlay);

        // What the address bar has to carry. A session id there would resume into an empty thread
        // once the checkpoint expired.
        state.EntryId.Should().Be(state.ConversationId);
        state.EntryId.Should().NotBe(state.SessionId);
    }

    [Fact]
    public async Task TheEntryIdNamesTheSessionInLegacyMode()
    {
        var (state, _, _) = Create(durable: false);

        await state.OpenAsync(CoachPresentation.Overlay);

        state.EntryId.Should().Be(state.SessionId);
        state.EntryId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ReopeningByIdReturnsToTheSameConversationRatherThanTheLatestOne()
    {
        var (state, _, client) = Create();
        var older = client.AddConversation("c-older", updatedAtUtc: new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        client.AddConversation("c-newer", updatedAtUtc: new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc));
        client.Seed(older.ConversationId, CoachMessageRole.Learner, "The older thread.");

        await state.OpenAsync(CoachPresentation.Overlay, "c-older");

        state.ConversationId.Should().Be("c-older");
        state.Timeline.Should().Contain(entry => entry.ReadableText() == "The older thread.");
    }

    [Fact]
    public async Task AStaleLinkStartsANewConversationRatherThanReportingAnErrorAboutAnIdTheLearnerNeverSaw()
    {
        var (state, _, client) = Create();

        await state.OpenAsync(CoachPresentation.Overlay, "c-does-not-exist");

        // Legacy resume has always treated a missing target as "then this is a new conversation".
        state.ConversationId.Should().NotBeNullOrEmpty();
        state.ConversationId.Should().NotBe("c-does-not-exist");
        client.CreateConversationCalls.Should().Be(1);
    }

    [Fact]
    public async Task PickingAConversationFromTheListStillReportsItGoneWhenItIsGone()
    {
        var (state, _, _) = Create();

        // The list path keeps the strict semantics: the id came from a row the learner just
        // tapped, so substituting a different thread would be the surprise, not the error.
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-does-not-exist");

        state.ConversationId.Should().BeNull();
        state.ConversationNoticeKey.Should().Be("Coach_ConversationGone");
    }
}
