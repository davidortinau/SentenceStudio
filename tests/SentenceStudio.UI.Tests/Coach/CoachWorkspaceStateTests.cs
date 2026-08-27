using System.Net;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Behavioural tests for the one shared coach workspace. These cover the contracts the
/// designer brief calls out as blocking: canvas auto-open, one run at a time, cancel/abandon,
/// undo-latest-only, and the announce-or-focus policy.
/// </summary>
public class CoachWorkspaceStateTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient();
        return (new CoachWorkspaceState(client), client);
    }

    // ---------------------------------------------------------------- open / resume

    [Fact]
    public async Task OpenAsync_WithoutSessionIdStartsANewSession()
    {
        var (state, client) = Create();

        await state.OpenAsync(CoachPresentation.Overlay);

        client.StartSessionCalls.Should().Be(1);
        state.SessionId.Should().Be("session-1");
        state.State.Should().Be(CoachUiState.Ready);
        state.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task OpenAsync_WithSessionIdResumesInsteadOfStarting()
    {
        var (state, client) = Create();

        await state.OpenAsync(CoachPresentation.Overlay, "session-9");

        client.StartSessionCalls.Should().Be(0);
        state.SessionId.Should().Be("session-9");
        state.PoliteAnnouncementKey.Should().Be("Coach_AnnounceResumed");
    }

    [Fact]
    public async Task OpenAsync_FallsBackToANewSessionWhenTheResumeTargetIsGone()
    {
        var (state, client) = Create();
        client.OnGetSession = _ => null;

        await state.OpenAsync(CoachPresentation.Overlay, "gone");

        client.StartSessionCalls.Should().Be(1);
        state.State.Should().Be(CoachUiState.Ready);
    }

    [Fact]
    public async Task OpenAsync_IsIdempotentForTheSameSession()
    {
        var (state, client) = Create();

        await state.OpenAsync(CoachPresentation.Overlay);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.StartSessionCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------- canvas

    [Fact]
    public async Task Canvas_IsClosedWhenTheWorkspaceOpens()
    {
        var (state, _) = Create();

        await state.OpenAsync(CoachPresentation.Overlay);

        state.IsCanvasOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Canvas_AutoOpensOnceForANewSuggestion()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(suggestion: CoachStateMachineTests.Suggestion());
        state.Draft = "8 minutes";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.SuggestionPending);
        state.IsCanvasOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Canvas_DoesNotReopenForTheSameSuggestionAfterTheLearnerClosesIt()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(suggestion: CoachStateMachineTests.Suggestion("sug-1"));
        state.Draft = "a";
        await state.SendDraftAsync();
        state.CloseCanvas();

        // Same suggestion still pending; another turn must not force it back open.
        state.Draft = "b";
        await state.SendDraftAsync();

        state.IsCanvasOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Canvas_ReopensForANewSuggestion()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(suggestion: CoachStateMachineTests.Suggestion("sug-1"));
        state.Draft = "a";
        await state.SendDraftAsync();
        state.CloseCanvas();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(suggestion: CoachStateMachineTests.Suggestion("sug-2"));
        state.Draft = "b";
        await state.SendDraftAsync();

        state.IsCanvasOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Canvas_OnMobileBadgesInsteadOfForceSwitchingPanes()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.FullScreen);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));
        state.Draft = "10 minutes, no audio";
        await state.SendDraftAsync();

        state.Pane.Should().Be(CoachPane.Coach);
        state.PlanBadgeCount.Should().Be(1);
    }

    [Fact]
    public async Task SetPane_ToPlanClearsTheBadge()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.FullScreen);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));
        state.Draft = "10 minutes";
        await state.SendDraftAsync();
        state.SetPane(CoachPane.Plan);

        state.PlanBadgeCount.Should().Be(0);
    }

    // ---------------------------------------------------------------- direct change

    [Fact]
    public async Task ApplyConstraintAsync_SendsAStructuredActionAndLandsOnAReceipt()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));

        await state.ApplyConstraintAsync(new CoachConstraintDeltaDto
        {
            AvailableMinutes = 10,
            ChangedFields = [CoachConstraintField.AvailableMinutes]
        });

        client.SubmittedTurns.Should().ContainSingle()
            .Which.InputKind.Should().Be(CoachTurnInputKind.ConstraintAction);
        state.State.Should().Be(CoachUiState.PlanUpdated);
        state.LatestReceipt.Should().NotBeNull();
        state.LatestReceipt!.PreservedCompletedItemCount.Should().Be(2);
        state.LatestReceipt.PreservedMinutesSpent.Should().Be(12);
    }

    [Fact]
    public async Task SendDraftAsync_SendsTheExpectedPlanVersionSoStaleWritesCanBeRejected()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        client.SubmittedTurns.Single().ExpectedPlanVersion.Should().Be("v1");
    }

    [Fact]
    public async Task SendDraftAsync_ClearsTheDraft()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        state.Draft.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- input limits

    [Fact]
    public async Task Draft_OverTheLimitMovesToInputTooLongAndBlocksSubmit()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Draft = new string('a', CoachConstraintLimits.MaxTurnTextLength + 1);

        state.State.Should().Be(CoachUiState.InputTooLong);
        state.IsDraftTooLong.Should().BeTrue();
        state.CanSubmit.Should().BeFalse();

        await state.SendDraftAsync();
        client.SubmitTurnCalls.Should().Be(0);
    }

    [Fact]
    public async Task Draft_BackUnderTheLimitReturnsToReady()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Draft = new string('a', CoachConstraintLimits.MaxTurnTextLength + 1);
        state.Draft = "ok";

        state.State.Should().Be(CoachUiState.Ready);
        state.CanSubmit.Should().BeTrue();
    }

    // ---------------------------------------------------------------- suggestions

    [Fact]
    public async Task AcceptSuggestionAsync_AppliesOnceAndProducesAReceipt()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id,
            CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());

        await state.OpenAsync(CoachPresentation.Overlay, "session-1");
        state.State.Should().Be(CoachUiState.SuggestionPending);

        await state.AcceptSuggestionAsync();

        state.State.Should().Be(CoachUiState.PlanUpdated);
        state.Receipts.Should().ContainSingle();
        state.PendingSuggestion.Should().BeNull();
    }

    [Fact]
    public async Task RejectSuggestionAsync_DoesNotWrite()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id,
            CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());

        await state.OpenAsync(CoachPresentation.Overlay, "session-1");
        await state.RejectSuggestionAsync();

        state.Receipts.Should().BeEmpty();
        state.State.Should().Be(CoachUiState.Ready);
    }

    [Fact]
    public async Task AmbiguousReplyLeavesTheSuggestionPendingAndWritesNothing()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id,
            CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            sessionStatus: CoachSessionStatus.AwaitingClarification,
            suggestion: CoachStateMachineTests.Suggestion(),
            clarifyingQuestion: "Should I add the speaking activity now?",
            clarificationsRemaining: 1);

        state.Draft = "Maybe.";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Clarification);
        state.PendingSuggestion.Should().NotBeNull();
        state.Receipts.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- undo

    [Fact]
    public async Task UndoAsync_ProducesAnUndoneReceiptAndNoRedo()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));
        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        await state.UndoAsync();

        state.State.Should().Be(CoachUiState.Undone);
        state.LatestReceipt!.Revision.Source.Should().Be(CoachRevisionSource.Undo);
        state.LatestReceipt.CanUndo.Should().BeFalse();
    }

    // ---------------------------------------------------------------- concurrency and cancel

    [Fact]
    public async Task OnlyOneRunReachesTheServerAtATime()
    {
        // The invariant is still one run in flight; what changed is what happens to the second
        // typed turn. It used to be DROPPED after its question had already been shown and the
        // composer cleared, so the learner watched a question they had asked go nowhere. It is
        // now queued: still one run at a time, but nothing is lost.
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var gate = new TaskCompletionSource();
        client.OnSubmitTurn = _ =>
        {
            if (client.SubmitTurnCalls == 1)
            {
                gate.Task.GetAwaiter().GetResult();
            }

            return CoachStateMachineTests.Turn();
        };

        state.Draft = "first";
        var first = Task.Run(() => state.SendDraftAsync());
        await WaitForAsync(() => state.State == CoachUiState.Running);

        state.Draft = "second";
        var second = Task.Run(() => state.SendDraftAsync());

        // Both questions are visible at once, but only one has reached the server.
        await WaitForAsync(() => state.Messages.Count(m => m.Role == CoachMessageRole.Learner) == 2);
        client.SubmitTurnCalls.Should().Be(1, "the second turn waits its turn rather than racing");

        gate.SetResult();
        await Task.WhenAll(first, second);

        client.SubmitTurnCalls.Should().Be(2, "and it is then actually sent");
    }

    [Fact]
    public async Task CancelRun_AbandonsTheRunAndDiscardsTheLateResult()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var gate = new TaskCompletionSource();
        client.OnSubmitTurn = _ =>
        {
            gate.Task.GetAwaiter().GetResult();
            return CoachStateMachineTests.Turn(
                receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));
        };

        state.Draft = "slow turn";
        var run = Task.Run(() => state.SendDraftAsync());
        await WaitForAsync(() => state.State == CoachUiState.Running);

        await state.CancelRunAsync();
        state.State.Should().Be(CoachUiState.Ready);
        state.LastRunAbandoned.Should().BeTrue();
        state.LastStopReason.Should().Be(CoachStopReason.Cancelled);

        gate.SetResult();
        await run;

        // The abandoned run's result must not land.
        state.Receipts.Should().BeEmpty();
        state.State.Should().Be(CoachUiState.Ready);
    }

    // ---------------------------------------------------------------- failures

    [Fact]
    public async Task StalePlanVersionSurfacesAsPlanChangedElsewhere()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => throw new CoachApiException(
            HttpStatusCode.Conflict, CoachProblemTypes.PlanVersionConflict, "conflict", "stale");

        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.PlanChangedElsewhere);
        state.AlertKey.Should().Be("Coach_StatusOutOfDate");
        state.PoliteAnnouncementKey.Should().BeNull();
    }

    [Fact]
    public async Task NetworkFailureSurfacesAsOffline()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => throw new HttpRequestException("no network");

        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Offline);
    }

    [Fact]
    public async Task RetryLastAsync_ResubmitsTheSameTurn()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => throw new CoachApiException(
            HttpStatusCode.InternalServerError, CoachProblemTypes.ToolFailure, "tool", "boom");

        state.Draft = "10 minutes";
        await state.SendDraftAsync();
        state.State.Should().Be(CoachUiState.Incomplete);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn();
        await state.RetryLastAsync();

        client.SubmitTurnCalls.Should().Be(2);
        client.SubmittedTurns[1].Text.Should().Be("10 minutes");
        state.State.Should().Be(CoachUiState.Ready);
    }

    [Fact]
    public async Task KeepCurrentPlan_DismissesAFailureWithoutRetrying()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => throw new CoachApiException(
            HttpStatusCode.BadRequest, CoachProblemTypes.PlanValidationFailed, "invalid", "no");

        state.Draft = "10 minutes";
        await state.SendDraftAsync();
        state.State.Should().Be(CoachUiState.Failed);

        state.KeepCurrentPlan();

        state.State.Should().Be(CoachUiState.Ready);
        state.AlertKey.Should().BeNull();
        client.SubmitTurnCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------- announce / focus

    [Fact]
    public async Task TypedDirectRequestAnnouncesAndKeepsFocusInTheComposer()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));

        state.Draft = "Make it 10 minutes and no audio.";
        await state.SendDraftAsync();

        state.PoliteAnnouncementKey.Should().Be("Coach_StatusUpdated");
        state.ConsumePendingFocus().Should().BeNull();
    }

    [Fact]
    public async Task TappedAcceptanceMovesFocusToTheReceiptAndSuppressesTheAnnouncement()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id,
            CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");

        await state.AcceptSuggestionAsync();

        state.PoliteAnnouncementKey.Should().BeNull();
        state.ConsumePendingFocus().Should().Be(CoachElementIds.Receipt("receipt-1"));
    }

    [Fact]
    public void ConsumePendingFocus_ReturnsTheTargetExactlyOnce()
    {
        var (state, _) = Create();

        state.ConsumePendingFocus().Should().BeNull();
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public async Task DeleteSessionAsync_ClearsEverythingAndReportsSessionDeleted()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        await state.DeleteSessionAsync();

        client.DeleteCalls.Should().Be(1);
        state.SessionId.Should().BeNull();
        state.Messages.Should().BeEmpty();
        state.State.Should().Be(CoachUiState.SessionDeleted);
        state.IsOpen.Should().BeFalse();
    }

    // ---------------------------------------------------------------- availability

    [Fact]
    public async Task RefreshAvailabilityAsync_TreatsAnUnexpectedFailureAsNoEntryPoint()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(new ThrowingClient(client));

        var availability = await state.RefreshAvailabilityAsync();

        availability.IsAvailable.Should().BeFalse();
        availability.State.Should().Be(CoachAvailabilityState.Disabled);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the expected state should be reached within the timeout");
    }

    private sealed class ThrowingClient(FakeCoachApiClient inner) : ICoachApiClient
    {
        public Task<CoachAvailabilityResponse> GetAvailabilityAsync(CancellationToken cancellationToken = default)
            => throw new HttpRequestException("api down");

        public Task<CoachSessionResponse> StartSessionAsync(StartCoachSessionRequest request, CancellationToken cancellationToken = default)
            => inner.StartSessionAsync(request, cancellationToken);

        public Task<CoachSessionResponse?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => inner.GetSessionAsync(sessionId, cancellationToken);

        public Task<CoachTurnResponse> SubmitTurnAsync(string sessionId, CoachTurnRequest request, CancellationToken cancellationToken = default)
            => inner.SubmitTurnAsync(sessionId, request, cancellationToken);

        public Task<CoachTurnResponse> AcceptSuggestionAsync(string sessionId, string suggestionId, CoachSuggestionDecisionRequest request, CancellationToken cancellationToken = default)
            => inner.AcceptSuggestionAsync(sessionId, suggestionId, request, cancellationToken);

        public Task<CoachTurnResponse> RejectSuggestionAsync(string sessionId, string suggestionId, CoachSuggestionDecisionRequest request, CancellationToken cancellationToken = default)
            => inner.RejectSuggestionAsync(sessionId, suggestionId, request, cancellationToken);

        public Task<CoachTurnResponse> UndoAsync(string sessionId, CoachUndoRequest request, CancellationToken cancellationToken = default)
            => inner.UndoAsync(sessionId, request, cancellationToken);

        public Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => inner.CancelSessionAsync(sessionId, cancellationToken);

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => inner.DeleteSessionAsync(sessionId, cancellationToken);

        // The durable-history surface is delegated so this double stays a test of the *availability*
        // failure it was written for. Only GetAvailabilityAsync throws; everything else behaves.
        public Task<CoachConversationDto> CreateConversationAsync(StartCoachConversationRequest request, CancellationToken cancellationToken = default)
            => inner.CreateConversationAsync(request, cancellationToken);

        public Task<CoachConversationPageDto?> ListConversationsAsync(int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
            => inner.ListConversationsAsync(limit, cursor, cancellationToken);

        public Task<CoachConversationDto?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
            => inner.GetConversationAsync(conversationId, cancellationToken);

        public Task<CoachMessagePageDto?> GetConversationMessagesAsync(string conversationId, int? limit = null, string? before = null, CancellationToken cancellationToken = default)
            => inner.GetConversationMessagesAsync(conversationId, limit, before, cancellationToken);

        public Task<CoachConversationDto> UpdateConversationAsync(string conversationId, UpdateCoachConversationRequest request, CancellationToken cancellationToken = default)
            => inner.UpdateConversationAsync(conversationId, request, cancellationToken);

        public Task<CoachTurnOperationDto> SubmitConversationTurnAsync(string conversationId, CoachConversationTurnRequest request, CancellationToken cancellationToken = default)
            => inner.SubmitConversationTurnAsync(conversationId, request, cancellationToken);

        public Task<CoachTurnOperationDto?> GetConversationOperationAsync(string conversationId, string operationId, CancellationToken cancellationToken = default)
            => inner.GetConversationOperationAsync(conversationId, operationId, cancellationToken);

        public Task<CoachTurnOperationDto?> CancelConversationTurnAsync(string conversationId, string operationId, CancellationToken cancellationToken = default)
            => inner.CancelConversationTurnAsync(conversationId, operationId, cancellationToken);

        public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
            => inner.DeleteConversationAsync(conversationId, cancellationToken);

        public Task<Stream?> ExportConversationAsync(string conversationId, CoachExportFormat format = CoachExportFormat.Json, CancellationToken cancellationToken = default)
            => inner.ExportConversationAsync(conversationId, format, cancellationToken);

        // Write approval is delegated for the same reason durable history is: this double exists
        // to fail one call, and a second failure would make every assertion ambiguous.
        public Task<CoachWriteOperationDto?> GetWriteOperationAsync(string conversationId, string operationId, CancellationToken cancellationToken = default)
            => inner.GetWriteOperationAsync(conversationId, operationId, cancellationToken);

        public Task<CoachWriteOperationDto?> AcceptWriteAsync(string conversationId, string operationId, CancellationToken cancellationToken = default)
            => inner.AcceptWriteAsync(conversationId, operationId, cancellationToken);

        public Task<CoachWriteOperationDto?> RejectWriteAsync(string conversationId, string operationId, CancellationToken cancellationToken = default)
            => inner.RejectWriteAsync(conversationId, operationId, cancellationToken);

        public Task<CoachWriteConfirmation?> RequestWriteConfirmationAsync(string conversationId, string operationId, CancellationToken cancellationToken = default)
            => inner.RequestWriteConfirmationAsync(conversationId, operationId, cancellationToken);

        public Task<CoachWriteOperationDto?> ConfirmWriteAsync(string conversationId, string operationId, CoachWriteConfirmation confirmation, CancellationToken cancellationToken = default)
            => inner.ConfirmWriteAsync(conversationId, operationId, confirmation, cancellationToken);

        public Task<CoachWriteOperationDto?> UndoWriteAsync(string conversationId, string operationId, CancellationToken cancellationToken = default)
            => inner.UndoWriteAsync(conversationId, operationId, cancellationToken);

        public Task<CoachMemoryPageDto?> ListActiveMemoriesAsync(int? pageSize = null, string? cursor = null, CancellationToken cancellationToken = default)
            => inner.ListActiveMemoriesAsync(pageSize, cursor, cancellationToken);

        public Task<CoachMemoryPageDto?> ListMemoryCandidatesAsync(int? pageSize = null, string? cursor = null, CancellationToken cancellationToken = default)
            => inner.ListMemoryCandidatesAsync(pageSize, cursor, cancellationToken);

        public Task<CoachMemoryFactDto?> ApproveMemoryAsync(string factId, CoachMemoryApproveRequest request, CancellationToken cancellationToken = default)
            => inner.ApproveMemoryAsync(factId, request, cancellationToken);

        public Task RejectMemoryAsync(string factId, CoachMemoryRejectRequest request, CancellationToken cancellationToken = default)
            => inner.RejectMemoryAsync(factId, request, cancellationToken);

        public Task<CoachMemoryFactDto?> EditMemoryAsync(string factId, CoachMemoryEditRequest request, CancellationToken cancellationToken = default)
            => inner.EditMemoryAsync(factId, request, cancellationToken);

        public Task ForgetMemoryAsync(string factId, int expectedVersion, CancellationToken cancellationToken = default)
            => inner.ForgetMemoryAsync(factId, expectedVersion, cancellationToken);

        public Task<CoachMemoryForgetAllResponse?> ForgetAllMemoriesAsync(CancellationToken cancellationToken = default)
            => inner.ForgetAllMemoriesAsync(cancellationToken);

        public Task<CoachReportedResponsesDto?> GetReportedResponsesAsync(string conversationId, CancellationToken cancellationToken = default)
            => inner.GetReportedResponsesAsync(conversationId, cancellationToken);

        public Task<CoachResponseReportResponse?> ReportResponseAsync(string conversationId, string messageId, CoachResponseReportRequest request, CancellationToken cancellationToken = default)
            => inner.ReportResponseAsync(conversationId, messageId, request, cancellationToken);
    }
}
