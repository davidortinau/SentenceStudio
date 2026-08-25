using System.Net;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Behavioural tests for the durable half of the coach workspace: resuming an exact conversation
/// across a process restart, reconciling the optimistic learner message against the ledger, paging
/// backwards without losing the reader's place, and recovering a turn whose reply never arrived.
/// </summary>
public class CoachDurableWorkspaceTests
{
    private static (CoachWorkspaceState State, CoachConversationDirectory Directory, FakeCoachApiClient Client)
        Create(bool durable = true)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = durable };
        var directory = new CoachConversationDirectory(client);
        return (new CoachWorkspaceState(client, directory), directory, client);
    }

    /// <summary>A second workspace over the same server, standing in for a process restart.</summary>
    private static CoachWorkspaceState Restart(FakeCoachApiClient client)
        => new(client, new CoachConversationDirectory(client));

    // ---------------------------------------------------------------- feature detection

    [Fact]
    public async Task OpenConversationAsync_DeclinesWhenDurableHistoryIsNotAvailable()
    {
        var (state, _, client) = Create(durable: false);

        var handled = await state.OpenConversationAsync(CoachPresentation.Overlay);

        handled.Should().BeFalse("the caller must fall back to the session-only flow");
        state.IsDurableHistoryEnabled.Should().BeFalse();
        client.StartSessionCalls.Should().Be(0);
    }

    [Fact]
    public async Task OpenAsync_KeepsTheHiddenHistoryNoticeWhenDurableHistoryIsOff()
    {
        var (state, _, _) = Create(durable: false);

        await state.OpenAsync(CoachPresentation.Overlay, "session-9");

        // Legacy resume genuinely cannot show what was said before, and still says so.
        state.IsResumedWithoutHistory.Should().BeTrue();
    }

    [Fact]
    public async Task OpenConversationAsync_DropsTheHiddenHistoryNoticeOnceHistoryIsReal()
    {
        var (state, _, client) = Create();
        var conversation = client.AddConversation("c-1");
        client.Seed(conversation.ConversationId, CoachMessageRole.Learner, "Earlier turn.");

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        state.IsDurableHistoryEnabled.Should().BeTrue();
        state.IsResumedWithoutHistory.Should().BeFalse("the history is no longer hidden - it is on screen");
    }

    // ---------------------------------------------------------------- resume

    [Fact]
    public async Task OpenConversationAsync_ShowsTheStoredTranscriptAfterAProcessRestart()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I order coffee?");
        client.Seed("c-1", CoachMessageRole.Coach, "Start with a greeting.");

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        // Nothing of the first workspace survives; only the server does.
        var restarted = Restart(client);
        await restarted.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        restarted.ConversationId.Should().Be("c-1");
        restarted.Timeline.Select(m => m.ReadableText())
            .Should().ContainInOrder("How do I order coffee?", "Start with a greeting.");
    }

    [Fact]
    public async Task OpenConversationAsync_ResumesTheNamedConversationRatherThanTheMostRecentOne()
    {
        var (state, _, client) = Create();
        client.AddConversation("older", updatedAtUtc: new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        client.AddConversation("newest", updatedAtUtc: new DateTime(2026, 1, 9, 8, 0, 0, DateTimeKind.Utc));
        client.Seed("older", CoachMessageRole.Learner, "The one I asked for.");
        client.Seed("newest", CoachMessageRole.Learner, "Not this one.");

        await state.OpenConversationAsync(CoachPresentation.Overlay, "older");

        state.ConversationId.Should().Be("older");
        state.Timeline.Should().ContainSingle().Which.ReadableText().Should().Be("The one I asked for.");
    }

    [Fact]
    public async Task OpenConversationAsync_WithoutAnIdAlwaysStartsANewConversation()
    {
        var (state, _, client) = Create();
        client.AddConversation("existing");

        await state.OpenConversationAsync(CoachPresentation.Overlay);

        client.CreateConversationCalls.Should().Be(1);
        state.ConversationId.Should().NotBe("existing");
        state.Timeline.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenConversationAsync_TreatsAnUnknownIdAsGoneWithoutSayingWhetherItExists()
    {
        var (state, _, _) = Create();

        var handled = await state.OpenConversationAsync(CoachPresentation.Overlay, "someone-elses");

        handled.Should().BeTrue();
        state.ConversationNoticeKey.Should().Be("Coach_ConversationGone");
        state.ConversationId.Should().BeNull();
    }

    [Fact]
    public async Task OpenConversationAsync_KeepsTheTranscriptReadableWhenThePlanCheckpointFails()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Coach, "Still readable.");
        client.OnStartSession = () => throw new CoachApiException(
            HttpStatusCode.InternalServerError, CoachProblemTypes.ToolFailure, null, null);

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        state.Timeline.Should().ContainSingle().Which.ReadableText().Should().Be("Still readable.");
    }

    // ---------------------------------------------------------------- server truth

    [Fact]
    public async Task LoadTranscriptAsync_AdoptsTheServerTimestampInsteadOfTheClientCaptureTime()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        var seeded = client.Seed("c-1", CoachMessageRole.Coach, "Stamped by the server.");

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        state.Timeline.Should().ContainSingle().Which.Timestamp
            .Should().Be(new DateTimeOffset(seeded.Message.CreatedAtUtc, TimeSpan.Zero),
                "two devices reading one thread must agree, and a client clock is not evidence");
    }

    [Fact]
    public async Task LoadTranscriptAsync_OrdersByServerSequenceRatherThanArrivalOrder()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "first");
        client.Seed("c-1", CoachMessageRole.Coach, "second");
        client.Seed("c-1", CoachMessageRole.Learner, "third");

        client.OnGetConversationMessages = (id, _, _) => new CoachMessagePageDto
        {
            ConversationId = id,
            // Deliberately shuffled: sequence is the only ordering the client may trust.
            Items = client.ConversationMessages[id].OrderByDescending(m => m.Sequence).ToList(),
            PreviousCursor = null,
            UnreadableCount = 0
        };

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        state.Timeline.Select(m => m.ReadableText()).Should().ContainInOrder("first", "second", "third");
    }

    [Fact]
    public async Task LoadTranscriptAsync_ReReadingTheSameMessagesDoesNotGrowTheThread()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "Only once.");

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");
        await state.LoadTranscriptAsync();
        await state.LoadTranscriptAsync();

        state.Messages.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- pagination

    [Fact]
    public async Task LoadEarlierMessagesAsync_PrependsOlderMessagesInChronologicalOrder()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");

        for (var i = 1; i <= 60; i++)
        {
            client.Seed("c-1", CoachMessageRole.Learner, $"message {i}");
        }

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        // Newest page first: the last 50 of 60.
        state.Timeline.Should().HaveCount(50);
        state.Timeline[0].ReadableText().Should().Be("message 11");
        state.HasEarlierMessages.Should().BeTrue();

        await state.LoadEarlierMessagesAsync();

        state.Timeline.Should().HaveCount(60);
        state.Timeline[0].ReadableText().Should().Be("message 1");
        state.Timeline[^1].ReadableText().Should().Be("message 60");
        state.HasEarlierMessages.Should().BeFalse();

        // Endpoints alone would pass on a transcript that is scrambled in the middle. The whole
        // stream has to read in the order it happened.
        state.Timeline.Select(e => e.ServerSequence).Should().BeInAscendingOrder();
        state.Timeline.Select(e => e.ReadableText())
            .Should().ContainInOrder(Enumerable.Range(1, 60).Select(i => $"message {i}"));
    }

    [Fact]
    public async Task LoadEarlierMessagesAsync_KeepsTheNewTurnAtTheEndAfterOlderHistoryIsPrepended()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");

        for (var i = 1; i <= 60; i++)
        {
            client.Seed("c-1", CoachMessageRole.Learner, $"message {i}");
        }

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");
        await state.LoadEarlierMessagesAsync();

        // Speaking after reading back through history must still land at the bottom. This is the
        // case where local arrival counters and server sequence disagree most sharply.
        await Ask(state, "and now this");

        state.Timeline[0].ReadableText().Should().Be("message 1");
        state.Timeline.Last(e => e.Kind == CoachTimelineKind.LearnerMessage)
            .ReadableText().Should().Be("and now this");
        state.Timeline.Select(e => e.ServerSequence)
            .Where(s => s is not null)
            .Should().BeInAscendingOrder("durable history never reorders itself");
    }

    [Fact]
    public async Task LoadEarlierMessagesAsync_LeavesAnAnchorOnTheMessageTheReaderWasLookingAt()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");

        for (var i = 1; i <= 60; i++)
        {
            client.Seed("c-1", CoachMessageRole.Coach, $"message {i}");
        }

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");
        var wasAtTop = state.Timeline[0].MessageId;

        await state.LoadEarlierMessagesAsync();

        state.ConsumeScrollAnchor().Should().Be(wasAtTop,
            "inserting above the viewport must not move what the reader is reading");
        state.ConsumeScrollAnchor().Should().BeNull("the anchor is consumed once, by whoever restores position");
    }

    [Fact]
    public async Task LoadEarlierMessagesAsync_RecoversFromARejectedCursorByRereadingTheNewestPage()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");

        for (var i = 1; i <= 60; i++)
        {
            client.Seed("c-1", CoachMessageRole.Learner, $"message {i}");
        }

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        client.OnGetConversationMessages = (id, limit, before) => before is null
            ? new CoachMessagePageDto
            {
                ConversationId = id,
                Items = client.ConversationMessages[id].TakeLast(limit ?? 50).ToList(),
                PreviousCursor = null,
                UnreadableCount = 0
            }
            : throw new CoachApiException(
                HttpStatusCode.BadRequest, CoachProblemTypes.InvalidCursor, null, null);

        await state.LoadEarlierMessagesAsync();

        state.ConversationNoticeKey.Should().BeNull("a stale cursor is not something the learner can act on");
        state.HasEarlierMessages.Should().BeFalse();
        state.Timeline.Should().HaveCount(50);
    }

    [Fact]
    public async Task LoadTranscriptAsync_CountsMessagesItCannotRenderWithoutInventingContent()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Coach, "readable");
        client.Seed("c-1", CoachMessageRole.Coach, string.Empty, isReadable: false);

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        state.UnreadableMessageCount.Should().Be(1);
        state.Timeline.Should().Contain(m => m.Kind == CoachTimelineKind.UnreadableMessage);
    }

    // ---------------------------------------------------------------- turn submission

    [Fact]
    public async Task AskAsync_SendsAnOperationIdAndIdempotencyKeyMintedByTheClient()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        await Ask(state, "How do I order coffee?");

        var submitted = client.SubmittedConversationTurns.Should().ContainSingle().Subject;
        submitted.OperationId.Should().NotBeNullOrWhiteSpace();
        submitted.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
        submitted.OperationId.Should().NotBe(submitted.IdempotencyKey);
    }

    [Fact]
    public async Task AskAsync_ReplacesTheOptimisticLearnerMessageWithTheCanonicalOne()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        await Ask(state, "How do I order coffee?");

        var learnerMessages = state.Timeline
            .Where(m => m.Kind == CoachTimelineKind.LearnerMessage)
            .ToList();

        learnerMessages.Should().ContainSingle("the optimistic copy is replaced, not joined");
        learnerMessages[0].ReadableText().Should().Be("How do I order coffee?");
        learnerMessages[0].MessageId.Should().NotBeNullOrWhiteSpace("it is now the server's message");
        learnerMessages[0].ServerSequence.Should().NotBeNull();
        learnerMessages[0].Status.Should().Be(CoachTimelineStatus.Settled);
    }

    [Fact]
    public async Task AskAsync_SaidTwiceIsTwoMessages()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        await Ask(state, "yes");
        await Ask(state, "yes");

        // Reconciliation matches on the turn handle, never on the text. A learner who says the
        // same thing twice said it twice.
        state.Timeline.Count(m => m.Kind == CoachTimelineKind.LearnerMessage && m.ReadableText() == "yes")
            .Should().Be(2);
    }

    [Fact]
    public async Task AskAsync_WhenTheModelFailsKeepsTheLearnerMessageAndOffersARetry()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        client.OnSubmitConversationTurn = (id, request) => new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = id,
            State = CoachTurnOperationState.Failed,
            Messages = Array.Empty<CoachHistoryMessageDto>(),
            ErrorCode = CoachProblemTypes.ToolFailure,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await Ask(state, "Will this work?");

        // A failed durable turn is not the legacy "incomplete" run: the server answered, the
        // learner's message was kept, and there is something to retry. The state has to say so.
        state.State.Should().Be(CoachUiState.Failed);
        var learner = state.Timeline.Should().ContainSingle(m => m.Kind == CoachTimelineKind.LearnerMessage).Subject;
        learner.ReadableText().Should().Be("Will this work?", "the learner should not have to retype what they said");
        learner.Status.Should().Be(CoachTimelineStatus.Failed);
        state.PendingOperationId.Should().NotBeNull("the retry has to reuse the same operation");
    }

    [Fact]
    public async Task RetryDurableTurnAsync_ReusesTheSameHandlesSoTheServerReplaysRatherThanReruns()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var failNext = true;
        client.OnSubmitConversationTurn = (id, request) =>
        {
            if (failNext)
            {
                failNext = false;
                return new CoachTurnOperationDto
                {
                    OperationId = request.OperationId,
                    ConversationId = id,
                    State = CoachTurnOperationState.Failed,
                    Messages = Array.Empty<CoachHistoryMessageDto>(),
                    ErrorCode = CoachProblemTypes.ToolFailure,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
            }

            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = id,
                State = CoachTurnOperationState.Completed,
                Result = CoachStateMachineTests.Turn(),
                Messages = Array.Empty<CoachHistoryMessageDto>(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        await Ask(state, "Try me.");
        await state.RetryDurableTurnAsync();

        client.SubmittedConversationTurns.Should().HaveCount(2);
        client.SubmittedConversationTurns[1].OperationId
            .Should().Be(client.SubmittedConversationTurns[0].OperationId);
        client.SubmittedConversationTurns[1].IdempotencyKey
            .Should().Be(client.SubmittedConversationTurns[0].IdempotencyKey);
        state.State.Should().Be(CoachUiState.Ready);
    }

    [Fact]
    public async Task PollPendingOperationAsync_FindsTheTurnWhoseReplyWasLostWithoutRunningItAgain()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        // The turn ran; the response never made it back to this client.
        client.OnSubmitConversationTurn = (id, request) =>
        {
            client.Seed(id, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
            client.Seed(id, CoachMessageRole.Coach, "Sam answered while the wire was down.");
            throw new HttpRequestException("connection reset");
        };

        await Ask(state, "Did that land?");
        state.State.Should().Be(CoachUiState.Offline);

        var pendingOperationId = state.PendingOperationId;
        pendingOperationId.Should().NotBeNull();

        client.OnGetConversationOperation = (id, operationId) => new CoachTurnOperationDto
        {
            OperationId = operationId,
            ConversationId = id,
            State = CoachTurnOperationState.Completed,
            Result = CoachStateMachineTests.Turn(),
            Messages = client.ConversationMessages[id],
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await state.PollPendingOperationAsync();

        client.SubmitConversationTurnCalls.Should().Be(1, "polling asks what happened, it does not ask again");
        state.Timeline.Count(m => m.Kind == CoachTimelineKind.LearnerMessage && m.ReadableText() == "Did that land?")
            .Should().Be(1);
        state.Timeline.Should().Contain(m => m.ReadableText() == "Sam answered while the wire was down.");
        state.State.Should().Be(CoachUiState.Ready);
    }

    [Fact]
    public async Task PollPendingOperationAsync_KeepsTheTurnRecoverableWhileItIsStillRunning()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        client.OnSubmitConversationTurn = (_, _) => throw new HttpRequestException("dropped");
        await Ask(state, "Still going?");

        client.OnGetConversationOperation = (id, operationId) => new CoachTurnOperationDto
        {
            OperationId = operationId,
            ConversationId = id,
            State = CoachTurnOperationState.Running,
            Messages = Array.Empty<CoachHistoryMessageDto>(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await state.PollPendingOperationAsync();

        state.HasRecoverableTurn.Should().BeTrue();
        state.PendingOperationId.Should().NotBeNull("asking again must not resend the turn");
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task CancelRunAsync_CancelsTheDurableOperationRatherThanOnlyTheLocalRequest()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var gate = new TaskCompletionSource();

        client.OnSubmitConversationTurn = (id, request) =>
        {
            gate.TrySetResult();
            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = id,
                State = CoachTurnOperationState.Pending,
                Messages = Array.Empty<CoachHistoryMessageDto>(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        client.OnGetConversationOperation = (id, operationId) => new CoachTurnOperationDto
        {
            OperationId = operationId,
            ConversationId = id,
            State = CoachTurnOperationState.Cancelled,
            CancelRequested = true,
            Messages = Array.Empty<CoachHistoryMessageDto>(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        state.Draft = "Cancel me.";
        var run = state.SendDraftAsync();
        await gate.Task;
        await state.CancelRunAsync();
        await run;

        client.CancelConversationTurnCalls.Should().BeGreaterThan(0,
            "a durable turn keeps running on the server unless the server is told to stop");
        state.State.Should().Be(CoachUiState.Ready);
    }

    // ---------------------------------------------------------------- closed conversations

    [Fact]
    public async Task OpenConversationAsync_ReportsAClosedConversationAsReadableButNotWritable()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1", isClosed: true);
        client.Seed("c-1", CoachMessageRole.Coach, "Archived thread.");

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        state.IsConversationClosed.Should().BeTrue();
        state.Timeline.Should().ContainSingle().Which.ReadableText().Should().Be("Archived thread.");
    }

    // ---------------------------------------------------------------- flag off

    [Fact]
    public async Task AskAsync_WithoutADurableConversationStillUsesTheSessionOnlyPath()
    {
        var (state, _, client) = Create(durable: false);
        await state.OpenAsync(CoachPresentation.Overlay);

        await Ask(state, "Legacy still works.");

        client.SubmittedTurns.Should().HaveCount(1);
        client.SubmittedConversationTurns.Should().BeEmpty();
        state.State.Should().Be(CoachUiState.Ready);
    }

    // ---------------------------------------------------------------- reset

    [Fact]
    public async Task Reset_ForgetsTheDurableConversationAlongWithEverythingElse()
    {
        var (state, _, client) = Create();
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Coach, "Something.");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        state.Reset();

        state.ConversationId.Should().BeNull();
        state.IsDurableHistoryEnabled.Should().BeFalse();
        state.Timeline.Should().BeEmpty();
        state.PendingOperationId.Should().BeNull();
    }

    /// <summary>Types a question into the composer and sends it, the way a learner does.</summary>
    private static async Task Ask(CoachWorkspaceState state, string text)
    {
        state.Draft = text;
        await state.SendDraftAsync();
    }
}
