using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The learner's own words belong in the conversation.
///
/// The server keeps no plaintext transcript, so it never echoes a learner turn back. If the
/// client does not hold them, the learner watches their question disappear and only an answer
/// arrive — which reads as if the coach is talking to itself. These tests pin the turn to the
/// screen from the moment it is sent, through every way a run can end.
/// </summary>
public class CoachLearnerMessageTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient();
        return (new CoachWorkspaceState(client), client);
    }

    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> OpenAsync()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);
        return (state, client);
    }

    private static IReadOnlyList<CoachMessageDto> LearnerTurns(CoachWorkspaceState state) =>
        state.Messages.Where(m => m.Role == CoachMessageRole.Learner).ToList();

    private static CoachMessageDto Reply(string id, string text) => new()
    {
        MessageId = id,
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.Text,
        Text = text,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the expected state should be reached within the timeout");
    }

    // ------------------------------------------------------------- appears immediately

    [Fact]
    public async Task TheLearnerTurnIsVisibleBeforeTheResponseArrives()
    {
        var (state, client) = await OpenAsync();

        var seenWhileInFlight = 0;
        client.OnSubmitTurn = _ =>
        {
            // Observed from inside the request: the question is already on screen.
            seenWhileInFlight = LearnerTurns(state).Count;
            return CoachStateMachineTests.Turn();
        };

        state.Draft = "How do I use 은/는?";
        await state.SendDraftAsync();

        seenWhileInFlight.Should().Be(1, "the turn is appended before the request is awaited");
    }

    [Fact]
    public async Task TheComposerClearsButTheQuestionIsKept()
    {
        var (state, _) = await OpenAsync();

        state.Draft = "What is the difference?";
        await state.SendDraftAsync();

        state.Draft.Should().BeEmpty("the composer is ready for the next question");
        LearnerTurns(state).Single().Text.Should().Be("What is the difference?");
    }

    [Fact]
    public async Task AnEmptyOrWhitespaceDraftIsNotAdded()
    {
        var (state, _) = await OpenAsync();

        state.Draft = "   ";
        await state.SendDraftAsync();

        state.Messages.Should().BeEmpty("nothing was submitted, so nothing is shown");
    }

    // ------------------------------------------------------------- ordering

    [Fact]
    public async Task TheLearnerTurnIsOrderedBeforeTheReply()
    {
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages: [Reply("m-1", "Use 은/는 for the topic.")]);

        state.Draft = "How do I use 은/는?";
        await state.SendDraftAsync();

        state.Messages.Select(m => m.Role).Should().Equal(
            [CoachMessageRole.Learner, CoachMessageRole.Coach],
            "a conversation reads question then answer");
    }

    [Fact]
    public async Task MultipleTurnsStayInTheOrderTheyWereAsked()
    {
        var (state, client) = await OpenAsync();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages: [Reply("m-1", "First reply.")]);
        state.Draft = "First question";
        await state.SendDraftAsync();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages: [Reply("m-2", "Second reply.")]);
        state.Draft = "Second question";
        await state.SendDraftAsync();

        state.Messages.Select(m => m.Text).Should().Equal(
            ["First question", "First reply.", "Second question", "Second reply."]);
    }

    // ------------------------------------------------------------- survives every ending

    [Fact]
    public async Task TheQuestionSurvivesAFailedRun()
    {
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => throw new CoachApiException(
            System.Net.HttpStatusCode.InternalServerError, null, "Server error", "boom");

        state.Draft = "Why did that happen?";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Failed);
        LearnerTurns(state).Single().Text.Should().Be("Why did that happen?",
            "a failure must not swallow what the learner asked");
    }

    [Fact]
    public async Task TheQuestionSurvivesGoingOffline()
    {
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => throw new HttpRequestException("no network");

        state.Draft = "Still here?";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Offline);
        LearnerTurns(state).Single().Text.Should().Be("Still here?");
    }

    [Fact]
    public async Task TheQuestionSurvivesCancellation()
    {
        var (state, client) = await OpenAsync();

        var gate = new TaskCompletionSource();
        client.OnSubmitTurn = _ =>
        {
            gate.Task.GetAwaiter().GetResult();
            return CoachStateMachineTests.Turn();
        };

        state.Draft = "Take your time";
        var run = Task.Run(() => state.SendDraftAsync());
        await WaitForAsync(() => state.State == CoachUiState.Running);

        await state.CancelRunAsync();
        gate.SetResult();
        await run;

        LearnerTurns(state).Single().Text.Should().Be("Take your time",
            "stopping the coach does not unask the question");
    }

    [Fact]
    public async Task TheQuestionSurvivesARefusalThatChangesNothing()
    {
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Rejected,
            stopReason: CoachStopReason.ValidationFailed,
            sessionStatus: CoachSessionStatus.Active,
            messages: [Reply("m-1", "I could not find a change that would help today.")]);

        state.Draft = "Suggest something useful";
        await state.SendDraftAsync();

        LearnerTurns(state).Single().Text.Should().Be("Suggest something useful");
    }

    // ------------------------------------------------------------- no duplication

    [Fact]
    public async Task RetryingDoesNotAskTheQuestionTwice()
    {
        // Pins the retry path itself: it replays the stored request and must not route back
        // through the append. (It holds because RetryLastAsync reuses the request directly, not
        // because of the id guard in AppendLearnerMessage — that guard is unreachable defence.)
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => throw new HttpRequestException("no network");

        state.Draft = "One question";
        await state.SendDraftAsync();
        LearnerTurns(state).Should().HaveCount(1);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn();
        await state.RetryLastAsync();

        LearnerTurns(state).Should().HaveCount(1,
            "a retry replays the same turn; it is not a second question");
    }

    [Fact]
    public async Task AskingTheSameThingTwiceShowsItTwice()
    {
        // The mirror of the retry case: two deliberate sends are two real questions, even when
        // the words match. Dedupe must not swallow one.
        var (state, _) = await OpenAsync();

        state.Draft = "Again?";
        await state.SendDraftAsync();
        state.Draft = "Again?";
        await state.SendDraftAsync();

        LearnerTurns(state).Should().HaveCount(2);
    }

    [Fact]
    public async Task AServerThatEchoesTheLearnerTurnDoesNotDoubleIt()
    {
        // Defensive: today the server never echoes learner text. If it ever starts, the learner
        // must not see their own question twice.
        var (state, client) = await OpenAsync();
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages:
        [
            new CoachMessageDto
            {
                MessageId = "server-echo",
                Role = CoachMessageRole.Learner,
                Kind = CoachMessageKind.Text,
                Text = "Echoed question",
                CreatedAtUtc = DateTime.UtcNow
            }
        ]);

        state.Draft = "Echoed question";
        await state.SendDraftAsync();

        LearnerTurns(state).Should().HaveCount(1);
    }

    // ------------------------------------------------------------- mixed turn

    [Fact]
    public async Task AMixedAnswerAndSuggestionStillLeadsWithTheQuestion()
    {
        var (state, client) = await OpenAsync();
        var answer = CoachAnswerStateTests.KoreanContrastAnswer();
        var pending = CoachStateMachineTests.Suggestion();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            sessionStatus: CoachSessionStatus.SuggestionPending,
            suggestion: pending,
            messages: [CoachAnswerStateTests.AnswerMessage(answer)],
            answer: answer);

        state.Draft = "Explain this and tidy my plan";
        await state.SendDraftAsync();

        state.Messages.First().Role.Should().Be(CoachMessageRole.Learner);
        state.Messages.First().Text.Should().Be("Explain this and tidy my plan");
        state.PendingSuggestion.Should().NotBeNull("the suggestion is still offered");
    }

    // ------------------------------------------------------------- privacy boundary

    [Fact]
    public async Task LocalTurnsSurviveAServerReadThatDoesReturnHistory()
    {
        // Guards a future server that persists coach turns. Its history still cannot contain the
        // learner's own words, so a reopen must merge rather than replace, or the learner's
        // questions vanish while the replies stay.
        var (state, client) = await OpenAsync();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);

        state.Draft = "My question";
        await state.SendDraftAsync();

        var sessionId = state.SessionId!;
        client.OnGetSession = id => FakeCoachApiClient.Session(
            id,
            messages: [Reply("server-1", "A remembered reply.")]);

        state.Close();
        await state.OpenAsync(CoachPresentation.Overlay, sessionId);

        state.Messages.Select(m => m.Text).Should().Contain("My question",
            "server history has no learner text, so the local copy is all there is");
        state.Messages.Select(m => m.Text).Should().Contain("A remembered reply.");
    }

    [Fact]
    public async Task ADifferentSessionsHistoryNeverKeepsLocalTurns()
    {
        var (state, client) = await OpenAsync();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);

        state.Draft = "Session A question";
        await state.SendDraftAsync();

        client.OnGetSession = id => FakeCoachApiClient.Session(
            id,
            messages: [Reply("server-b", "Session B reply.")]);

        await state.OpenAsync(CoachPresentation.Overlay, "session-b");

        state.Messages.Select(m => m.Text).Should().NotContain("Session A question",
            "one session's words must never appear in another");
    }

    [Fact]
    public async Task LocalTurnsAreDroppedWhenTheSessionIsReset()
    {
        var (state, _) = await OpenAsync();
        state.Draft = "Forget this";
        await state.SendDraftAsync();

        state.Reset();

        state.Messages.Should().BeEmpty("a reset is a real reload boundary");
    }

}
