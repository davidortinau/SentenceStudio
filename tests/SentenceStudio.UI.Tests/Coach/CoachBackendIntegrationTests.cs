using System.Net;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Backend-integration behaviours added after the coach endpoints landed: the cancel route,
/// the aligned RFC 7807 mapping, resume with an empty transcript, and honest rendering of the
/// fields the server deliberately leaves empty.
/// </summary>
public class CoachBackendIntegrationTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient();
        return (new CoachWorkspaceState(client), client);
    }

    // ================================================================ cancel route

    [Fact]
    public async Task CancelSessionAsync_PostsToTheLandedCancelRoute()
    {
        HttpRequestMessage? captured = null;
        var client = StubClient(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await client.CancelSessionAsync("s 1");

        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/v1/coach/sessions/s%201/cancel");
    }

    [Fact]
    public async Task CancelSessionAsync_TreatsA404AsAlreadyStopped()
    {
        // Nothing running, or the session is gone. Stop must never surface an error.
        var client = StubClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => client.CancelSessionAsync("s-1");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CancelSessionAsync_SurfacesUnexpectedFailuresAsATypedException()
    {
        var client = StubClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => client.CancelSessionAsync("s-1");

        (await act.Should().ThrowAsync<CoachApiException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task StopCallsTheServerBeforeAbandoningTheRunLocally()
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

        client.CancelCalls.Should().Be(1, "the server must release the concurrency slot");
        state.State.Should().Be(CoachUiState.Ready);
        state.LastRunAbandoned.Should().BeTrue();
        state.LastStopReason.Should().Be(CoachStopReason.Cancelled);

        gate.SetResult();
        await run;

        // The abandoned run's late result is still discarded.
        state.Receipts.Should().BeEmpty();
        state.State.Should().Be(CoachUiState.Ready);
    }

    [Fact]
    public async Task StopStillReleasesTheUiWhenTheCancelEndpointFails()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnCancel = () => throw new HttpRequestException("cancel unreachable");

        var gate = new TaskCompletionSource();
        client.OnSubmitTurn = _ =>
        {
            gate.Task.GetAwaiter().GetResult();
            return CoachStateMachineTests.Turn();
        };

        state.Draft = "slow turn";
        var run = Task.Run(() => state.SendDraftAsync());
        await WaitForAsync(() => state.State == CoachUiState.Running);

        await state.CancelRunAsync();

        state.State.Should().Be(CoachUiState.Ready);
        state.LastRunAbandoned.Should().BeTrue();

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task CancelRunAsync_DoesNothingWhenNoRunIsInFlight()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        await state.CancelRunAsync();

        client.CancelCalls.Should().Be(0);
        state.LastRunAbandoned.Should().BeFalse();
    }

    // ================================================================ error mapping

    /// <summary>
    /// Every problem type the landed <c>CoachEndpoints.ToProblem</c> table can emit, in the
    /// HTTP band the server uses for it.
    /// </summary>
    [Theory]
    // 404 band
    [InlineData(CoachProblemTypes.Unavailable, CoachUiState.SessionDeleted)]
    [InlineData(CoachProblemTypes.SessionNotFound, CoachUiState.Expired)]
    [InlineData(CoachProblemTypes.SessionExpired, CoachUiState.Expired)]
    [InlineData(CoachProblemTypes.SuggestionNotFound, CoachUiState.Ready)]
    // 409 band
    [InlineData(CoachProblemTypes.PlanVersionConflict, CoachUiState.PlanChangedElsewhere)]
    [InlineData(CoachProblemTypes.RunInProgress, CoachUiState.Incomplete)]
    // 422 band
    [InlineData(CoachProblemTypes.InvalidTurnInput, CoachUiState.InputTooLong)]
    [InlineData(CoachProblemTypes.InvalidConstraint, CoachUiState.Failed)]
    [InlineData(CoachProblemTypes.PlanValidationFailed, CoachUiState.Failed)]
    [InlineData(CoachProblemTypes.NothingToUndo, CoachUiState.Ready)]
    // 429 / 503 bands
    [InlineData(CoachProblemTypes.RateLimited, CoachUiState.Limited)]
    [InlineData(CoachProblemTypes.ToolFailure, CoachUiState.Incomplete)]
    // Durable-history band. A cancellation the learner asked for, and a refused replay, are not
    // failures; a vanished conversation is gone rather than expired.
    [InlineData(CoachProblemTypes.ConversationNotFound, CoachUiState.SessionDeleted)]
    [InlineData(CoachProblemTypes.ConversationStateConflict, CoachUiState.PlanChangedElsewhere)]
    [InlineData(CoachProblemTypes.IdempotencyConflict, CoachUiState.Ready)]
    [InlineData(CoachProblemTypes.InvalidCursor, CoachUiState.Ready)]
    [InlineData(CoachProblemTypes.RunCancelled, CoachUiState.Ready)]
    public void EveryServerProblemTypeMapsToADeliberateState(string problemType, CoachUiState expected)
    {
        var exception = new CoachApiException(HttpStatusCode.BadRequest, problemType, "t", "d");

        CoachStateMachine.FromProblem(exception).Should().Be(expected);
    }

    [Fact]
    public void EveryProblemTypeConstantIsMappedExplicitly()
    {
        // Guards against a new server problem type silently degrading to a generic failure.
        var constants = typeof(CoachProblemTypes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        constants.Should().NotBeEmpty();

        var unmapped = constants
            .Where(type => CoachStateMachine.FromProblem(
                new CoachApiException(HttpStatusCode.BadRequest, type, null, null)) == CoachUiState.Failed)
            .ToList();

        // Only the two "nothing changed, and it really is a failure" types may land on Failed.
        unmapped.Should().BeEquivalentTo([
            CoachProblemTypes.InvalidConstraint,
            CoachProblemTypes.PlanValidationFailed
        ]);
    }

    [Fact]
    public async Task AnUnavailableCoachMidSessionClosesTheWorkspaceRatherThanOfferingARetry()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => throw new CoachApiException(
            HttpStatusCode.NotFound, CoachProblemTypes.Unavailable, "Coach unavailable", null);

        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.SessionDeleted);
        CoachStateMachine.IsTerminal(state.State).Should().BeTrue();
    }

    [Fact]
    public async Task AStaleUndoIsNotReportedAsAPlanFailure()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnUndo = () => throw new CoachApiException(
            HttpStatusCode.UnprocessableEntity, CoachProblemTypes.NothingToUndo, "Nothing to undo", null);

        await state.UndoAsync("rev-gone");

        state.State.Should().Be(CoachUiState.Ready);
        state.Receipts.Should().BeEmpty();
    }

    // ================================================================ resume with no transcript

    [Fact]
    public async Task ResumeWithEmptyServerMessagesFlagsTheMissingHistoryInsteadOfFabricatingIt()
    {
        // The server keeps no plaintext transcript, so a session read always answers Messages=[].
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);

        await state.OpenAsync(CoachPresentation.Overlay, "session-7");

        state.Messages.Should().BeEmpty("the client must not invent turns it does not have");
        state.IsResumedWithoutHistory.Should().BeTrue();
        state.State.Should().Be(CoachUiState.Ready);
    }

    [Fact]
    public async Task ClosingAndReopeningTheOverlayKeepsTheConversationTheLearnerCanAlreadySee()
    {
        // The real same-circuit path: closing the overlay drops the query parameter but keeps the
        // session, and reopening re-reads it from the server. That read answers Messages=[], and
        // it must NOT blank a conversation the learner is still in the middle of.
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);

        await state.OpenAsync(CoachPresentation.Overlay);
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages: [Message("m-1", "Ten minutes it is.")]);
        state.Draft = "10 minutes";
        await state.SendDraftAsync();
        state.Messages.Should().HaveCount(2, "the learner's question and the reply are both shown");

        state.Close();
        state.IsOpen.Should().BeFalse();

        await state.OpenAsync(CoachPresentation.Overlay, state.SessionId);

        state.Messages.Should().HaveCount(2, "a same-session read with no transcript must not blank the view");
        state.Messages.Should().Contain(m => m.Text == "10 minutes",
            "the learner's own words survive a close and reopen in the same circuit");
        state.IsResumedWithoutHistory.Should().BeFalse("there is visible history, so nothing needs explaining");
    }

    [Fact]
    public async Task ReopeningAlsoKeepsEvidenceTheServerNoLongerEchoes()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);
        await state.OpenAsync(CoachPresentation.Overlay);

        var evidence = new CoachEvidenceDto
        {
            Kind = CoachEvidenceKind.PracticeBalance,
            Label = "Practice balance",
            Summary = "Mostly input.",
            WindowStartDate = new DateOnly(2026, 8, 1),
            WindowEndDate = new DateOnly(2026, 8, 14),
            Values = [new CoachEvidenceValueDto { Label = "Input", Value = 40, Unit = CoachEvidenceUnit.Minutes }]
        };
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(evidence: [evidence]);
        state.Draft = "how am i doing";
        await state.SendDraftAsync();
        state.Evidence.Should().ContainSingle();

        state.Close();
        await state.OpenAsync(CoachPresentation.Overlay, state.SessionId);

        state.Evidence.Should().ContainSingle("a session read answers Evidence=[] and must not erase it");
    }

    [Fact]
    public async Task SwitchingToADifferentSessionDropsTheOldConversation()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);
        await state.OpenAsync(CoachPresentation.Overlay, "session-a");

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages: [Message("m-1", "hello")]);
        state.Draft = "hi";
        await state.SendDraftAsync();
        state.Messages.Should().HaveCount(2, "the learner's own turn is shown alongside the reply");

        await state.OpenAsync(CoachPresentation.Overlay, "session-b");

        state.Messages.Should().BeEmpty("another session's history must never leak in");
        state.IsResumedWithoutHistory.Should().BeTrue();
    }

    [Fact]
    public async Task ANewSessionIsNotLabelledAsAResumeWithMissingHistory()
    {
        var (state, _) = Create();

        await state.OpenAsync(CoachPresentation.Overlay);

        state.Messages.Should().BeEmpty();
        state.IsResumedWithoutHistory.Should().BeFalse("an empty new conversation needs no explanation");
    }

    [Fact]
    public async Task TheResumedSummaryClearsOnceTheLearnerAddsATurn()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);
        await state.OpenAsync(CoachPresentation.Overlay, "session-7");
        state.IsResumedWithoutHistory.Should().BeTrue();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(messages: [Message("m-1", "Ten minutes it is.")]);
        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        state.IsResumedWithoutHistory.Should().BeFalse();
    }

    [Fact]
    public async Task ResumeStillCarriesServerOwnedRevisionsSoPlanContinuitySurvivesAReload()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id, revisions:
        [
            CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest).Revision
        ]);

        await state.OpenAsync(CoachPresentation.Overlay, "session-7");

        state.Messages.Should().BeEmpty();
        state.Revisions.Should().ContainSingle("revisions are server-owned and do survive a reload");
        state.PlanState.Should().NotBeNull();
    }

    // ================================================================ honest empty data

    [Fact]
    public async Task EmptyServerEvidenceIsNotBackfilledFromAnEarlierTurn()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var evidence = new CoachEvidenceDto
        {
            Kind = CoachEvidenceKind.PracticeBalance,
            Label = "Practice balance",
            Summary = "Mostly input.",
            WindowStartDate = new DateOnly(2026, 8, 1),
            WindowEndDate = new DateOnly(2026, 8, 14),
            Values = []
        };

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(evidence: [evidence]);
        state.Draft = "how am i doing";
        await state.SendDraftAsync();

        // The row itself is kept — label, summary and window are real. Its values are genuinely
        // empty and must stay that way.
        state.Evidence.Should().ContainSingle();
        state.Evidence[0].Values.Should().BeEmpty();
    }

    [Fact]
    public async Task ResourceTitleIsNullUntilTheLearnersOwnPlanSuppliesOne()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        // The server always sends ResourceTitle = null on coach plan items.
        state.ResourceTitleFor("plan-item-1").Should().BeNull();
        state.PlanResourceTitlesLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task ResourceTitleJoinsFromTheCurrentPlanWhenThatDataExists()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.SetPlanResourceTitles(new Dictionary<string, string>
        {
            ["plan-item-1"] = "Korean Short Stories"
        });

        state.ResourceTitleFor("plan-item-1").Should().Be("Korean Short Stories");
        state.PlanResourceTitlesLoaded.Should().BeTrue();
    }

    [Fact]
    public async Task ResourceTitleStaysNullForItemsTheCurrentPlanDoesNotKnow()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.SetPlanResourceTitles(new Dictionary<string, string> { ["plan-item-1"] = "Korean Short Stories" });

        state.ResourceTitleFor("plan-item-2").Should().BeNull("a missing join must render nothing, not a placeholder");
        state.ResourceTitleFor(null).Should().BeNull();
        state.ResourceTitleFor("  ").Should().BeNull();
    }

    [Fact]
    public async Task NoPlanDataRecordsTheAttemptAndYieldsNoTitles()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        // Learner has no cached plan, or the lookup failed.
        state.SetPlanResourceTitles(null);

        state.PlanResourceTitlesLoaded.Should().BeTrue("the canvas must not retry on every toggle");
        state.ResourceTitleFor("plan-item-1").Should().BeNull();
    }

    [Fact]
    public async Task BlankResourceTitlesFromThePlanAreDiscarded()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.SetPlanResourceTitles(new Dictionary<string, string>
        {
            ["plan-item-1"] = "   ",
            ["plan-item-2"] = "Real Title"
        });

        state.ResourceTitleFor("plan-item-1").Should().BeNull();
        state.ResourceTitleFor("plan-item-2").Should().Be("Real Title");
    }

    [Fact]
    public void ResetClearsTheResourceTitleLookupSoANewSessionRefetches()
    {
        var (state, _) = Create();
        state.SetPlanResourceTitles(new Dictionary<string, string> { ["plan-item-1"] = "Title" });

        state.Reset();

        state.PlanResourceTitlesLoaded.Should().BeFalse();
        state.ResourceTitleFor("plan-item-1").Should().BeNull();
        state.IsResumedWithoutHistory.Should().BeFalse();
    }

    // ================================================================ helpers

    private static CoachMessageDto Message(string id, string text) => new()
    {
        MessageId = id,
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.Text,
        Text = text,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static CoachApiClient StubClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("https://api.test") });

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the expected state should be reached within the timeout");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
