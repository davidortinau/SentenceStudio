using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression cover for the E2E defect found on 2026-08-15: after an ambiguous reply the
/// suggestion card rendered its base "Include it" / "Not now" pair AND, below the clarification
/// question, a second identical "Yes, update it" / "Not now" pair — four buttons for one binary
/// decision, and a duplicated accessible action set.
/// </summary>
/// <remarks>
/// LC-SUG-01 / LC-AMB-01 require exactly two consequential actions on a pending suggestion, in
/// every state. A clarification re-frames that choice; it never adds to it.
///
/// The card's action decision lives in <see cref="CoachStateMachine.SuggestionActions"/> so the
/// contract is assertable without a renderer: the resolver returns ONE accept key and ONE reject
/// key, and the component renders exactly one row from it.
/// </remarks>
public class CoachSuggestionActionsTests
{
    private const string Question = "Should I add the speaking activity to Today's Plan now?";

    // ---------------------------------------------------------------- exactly two actions

    [Fact]
    public void ANormalPendingSuggestionOffersExactlyOneAcceptAndOneReject()
    {
        var actions = CoachStateMachine.SuggestionActions(
            CoachUiState.SuggestionPending, hasPendingSuggestion: true, hasClarification: false);

        actions.IsVisible.Should().BeTrue();
        actions.ConsequentialActionCount.Should().Be(2);
        actions.AcceptLabelKey.Should().Be("Coach_Accept");
        actions.RejectLabelKey.Should().Be("Coach_Reject");
        actions.ShowClarification.Should().BeFalse();
    }

    [Fact]
    public void AClarificationStillOffersExactlyOneAcceptAndOneReject()
    {
        // The defect: this state used to produce a SECOND pair on top of the base pair.
        var actions = CoachStateMachine.SuggestionActions(
            CoachUiState.Clarification, hasPendingSuggestion: true, hasClarification: true);

        actions.ConsequentialActionCount.Should().Be(2, "a clarification re-frames the choice, it does not add to it");
        actions.AcceptLabelKey.Should().Be("Coach_ClarifyYes", "the answer must read as an unambiguous yes");
        actions.RejectLabelKey.Should().Be("Coach_Reject");
        actions.ShowClarification.Should().BeTrue("the focused question is still shown");
    }

    [Fact]
    public void TheClarificationLimitStateAlsoOffersExactlyOnePair()
    {
        var actions = CoachStateMachine.SuggestionActions(
            CoachUiState.ClarificationLimitReached, hasPendingSuggestion: true, hasClarification: true);

        actions.ConsequentialActionCount.Should().Be(2);
        actions.ShowClarification.Should().BeTrue();
        actions.AcceptLabelKey.Should().Be("Coach_ClarifyYes");
    }

    [Theory]
    [InlineData(CoachUiState.SuggestionPending, false)]
    [InlineData(CoachUiState.SuggestionPending, true)]
    [InlineData(CoachUiState.Clarification, true)]
    [InlineData(CoachUiState.ClarificationLimitReached, true)]
    [InlineData(CoachUiState.Applying, false)]
    [InlineData(CoachUiState.Ready, false)]
    public void EveryStateWithAPendingSuggestionExposesTwoActionsAndNoMore(
        CoachUiState state, bool hasClarification)
    {
        var actions = CoachStateMachine.SuggestionActions(state, hasPendingSuggestion: true, hasClarification);

        actions.ConsequentialActionCount.Should().Be(2,
            $"state {state} must never present a second decision pair");
        actions.AcceptLabelKey.Should().NotBeNullOrWhiteSpace();
        actions.RejectLabelKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TheAcceptAndRejectActionsAreNeverTheSameControl()
    {
        foreach (var state in Enum.GetValues<CoachUiState>())
        {
            foreach (var hasClarification in new[] { true, false })
            {
                var actions = CoachStateMachine.SuggestionActions(state, true, hasClarification);

                actions.AcceptLabelKey.Should().NotBe(actions.RejectLabelKey,
                    $"state {state} must offer a real choice");
            }
        }
    }

    // ---------------------------------------------------------------- nothing pending

    [Fact]
    public void NoPendingSuggestionMeansNoConsequentialActions()
    {
        var actions = CoachStateMachine.SuggestionActions(
            CoachUiState.Ready, hasPendingSuggestion: false, hasClarification: false);

        actions.IsVisible.Should().BeFalse();
        actions.ConsequentialActionCount.Should().Be(0);
        actions.ShowClarification.Should().BeFalse();
    }

    [Fact]
    public void AClarificationWithNothingPendingStillOffersNoActions()
    {
        // The card is not mounted at all, so it must not claim actions it cannot perform.
        var actions = CoachStateMachine.SuggestionActions(
            CoachUiState.Clarification, hasPendingSuggestion: false, hasClarification: true);

        actions.IsVisible.Should().BeFalse();
        actions.ConsequentialActionCount.Should().Be(0);
    }

    // ---------------------------------------------------------------- question gating

    [Fact]
    public void TheQuestionIsOnlyShownWhenOneWasActuallyAsked()
    {
        // A clarification state with no question text must not render an empty question block.
        CoachStateMachine.SuggestionActions(CoachUiState.Clarification, true, hasClarification: false)
            .ShowClarification.Should().BeFalse();
    }

    [Fact]
    public void AStaleQuestionIsNotShownOutsideTheClarificationStates()
    {
        // Once the learner moves on, an earlier question must stop framing the actions.
        CoachStateMachine.SuggestionActions(CoachUiState.SuggestionPending, true, hasClarification: true)
            .ShowClarification.Should().BeFalse();

        CoachStateMachine.SuggestionActions(CoachUiState.SuggestionPending, true, hasClarification: true)
            .AcceptLabelKey.Should().Be("Coach_Accept", "the base offer wording returns");
    }

    // ---------------------------------------------------------------- end to end through state

    [Fact]
    public async Task AnAmbiguousReplyKeepsOnePairAndTheRationale()
    {
        var client = new FakeCoachApiClient();
        client.OnGetSession = id => FakeCoachApiClient.Session(id,
            CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Incomplete,
            stopReason: CoachStopReason.ClarificationRequested,
            sessionStatus: CoachSessionStatus.SuggestionPending,
            suggestion: CoachStateMachineTests.Suggestion(),
            clarifyingQuestion: Question,
            clarificationsRemaining: 1,
            messages: [Clarification(Question)]);

        state.Draft = "Maybe.";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Clarification);
        state.PendingSuggestion!.Rationale.Should().NotBeNullOrWhiteSpace("the rationale stays");
        state.PendingSuggestion.Preview.Should().NotBeNull("the preview stays");

        var actions = CoachStateMachine.SuggestionActions(
            state.State,
            hasPendingSuggestion: state.PendingSuggestion is not null,
            hasClarification: state.Messages.Any(m => m.Kind == CoachMessageKind.Clarification));

        actions.ConsequentialActionCount.Should().Be(2);
        actions.ShowClarification.Should().BeTrue();
    }

    private static CoachMessageDto Clarification(string text) => new()
    {
        MessageId = "m-clarify",
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.Clarification,
        Text = text,
        CreatedAtUtc = DateTime.UtcNow
    };
}
