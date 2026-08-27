using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression cover for the E2E defect found on 2026-08-15: with a valid pending suggestion the
/// learner typed "Maybe.", the API correctly answered with a focused clarification and preserved
/// the suggestion, and the UI rendered the question AND a warning card reading "The coach stopped
/// before finishing. Nothing changed." with Try again / Keep Today's Plan.
/// </summary>
/// <remarks>
/// <para>
/// The server reports an asked clarification as <c>Status=Incomplete</c> with
/// <c>StopReason=ClarificationRequested</c> — see <c>CoachSessionService.AskClarificationAsync</c>.
/// The client checked <c>Status == Incomplete</c> before it checked for a clarification, so an
/// expected conversational turn was classified as a stopped run.
/// </para>
/// <para>
/// The payloads below mirror that method exactly, including the detail that when a suggestion is
/// pending the server sends <c>SessionStatus = SuggestionPending</c> rather than
/// <c>AwaitingClarification</c>.
/// </para>
/// </remarks>
public class CoachClarificationStateTests
{
    private const string Question = "Do you want me to turn audio on for Today's Plan?";

    /// <summary>Exactly what <c>AskClarificationAsync</c> returns while a suggestion is pending.</summary>
    private static CoachTurnResponse ClarificationTurn(
        int clarificationsRemaining = 1,
        PendingCoachSuggestionDto? suggestion = null) =>
        CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Incomplete,
            stopReason: CoachStopReason.ClarificationRequested,
            sessionStatus: CoachSessionStatus.SuggestionPending,
            suggestion: suggestion ?? CoachStateMachineTests.Suggestion(),
            clarifyingQuestion: Question,
            clarificationsRemaining: clarificationsRemaining);

    // ---------------------------------------------------------------- the defect

    [Fact]
    public void AnAskedClarificationIsNotAStoppedRun()
    {
        CoachStateMachine.FromTurn(ClarificationTurn())
            .Should().Be(CoachUiState.Clarification,
                "Incomplete plus ClarificationRequested is an expected turn, not a failure");
    }

    [Fact]
    public void AnAskedClarificationNeverRendersTheStoppedOrFailedCard()
    {
        var state = CoachStateMachine.FromTurn(ClarificationTurn());

        state.Should().NotBe(CoachUiState.Incomplete, "that card says 'The coach stopped before finishing'");
        state.Should().NotBe(CoachUiState.Failed);
    }

    [Fact]
    public void AClarificationLeavesTheComposerUsable()
    {
        var state = CoachStateMachine.FromTurn(ClarificationTurn());

        CoachStateMachine.CanSubmit(state).Should().BeTrue("the learner has to be able to answer");
        CoachStateMachine.IsBusy(state).Should().BeFalse();
        CoachStateMachine.IsTerminal(state).Should().BeFalse();
    }

    [Fact]
    public void AClarificationWithoutAPendingSuggestionIsStillAClarification()
    {
        // The server sends SessionStatus=AwaitingClarification when nothing is pending.
        var turn = CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Incomplete,
            stopReason: CoachStopReason.ClarificationRequested,
            sessionStatus: CoachSessionStatus.AwaitingClarification,
            clarifyingQuestion: Question,
            clarificationsRemaining: 1);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Clarification);
    }

    // ---------------------------------------------------------------- budget exhausted

    [Fact]
    public void TheGiveUpTurnIsABinaryChoiceNotAStoppedRun()
    {
        // CoachSessionService's "I still could not tell what to change" path: same stop reason,
        // no question attached. It must offer a choice, not Try again.
        var turn = CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Incomplete,
            stopReason: CoachStopReason.ClarificationRequested,
            sessionStatus: CoachSessionStatus.Active,
            suggestion: CoachStateMachineTests.Suggestion(),
            clarifyingQuestion: null,
            clarificationsRemaining: 0);

        var state = CoachStateMachine.FromTurn(turn);

        state.Should().Be(CoachUiState.ClarificationLimitReached);
        state.Should().NotBe(CoachUiState.Incomplete);
        CoachStateMachine.CanSubmit(state).Should().BeTrue();
    }

    [Fact]
    public void TheLastClarificationStillPresentsABinaryChoice()
    {
        CoachStateMachine.FromTurn(ClarificationTurn(clarificationsRemaining: 0))
            .Should().Be(CoachUiState.ClarificationLimitReached, "never loop past the budget");
    }

    // ---------------------------------------------------------------- genuine failures survive

    [Fact]
    public void AGenuinelyStoppedRunStillReportsIncomplete()
    {
        // The reorder must not swallow real early stops.
        var turn = CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Incomplete,
            stopReason: CoachStopReason.IterationLimit);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Incomplete);
    }

    [Theory]
    [InlineData(CoachStopReason.Timeout)]
    [InlineData(CoachStopReason.ToolFailure)]
    [InlineData(CoachStopReason.OutputTokenLimit)]
    [InlineData(CoachStopReason.ConcurrencyLimit)]
    public void OtherEarlyStopsAreUnaffected(CoachStopReason stopReason)
    {
        var turn = CoachStateMachineTests.Turn(status: CoachTurnStatus.Incomplete, stopReason: stopReason);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Incomplete);
    }

    [Fact]
    public void AFailedTurnStillOutranksAClarification()
    {
        var turn = CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Failed,
            stopReason: CoachStopReason.ClarificationRequested,
            clarifyingQuestion: Question);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Failed);
    }

    [Fact]
    public void ExpiryAndRateLimitStillOutrankAClarification()
    {
        CoachStateMachine.FromTurn(CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Incomplete,
            stopReason: CoachStopReason.SessionExpired,
            clarifyingQuestion: Question)).Should().Be(CoachUiState.Expired);

        CoachStateMachine.FromTurn(CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Incomplete,
            stopReason: CoachStopReason.RateLimit,
            clarifyingQuestion: Question)).Should().Be(CoachUiState.Limited);
    }

    // ---------------------------------------------------------------- applied through the state

    [Fact]
    public async Task AnAmbiguousReplyKeepsThePendingPreviewAndWritesNothing()
    {
        var client = new FakeCoachApiClient();
        client.OnGetSession = id => FakeCoachApiClient.Session(id,
            CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");

        client.OnSubmitTurn = _ => ClarificationTurn();
        state.Draft = "Maybe.";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Clarification);
        state.PendingSuggestion.Should().NotBeNull("the preview must survive an ambiguous reply");
        state.Receipts.Should().BeEmpty("nothing may be written");
        state.PlanState!.PlanVersion.Should().Be("v1", "Today's Plan is untouched");
        state.CanSubmit.Should().BeTrue("the composer stays usable so the learner can answer");
    }

    [Fact]
    public async Task AnAmbiguousReplyAnnouncesTheQuestionRatherThanAnAlert()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => ClarificationTurn();
        state.Draft = "Maybe.";
        await state.SendDraftAsync();

        state.PoliteAnnouncementKey.Should().Be("Coach_AnnounceClarification");
        state.AlertKey.Should().BeNull("a question is not an error");
    }

    [Fact]
    public void TheClarificationStatesCarryNoRetryAffordance()
    {
        // Try again belongs to Incomplete/Failed/Offline only. A clarification is answered by
        // choosing, not by resubmitting the ambiguous turn.
        foreach (var state in new[] { CoachUiState.Clarification, CoachUiState.ClarificationLimitReached })
        {
            CoachStateMachine.AnnouncementKey(state).Should().NotBe("Coach_Incomplete");
            CoachStateMachine.AnnouncementKey(state).Should().NotBe("Coach_Failed");
        }
    }
}
