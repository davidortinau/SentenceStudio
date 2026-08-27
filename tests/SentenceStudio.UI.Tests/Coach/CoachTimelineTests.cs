using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The conversation is one chronological stream.
/// </summary>
/// <remarks>
/// <para>
/// The live defect this pins: two learner questions rendered back to back, then a single answer
/// and a receipt. Two things caused it. Artifacts were grouped by type — every receipt and
/// suggestion rendered after every message — and a typed turn submitted while another was in
/// flight was silently dropped by the busy guard after its question had already been shown.
/// </para>
/// <para>
/// Sequence is client-allocated and monotonic, and every artifact of one exchange shares its
/// turn, so a slow reply lands beside its own question rather than after a later one.
/// </para>
/// </remarks>
public class CoachTimelineTests
{
    private static CoachMessageDto Reply(string id, string text, CoachMessageKind kind = CoachMessageKind.Text) => new()
    {
        MessageId = id,
        Role = CoachMessageRole.Coach,
        Kind = kind,
        Text = text,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> OpenAsync()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);
        return (state, client);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the expected state should be reached within the timeout");
    }

    private static IReadOnlyList<string> Texts(CoachWorkspaceState state) => state.Timeline
        .Select(e => e.Kind == CoachTimelineKind.Receipt ? "[receipt]" : e.Message?.Text ?? "[?]")
        .ToList();

    // ---------------------------------------------------------------- ordering within a turn

    [Fact]
    public async Task AReceiptRendersInsideTheTurnThatProducedItNotAfterEverything()
    {
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest),
            messages: [Reply("m-1", "Done.")]);

        state.Draft = "make it 30 minutes";
        await state.SendDraftAsync();

        Texts(state).Should().Equal(["make it 30 minutes", "Done.", "[receipt]"]);
    }

    [Fact]
    public async Task TwoSequentialTurnsStayPaired()
    {
        var (state, client) = await OpenAsync();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages: [Reply("m-1", "First reply.")]);
        state.Draft = "first question";
        await state.SendDraftAsync();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest),
            messages: [Reply("m-2", "Second reply.")]);
        state.Draft = "second question";
        await state.SendDraftAsync();

        Texts(state).Should().Equal(
        [
            "first question", "First reply.",
            "second question", "Second reply.", "[receipt]"
        ]);
    }

    [Fact]
    public async Task LearnerMessagesAreNotHoistedToTheTop()
    {
        var (state, client) = await OpenAsync();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages: [Reply("m-1", "First reply.")]);
        state.Draft = "first question";
        await state.SendDraftAsync();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages: [Reply("m-2", "Second reply.")]);
        state.Draft = "second question";
        await state.SendDraftAsync();

        var timeline = state.Timeline;
        timeline[0].Kind.Should().Be(CoachTimelineKind.LearnerMessage);
        timeline[1].Kind.Should().Be(CoachTimelineKind.CoachMessage);
        timeline[2].Kind.Should().Be(CoachTimelineKind.LearnerMessage,
            "grouping all learner turns first would be just as wrong as grouping them last");
    }

    // ---------------------------------------------------------------- overlap

    [Fact]
    public async Task ASecondSendWhileBusyIsQueuedNotDropped()
    {
        // The live defect: the second question appeared on screen, the composer cleared, and the
        // turn was discarded by the busy guard. It looked asked and was never sent.
        var (state, client) = await OpenAsync();

        var gate = new TaskCompletionSource();
        var submitted = new List<string>();

        client.OnSubmitTurn = request =>
        {
            lock (submitted)
            {
                submitted.Add(request.Text ?? string.Empty);
            }

            if (submitted.Count == 1)
            {
                gate.Task.GetAwaiter().GetResult();
            }

            return CoachStateMachineTests.Turn(
                messages: [Reply($"m-{submitted.Count}", $"Reply {submitted.Count}.")]);
        };

        state.Draft = "first question";
        var first = Task.Run(() => state.SendDraftAsync());
        await WaitForAsync(() => state.State == CoachUiState.Running);

        state.Draft = "second question";
        var second = Task.Run(() => state.SendDraftAsync());

        // Both questions are on screen immediately, in submission order.
        await WaitForAsync(() => state.Timeline.Count(e => e.Kind == CoachTimelineKind.LearnerMessage) == 2);

        gate.SetResult();
        await Task.WhenAll(first, second);

        submitted.Should().Equal(["first question", "second question"],
            "a queued turn is still sent, and in the order it was asked");
    }

    [Fact]
    public async Task ALateResponseLandsBesideItsOwnQuestion()
    {
        var (state, client) = await OpenAsync();

        var gate = new TaskCompletionSource();
        var count = 0;

        client.OnSubmitTurn = request =>
        {
            var index = Interlocked.Increment(ref count);

            if (index == 1)
            {
                gate.Task.GetAwaiter().GetResult();
            }

            return CoachStateMachineTests.Turn(messages: [Reply($"m-{index}", $"Reply to {request.Text}.")]);
        };

        state.Draft = "first question";
        var first = Task.Run(() => state.SendDraftAsync());
        await WaitForAsync(() => state.State == CoachUiState.Running);

        state.Draft = "second question";
        var second = Task.Run(() => state.SendDraftAsync());
        await WaitForAsync(() => state.Timeline.Count(e => e.Kind == CoachTimelineKind.LearnerMessage) == 2);

        gate.SetResult();
        await Task.WhenAll(first, second);

        Texts(state).Should().Equal(
        [
            "first question", "Reply to first question.",
            "second question", "Reply to second question."
        ], "each reply belongs to the question that asked for it");
    }

    // ---------------------------------------------------------------- failure paths

    [Fact]
    public async Task AFailedTurnLeavesItsQuestionInPlace()
    {
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => throw new CoachApiException(
            System.Net.HttpStatusCode.InternalServerError, null, "Server error", "boom");

        state.Draft = "did this send";
        await state.SendDraftAsync();

        Texts(state).Should().Equal(["did this send"]);
        state.State.Should().Be(CoachUiState.Failed);
    }

    [Fact]
    public async Task AFailedTurnDoesNotBlockTheNextOne()
    {
        // The gate must be released on every path, or one failure would wedge the composer.
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => throw new HttpRequestException("offline");

        state.Draft = "first";
        await state.SendDraftAsync();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages: [Reply("m-1", "Recovered.")]);
        state.Draft = "second";
        await state.SendDraftAsync();

        Texts(state).Should().Equal(["first", "second", "Recovered."]);
    }

    // ---------------------------------------------------------------- mixed turn

    [Fact]
    public async Task AMixedAnswerAndSuggestionStayInsideOneTurn()
    {
        var (state, client) = await OpenAsync();
        var answer = CoachAnswerStateTests.KoreanContrastAnswer();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            sessionStatus: CoachSessionStatus.SuggestionPending,
            suggestion: CoachStateMachineTests.Suggestion(),
            messages: [CoachAnswerStateTests.AnswerMessage(answer)],
            answer: answer);

        state.Draft = "explain this and tidy my plan";
        await state.SendDraftAsync();

        var timeline = state.Timeline;
        timeline[0].Kind.Should().Be(CoachTimelineKind.LearnerMessage);
        timeline[1].Kind.Should().Be(CoachTimelineKind.CoachMessage);
        timeline[1].Answer.Should().NotBeNull("the structured answer is paired to its message");

        state.PendingSuggestionTurn.Should().Be(timeline[0].TurnSequence,
            "the suggestion belongs to the exchange that offered it");
    }

    // ---------------------------------------------------------------- de-duplication

    [Fact]
    public async Task AResentServerMessageDoesNotAppearTwice()
    {
        var (state, client) = await OpenAsync();
        var repeated = Reply("m-stable", "Same message.");

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages: [repeated]);

        state.Draft = "first";
        await state.SendDraftAsync();
        state.Draft = "second";
        await state.SendDraftAsync();

        state.Timeline.Count(e => e.Message?.MessageId == "m-stable")
            .Should().Be(1, "an id that repeats is the same artifact, not a new one");
    }

    [Fact]
    public async Task DeDuplicationDoesNotReorderWhatIsAlreadyThere()
    {
        var (state, client) = await OpenAsync();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages: [Reply("m-1", "First reply.")]);
        state.Draft = "first";
        await state.SendDraftAsync();

        // The second turn repeats the first message and adds one.
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages: [Reply("m-1", "First reply."), Reply("m-2", "Second reply.")]);
        state.Draft = "second";
        await state.SendDraftAsync();

        Texts(state).Should().Equal(["first", "First reply.", "second", "Second reply."],
            "the repeated message keeps its original place");
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public async Task TheStreamIsClearedOnReset()
    {
        var (state, _) = await OpenAsync();
        state.Draft = "something";
        await state.SendDraftAsync();

        state.Reset();

        state.Timeline.Should().BeEmpty();
        state.PendingSuggestionTurn.Should().BeNull();
        state.HasLearnerTurn.Should().BeFalse();
    }

    [Fact]
    public async Task AnotherSessionsStreamNeverLeaksIn()
    {
        var (state, client) = await OpenAsync();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);

        state.Draft = "session A question";
        await state.SendDraftAsync();

        await state.OpenAsync(CoachPresentation.Overlay, "session-b");

        state.Timeline.Should().BeEmpty();
    }

    [Fact]
    public async Task SequencesAreMonotonicAndNeverReused()
    {
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest),
            messages: [Reply("m-1", "Reply.")]);

        state.Draft = "one";
        await state.SendDraftAsync();
        state.Draft = "two";
        await state.SendDraftAsync();

        var sequences = state.Timeline.Select(e => e.Sequence).ToList();

        sequences.Should().OnlyHaveUniqueItems();
        sequences.Should().BeInAscendingOrder("the rendered order is the sequence order");
    }
}
