using System.Net;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Pure transition rules for the shared coach UI. These are deliberately behavioural, not pixel
/// assertions: they pin the contracts the designer brief calls out as blocking.
/// </summary>
public class CoachStateMachineTests
{
    // ---------------------------------------------------------------- presentation

    [Theory]
    [InlineData(320, CoachPresentation.FullScreen)]
    [InlineData(767, CoachPresentation.FullScreen)]
    [InlineData(768, CoachPresentation.Overlay)]
    [InlineData(1024, CoachPresentation.Overlay)]
    [InlineData(1920, CoachPresentation.Overlay)]
    public void ChoosePresentation_UsesTheRepositoryBreakpoint(int width, CoachPresentation expected)
    {
        CoachStateMachine.ChoosePresentation(width).Should().Be(expected);
    }

    [Theory]
    [InlineData(768, false)]
    [InlineData(991, false)]
    [InlineData(992, true)]
    [InlineData(1440, true)]
    public void UsesSplitCanvas_SwitchesToTabsBelow992(int width, bool expected)
    {
        CoachStateMachine.UsesSplitCanvas(width).Should().Be(expected);
    }

    // ---------------------------------------------------------------- turn mapping

    [Fact]
    public void FromTurn_AppliedChangeBecomesPlanUpdated()
    {
        var turn = Turn(receipt: Receipt(CoachRevisionSource.DirectRequest));

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.PlanUpdated);
    }

    [Fact]
    public void FromTurn_UndoReceiptBecomesUndone()
    {
        var turn = Turn(receipt: Receipt(CoachRevisionSource.Undo));

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Undone);
    }

    [Fact]
    public void FromTurn_PendingSuggestionBecomesSuggestionPending()
    {
        var turn = Turn(suggestion: Suggestion());

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.SuggestionPending);
    }

    [Fact]
    public void FromTurn_ClarificationWithBudgetLeftAsksAgain()
    {
        var turn = Turn(
            sessionStatus: CoachSessionStatus.AwaitingClarification,
            clarifyingQuestion: "Should I add the speaking activity to Today's Plan now?",
            clarificationsRemaining: 1);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Clarification);
    }

    [Fact]
    public void FromTurn_ClarificationWithNoBudgetLeftStopsLooping()
    {
        var turn = Turn(
            sessionStatus: CoachSessionStatus.AwaitingClarification,
            clarifyingQuestion: "Should I add it?",
            clarificationsRemaining: 0);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.ClarificationLimitReached);
    }

    [Fact]
    public void FromTurn_RateLimitOutranksEverythingButExpiry()
    {
        var turn = Turn(
            stopReason: CoachStopReason.RateLimit,
            suggestion: Suggestion());

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Limited);
    }

    [Fact]
    public void FromTurn_ExpiredSessionOutranksRateLimit()
    {
        var turn = Turn(
            stopReason: CoachStopReason.SessionExpired,
            sessionStatus: CoachSessionStatus.Expired);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Expired);
    }

    [Fact]
    public void FromTurn_IncompleteRunDoesNotClaimAFailure()
    {
        var turn = Turn(status: CoachTurnStatus.Incomplete, stopReason: CoachStopReason.IterationLimit);

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.Incomplete);
    }

    [Fact]
    public void FromTurn_NoChangeReturnsToReady()
    {
        CoachStateMachine.FromTurn(Turn()).Should().Be(CoachUiState.Ready);
    }

    // ---------------------------------------------------------------- problem mapping

    [Fact]
    public void FromProblem_StalePlanVersionGetsItsOwnState()
    {
        // A generic "Failed" here would be a lie: something really did change elsewhere.
        var exception = Problem(CoachProblemTypes.PlanVersionConflict, HttpStatusCode.Conflict);

        CoachStateMachine.FromProblem(exception).Should().Be(CoachUiState.PlanChangedElsewhere);
    }

    [Theory]
    [InlineData(CoachProblemTypes.SessionExpired, CoachUiState.Expired)]
    [InlineData(CoachProblemTypes.SessionNotFound, CoachUiState.Expired)]
    [InlineData(CoachProblemTypes.RateLimited, CoachUiState.Limited)]
    [InlineData(CoachProblemTypes.Timeout, CoachUiState.Incomplete)]
    [InlineData(CoachProblemTypes.ToolFailure, CoachUiState.Incomplete)]
    [InlineData(CoachProblemTypes.RunInProgress, CoachUiState.Incomplete)]
    [InlineData(CoachProblemTypes.InvalidTurnInput, CoachUiState.InputTooLong)]
    [InlineData(CoachProblemTypes.PlanValidationFailed, CoachUiState.Failed)]
    public void FromProblem_MapsKnownProblemTypes(string problemType, CoachUiState expected)
    {
        CoachStateMachine.FromProblem(Problem(problemType, HttpStatusCode.BadRequest))
            .Should().Be(expected);
    }

    [Fact]
    public void FromProblem_UnknownProblemFallsBackToFailed()
    {
        CoachStateMachine.FromProblem(Problem("https://example.test/unknown", HttpStatusCode.InternalServerError))
            .Should().Be(CoachUiState.Failed);
    }

    // ---------------------------------------------------------------- canvas contract

    [Fact]
    public void ShouldAutoOpenCanvas_OpensOnceForANewChange()
    {
        CoachStateMachine.ShouldAutoOpenCanvas(lastAutoOpenKey: null, newKey: "rev-1").Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoOpenCanvas_DoesNotReopenForTheSameChange()
    {
        CoachStateMachine.ShouldAutoOpenCanvas(lastAutoOpenKey: "rev-1", newKey: "rev-1").Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoOpenCanvas_OpensAgainForTheNextChange()
    {
        CoachStateMachine.ShouldAutoOpenCanvas(lastAutoOpenKey: "rev-1", newKey: "rev-2").Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoOpenCanvas_DoesNothingWhenThereIsNoChange()
    {
        CoachStateMachine.ShouldAutoOpenCanvas(lastAutoOpenKey: "rev-1", newKey: null).Should().BeFalse();
    }

    // ---------------------------------------------------------------- announce or focus

    [Fact]
    public void OutcomePolicy_TypedRequestKeepsFocusInTheComposer()
    {
        var policy = CoachStateMachine.OutcomePolicy(CoachInitiator.Composer, succeeded: true);

        policy.MoveFocusToReceipt.Should().BeFalse();
        policy.AnnouncePolitely.Should().BeTrue();
    }

    [Fact]
    public void OutcomePolicy_TappedAcceptMovesFocusAndSuppressesTheAnnouncement()
    {
        var policy = CoachStateMachine.OutcomePolicy(CoachInitiator.SuggestionButton, succeeded: true);

        policy.MoveFocusToReceipt.Should().BeTrue();
        policy.AnnouncePolitely.Should().BeFalse();
    }

    [Fact]
    public void OutcomePolicy_TappedUndoMovesFocusAndSuppressesTheAnnouncement()
    {
        var policy = CoachStateMachine.OutcomePolicy(CoachInitiator.UndoButton, succeeded: true);

        policy.MoveFocusToReceipt.Should().BeTrue();
        policy.AnnouncePolitely.Should().BeFalse();
    }

    [Fact]
    public void OutcomePolicy_ChipKeepsFocusOnTheChip()
    {
        var policy = CoachStateMachine.OutcomePolicy(CoachInitiator.Chip, succeeded: true);

        policy.MoveFocusToReceipt.Should().BeFalse();
        policy.AnnouncePolitely.Should().BeTrue();
    }

    [Fact]
    public void OutcomePolicy_FailureNeverUsesThePoliteRegion()
    {
        foreach (var initiator in Enum.GetValues<CoachInitiator>())
        {
            CoachStateMachine.OutcomePolicy(initiator, succeeded: false)
                .AnnouncePolitely.Should().BeFalse();
        }
    }

    [Fact]
    public void OutcomePolicy_TypedFailureDoesNotYankFocus()
    {
        CoachStateMachine.OutcomePolicy(CoachInitiator.Composer, succeeded: false)
            .MoveFocusToReceipt.Should().BeFalse();
    }

    // ---------------------------------------------------------------- busy / submit gates

    [Theory]
    [InlineData(CoachUiState.Opening)]
    [InlineData(CoachUiState.Resuming)]
    [InlineData(CoachUiState.LoadingEvidence)]
    [InlineData(CoachUiState.Running)]
    [InlineData(CoachUiState.Applying)]
    [InlineData(CoachUiState.Undoing)]
    public void IsBusy_DisablesEveryAffordanceWhileARunIsInFlight(CoachUiState state)
    {
        CoachStateMachine.IsBusy(state).Should().BeTrue();
        CoachStateMachine.CanSubmit(state).Should().BeFalse();
    }

    [Theory]
    [InlineData(CoachUiState.Ready)]
    [InlineData(CoachUiState.SuggestionPending)]
    [InlineData(CoachUiState.PlanUpdated)]
    [InlineData(CoachUiState.Failed)]
    public void CanSubmit_AllowsInputInRestingStates(CoachUiState state)
    {
        CoachStateMachine.CanSubmit(state).Should().BeTrue();
    }

    [Theory]
    [InlineData(CoachUiState.Expired)]
    [InlineData(CoachUiState.SessionDeleted)]
    public void TerminalStatesCannotSubmit(CoachUiState state)
    {
        CoachStateMachine.IsTerminal(state).Should().BeTrue();
        CoachStateMachine.CanSubmit(state).Should().BeFalse();
    }

    [Fact]
    public void AnnouncementKey_IsDefinedForEveryUserVisibleState()
    {
        var withoutAnnouncement = Enum.GetValues<CoachUiState>()
            .Where(s => CoachStateMachine.AnnouncementKey(s) is null)
            .ToArray();

        // Only the two silent entry states have no announcement.
        withoutAnnouncement.Should().BeEquivalentTo([CoachUiState.Opening, CoachUiState.Ready]);
    }

    // ---------------------------------------------------------------- builders

    private static CoachApiException Problem(string problemType, HttpStatusCode status) =>
        new(status, problemType, "test", "test");

    internal static CoachTurnResponse Turn(
        CoachTurnStatus status = CoachTurnStatus.Completed,
        CoachStopReason stopReason = CoachStopReason.Completed,
        CoachSessionStatus sessionStatus = CoachSessionStatus.Active,
        CoachChangeReceiptDto? receipt = null,
        PendingCoachSuggestionDto? suggestion = null,
        string? clarifyingQuestion = null,
        int clarificationsRemaining = 2,
        IReadOnlyList<CoachMessageDto>? messages = null,
        IReadOnlyList<CoachEvidenceDto>? evidence = null,
        CoachAnswerDto? answer = null,
        CoachLimitationDto? limitation = null) => new()
        {
            SessionId = "session-1",
            TurnId = "turn-1",
            Status = status,
            StopReason = stopReason,
            SessionStatus = sessionStatus,
            Messages = messages ?? Array.Empty<CoachMessageDto>(),
            Evidence = evidence ?? Array.Empty<CoachEvidenceDto>(),
            Answer = answer,
            ActiveConstraints = Constraints(),
            PlanState = PlanState(),
            ChangeReceipt = receipt,
            PendingSuggestion = suggestion,
            ClarifyingQuestion = clarifyingQuestion,
            ClarificationsRemaining = clarificationsRemaining,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(24),
            Limitation = limitation
        };

    internal static CoachConstraintSetDto Constraints() => new()
    {
        AvailableMinutes = 10,
        AudioAllowed = true,
        SpeechAllowed = true,
        TypingAllowed = true,
        EnergyLevel = CoachEnergyLevel.Normal
    };

    internal static CoachPlanStateDto PlanState(string version = "v1") => new()
    {
        PlanDate = DateOnly.FromDateTime(DateTime.UtcNow),
        PlanVersion = version,
        AppliedConstraints = Constraints(),
        EstimatedTotalMinutes = 10,
        CompletedCount = 0,
        TotalCount = 3,
        CompletionPercentage = 0
    };

    internal static CoachChangeReceiptDto Receipt(
        CoachRevisionSource source,
        string receiptId = "receipt-1",
        string revisionId = "rev-1",
        bool canUndo = true) => new()
        {
            ReceiptId = receiptId,
            Revision = new CoachRevisionDto
            {
                RevisionId = revisionId,
                RevisionNumber = 1,
                Source = source,
                Summary = "Updated remaining items",
                BeforePlanVersion = "v1",
                AfterPlanVersion = "v2",
                CreatedAtUtc = DateTime.UtcNow,
                CanUndo = canUndo
            },
            Summary = "Updated remaining items",
            AppliedDelta = new CoachConstraintDeltaDto(),
            Diff = Diff(),
            ReplacedItemCount = 3,
            PreservedCompletedItemCount = 2,
            PreservedInProgressItemCount = 0,
            PreservedMinutesSpent = 12,
            CanUndo = canUndo,
            UndoLabel = "Undo"
        };

    internal static CoachPlanDiffDto Diff() => new()
    {
        BeforePlanVersion = "v1",
        AfterPlanVersion = "v2",
        IsPreview = false,
        EstimatedMinutesBefore = 20,
        EstimatedMinutesAfter = 10
    };

    internal static PendingCoachSuggestionDto Suggestion(string suggestionId = "sug-1") => new()
    {
        SuggestionId = suggestionId,
        Delta = new CoachConstraintDeltaDto(),
        Rationale = "Your last 14 days were mostly input.",
        Preview = Diff(),
        AcceptLabel = "Include speaking",
        RejectLabel = "Not now",
        CreatedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    // ================================================================ R5 regression: answer-shape refusal

    /// <summary>
    /// R5-4: A Rejected + ValidationFailed turn carrying an AnswerShapeInvalid limitation,
    /// with no receipt and no clarification, maps to Ready. The composer stays usable, the
    /// limitation DTO is retained (not discarded), and no autosend fires.
    /// </summary>
    [Fact]
    public void FromTurn_AnswerShapeRefusal_StaysReady_LimitationRetained()
    {
        var turn = Turn(
            status: CoachTurnStatus.Rejected,
            stopReason: CoachStopReason.ValidationFailed,
            limitation: new CoachLimitationDto
            {
                Code = CoachLimitationCode.AnswerShapeInvalid,
                Coverage = CoachEvidenceCoverage.Unknown,
                AsOfUtc = DateTime.UtcNow
            });

        var state = CoachStateMachine.FromTurn(turn);

        state.Should().Be(CoachUiState.Ready,
            "a shape refusal is a deliberate no-op; the learner may immediately re-phrase");

        // Confirm the limitation is still accessible for the UI card.
        turn.Limitation.Should().NotBeNull();
        turn.Limitation!.Code.Should().Be(CoachLimitationCode.AnswerShapeInvalid);
    }

    /// <summary>
    /// Same refusal but with a pending suggestion — maps to SuggestionPending, not Ready.
    /// The shape failure must not silently withdraw an existing offer.
    /// </summary>
    [Fact]
    public void FromTurn_AnswerShapeRefusal_WithPendingSuggestion_KeepsSuggestionPending()
    {
        var turn = Turn(
            status: CoachTurnStatus.Rejected,
            stopReason: CoachStopReason.ValidationFailed,
            suggestion: Suggestion(),
            limitation: new CoachLimitationDto
            {
                Code = CoachLimitationCode.AnswerShapeInvalid,
                Coverage = CoachEvidenceCoverage.Unknown
            });

        CoachStateMachine.FromTurn(turn).Should().Be(CoachUiState.SuggestionPending);
    }

    /// <summary>
    /// CanSubmit confirms the composer remains usable from the Ready state after shape refusal.
    /// </summary>
    [Fact]
    public void CanSubmit_AfterShapeRefusal_ComposerStaysEnabled()
    {
        CoachStateMachine.CanSubmit(CoachUiState.Ready).Should().BeTrue(
            "the learner can immediately re-phrase after an answer-shape refusal");
    }
}
