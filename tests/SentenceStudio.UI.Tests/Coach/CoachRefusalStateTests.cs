using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression cover for the E2E defect found on 2026-08-15: on a fresh baseline session with
/// sparse data, "Could you suggest one useful change to today's plan?" returned the deterministic
/// no-op — no pending suggestion, no write — and the UI rendered a <c>role="alert"</c> card
/// reading "I could not update Today's Plan. Nothing changed." with Try again / Keep Today's Plan.
/// </summary>
/// <remarks>
/// <para>
/// That is a safe, deliberate refusal, not an operational failure. Every refusal path in
/// <c>CoachSessionService</c> — the suggestion validator finding no effective change, an unusable
/// model answer, a failed ownership check, and an answer-leak embargo — returns the same SHAPE:
/// <c>Status = Rejected</c>, <c>StopReason = ValidationFailed</c>, no receipt, and a Notice
/// message. The client had folded Rejected into Failed.
/// </para>
/// <para>
/// Detection is on that shape only. The copy belongs to the server and the model and must never
/// be string-matched.
/// </para>
/// </remarks>
public class CoachRefusalStateTests
{
    /// <summary>The shared shape of every server refusal path.</summary>
    private static CoachTurnResponse RefusalTurn(
        PendingCoachSuggestionDto? preservedSuggestion = null,
        CoachSessionStatus sessionStatus = CoachSessionStatus.Active) =>
        CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Rejected,
            stopReason: CoachStopReason.ValidationFailed,
            sessionStatus: sessionStatus,
            suggestion: preservedSuggestion,
            messages: [Notice("I could not find a change that would help today, so Today\u2019s Plan is unchanged.")]);

    private static CoachMessageDto Notice(string text) => new()
    {
        MessageId = "m-notice",
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.Notice,
        Text = text,
        CreatedAtUtc = DateTime.UtcNow
    };

    // ---------------------------------------------------------------- the defect

    [Fact]
    public void ANoEffectiveSuggestionIsNeutralNotAFailure()
    {
        var state = CoachStateMachine.FromTurn(RefusalTurn());

        state.Should().Be(CoachUiState.Ready);
        state.Should().NotBe(CoachUiState.Failed, "no alert and no Try again for a safe no-op");
        state.Should().NotBe(CoachUiState.Incomplete);
    }

    [Fact]
    public void ANoEffectiveSuggestionLeavesTheWorkspaceUsable()
    {
        var state = CoachStateMachine.FromTurn(RefusalTurn());

        CoachStateMachine.CanSubmit(state).Should().BeTrue();
        CoachStateMachine.IsBusy(state).Should().BeFalse();
        CoachStateMachine.IsTerminal(state).Should().BeFalse();
    }

    [Fact]
    public void ARefusalNeverAnnouncesAnError()
    {
        // Coach_Failed is the alert copy the learner should never hear for a refusal.
        CoachStateMachine.AnnouncementKey(CoachStateMachine.FromTurn(RefusalTurn()))
            .Should().NotBe("Coach_Failed");
    }

    [Fact]
    public void ARefusalNeverWithdrawsAnUnansweredOffer()
    {
        // The unusable-model-answer path deliberately preserves a pending suggestion; the UI
        // must keep showing the card rather than dropping the learner back to a blank Ready.
        var turn = RefusalTurn(
            preservedSuggestion: CoachStateMachineTests.Suggestion(),
            sessionStatus: CoachSessionStatus.SuggestionPending);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.SuggestionPending);
    }

    // ---------------------------------------------------------------- genuine failures survive

    [Theory]
    [InlineData(CoachStopReason.ToolFailure)]
    [InlineData(CoachStopReason.Timeout)]
    [InlineData(CoachStopReason.OutputTokenLimit)]
    [InlineData(CoachStopReason.IterationLimit)]
    [InlineData(CoachStopReason.ConcurrencyLimit)]
    public void RealEarlyStopsStayIncomplete(CoachStopReason stopReason)
    {
        var turn = CoachStateMachineTests.Turn(status: CoachTurnStatus.Incomplete, stopReason: stopReason);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Incomplete);
    }

    [Theory]
    [InlineData(CoachStopReason.Failed)]
    [InlineData(CoachStopReason.ToolFailure)]
    [InlineData(CoachStopReason.ValidationFailed)]
    public void AFailedTurnIsStillAFailureWhateverStoppedIt(CoachStopReason stopReason)
    {
        // Status=Failed is the server saying something broke. Only Rejected is a refusal.
        var turn = CoachStateMachineTests.Turn(status: CoachTurnStatus.Failed, stopReason: stopReason);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Failed);
    }

    [Fact]
    public void ARejectionForSomeOtherReasonIsStillAFailure()
    {
        // The refusal shape is narrow on purpose: Rejected alone does not earn a free pass.
        var turn = CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Rejected,
            stopReason: CoachStopReason.InputRejected);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Failed);
    }

    [Fact]
    public void ARejectionThatSomehowWroteSomethingIsNotTreatedAsANoOp()
    {
        // Defensive: "nothing changed" must never be claimed while a receipt exists.
        var turn = CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Rejected,
            stopReason: CoachStopReason.ValidationFailed,
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Failed);
    }

    [Fact]
    public void ExpiryAndRateLimitStillOutrankARefusal()
    {
        CoachStateMachine.FromTurn(CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Rejected,
            stopReason: CoachStopReason.SessionExpired)).Should().Be(CoachUiState.Expired);

        CoachStateMachine.FromTurn(CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Rejected,
            stopReason: CoachStopReason.RateLimit)).Should().Be(CoachUiState.Limited);
    }

    [Fact]
    public void ARefusalCarryingAQuestionIsStillAClarification()
    {
        var turn = CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Rejected,
            stopReason: CoachStopReason.ValidationFailed,
            clarifyingQuestion: "Should I add a short speaking activity?",
            clarificationsRemaining: 1);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Clarification);
    }

    // ---------------------------------------------------------------- applied through the state

    [Fact]
    public async Task TheNoOpTurnKeepsTheNoticeAndWritesNothing()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => RefusalTurn();
        state.Draft = "Could you suggest one useful change to today's plan?";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Ready);
        state.AlertKey.Should().BeNull("a safe no-op must not raise role=alert");
        state.Receipts.Should().BeEmpty();
        state.PendingSuggestion.Should().BeNull();
        state.PlanState!.PlanVersion.Should().Be("v1", "Today's Plan is untouched");

        // The coach's own explanation still reaches the learner, in the conversation.
        state.Messages.Should().ContainSingle(m => m.Kind == CoachMessageKind.Notice);
        state.CanSubmit.Should().BeTrue();
    }

    [Fact]
    public async Task AGenuineToolFailureStillRaisesTheErrorPath()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Incomplete, stopReason: CoachStopReason.ToolFailure);
        state.Draft = "suggest something";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Incomplete, "a real early stop keeps its Try again");
    }
}
