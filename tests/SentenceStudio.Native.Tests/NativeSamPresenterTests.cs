using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Data;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native.Tests;

public sealed class NativeSamPresenterTests
{
    static NativeSamPresenterTests()
    {
        NativeSamPresenter.PollInterval = TimeSpan.FromMilliseconds(1);
        NativeSamPresenter.StillWorkingAfter = TimeSpan.FromSeconds(30);
        NativeSamPresenter.StopAfter = TimeSpan.FromSeconds(31);
    }

    [Fact]
    public async Task EmptyOwnerMakesNoCallAndHidesSam()
    {
        using var fixture = new NativeSliceFixture();
        var api = new FakeSam();
        var sam = Create(fixture, api);

        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        Assert.False(sam.State.IsVisible);
        Assert.False(sam.State.IsOpen);
        Assert.Empty(api.Owners);
    }

    [Fact]
    public async Task AvailabilityTellsTheServersAnswerFromAnOfflineOrFailedRead()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        var sam = Create(fixture, api);
        Assert.Equal(NativeSamAvailability.Unknown, sam.State.Availability);

        await sam.RefreshAvailabilityAsync();
        Assert.Equal(NativeSamAvailability.Offered, sam.State.Availability);
        Assert.True(sam.State.IsVisible);

        api.Availability = new CoachAvailabilityResponse
        {
            IsAvailable = false, State = CoachAvailabilityState.Disabled
        };
        await sam.RefreshAvailabilityAsync();
        Assert.Equal(NativeSamAvailability.NotOffered, sam.State.Availability);
        Assert.False(sam.State.IsVisible);

        api.Overrides[nameof(ICoachApiClient.GetAvailabilityAsync)] = _ =>
            Task.FromException<CoachAvailabilityResponse>(new HttpRequestException("down"));
        await sam.RefreshAvailabilityAsync();
        Assert.Equal(NativeSamAvailability.Unreachable, sam.State.Availability);
        Assert.False(sam.State.IsVisible);

        sam.ClearForAccountBoundary();
        Assert.Equal(NativeSamAvailability.Unknown, sam.State.Availability);
        fixture.SetConnectivity(false);
        await sam.RefreshAvailabilityAsync();
        Assert.Equal(NativeSamAvailability.Offline, sam.State.Availability);
        Assert.False(sam.State.IsVisible);
    }

    [Fact]
    public async Task OpenResumesMostRecentOpenConversationForTheActiveOwnerOnly()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("closed", closed: true, minutesAgo: 1));
        api.Conversations.Add(Conversation("recent", minutesAgo: 5));
        api.Conversations.Add(Conversation("older", minutesAgo: 50));
        api.Messages["recent"] = [History("m1", CoachMessageRole.Learner, "hello", 1),
            History("m2", CoachMessageRole.Coach, "hi", 2)];
        var sam = Create(fixture, api);

        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        Assert.True(sam.State.IsOpen);
        Assert.Equal("recent", sam.State.ConversationId);
        Assert.Equal(NativeSamStatus.Ready, sam.State.Status);
        Assert.Equal(["hello", "hi"], sam.State.SafeEntries.Select(entry => entry.Text));
        Assert.True(sam.State.HasLearnerSpoken);
        Assert.All(api.Owners, owner => Assert.Equal(NativeSliceFixture.UserA, owner));
        Assert.Equal(0, api.Created);
    }

    [Fact]
    public async Task OwnerSwitchDropsEverythingFromThePreviousOwner()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("a-thread"));
        api.Messages["a-thread"] = [History("m1", CoachMessageRole.Learner, "owner A secret", 1)];
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        Assert.Contains(sam.State.SafeEntries, entry => entry.Text == "owner A secret");

        fixture.Activate(NativeSliceFixture.UserB);
        api.Conversations.Clear();
        api.Owners.Clear();
        await sam.RefreshAvailabilityAsync();

        Assert.Null(sam.State.ConversationId);
        Assert.Empty(sam.State.SafeEntries);
        Assert.Empty(sam.State.SafeConversations);
        Assert.False(sam.State.IsOpen);
        Assert.All(api.Owners, owner => Assert.Equal(NativeSliceFixture.UserB, owner));

        fixture.LoseOwner();
        api.Owners.Clear();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        Assert.Empty(api.Owners);
        Assert.False(sam.State.IsVisible);
    }

    [Fact]
    public async Task OfflineSendQueuesNothingAndKeepsTheDraft()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        fixture.SetConnectivity(false);
        sam.SetDraft("What does 좋아하다 mean?");
        await sam.SendAsync();

        Assert.Equal(NativeSamStatus.Offline, sam.State.Status);
        Assert.Equal("What does 좋아하다 mean?", sam.State.Draft);
        Assert.Equal(0, api.Turns);
        Assert.DoesNotContain(sam.State.SafeEntries, entry => entry.Kind == NativeSamEntryKind.Learner);
    }

    [Fact]
    public async Task OverLimitDraftIsBlockedBeforeTheNetwork()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        sam.SetDraft(new string('a', CoachConstraintLimits.MaxTurnTextLength + 1));
        Assert.False(sam.State.CanSend);
        await sam.SendAsync();

        Assert.Equal(NativeSamStatus.InputTooLong, sam.State.Status);
        Assert.Equal(0, api.Turns);
    }

    [Fact]
    public async Task RefusalIsASafeNoOpWithTheTypedLimitationAndEvidenceOnTheAnswer()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        api.TurnResult = (request, conversation) => Turn(
            status: CoachTurnStatus.Rejected, stop: CoachStopReason.ValidationFailed,
            limitation: new CoachLimitationDto
            {
                Code = CoachLimitationCode.RefusedByDesign,
                Alternatives = [CoachAlternativeCode.RemoveOneListAtATime, CoachAlternativeCode.ExportBeforeRemoving]
            },
            evidence: [new CoachEvidenceDto
            {
                Kind = CoachEvidenceKind.PracticeBalance, Label = "Practice", Summary = "12 minutes",
                WindowStartDate = new DateOnly(2026, 10, 25), WindowEndDate = new DateOnly(2026, 10, 31)
            }]);
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        sam.SetDraft("delete all my vocabulary");
        await sam.SendAsync();

        Assert.Equal(NativeSamStatus.Ready, sam.State.Status);
        Assert.False(sam.State.CanRetry);
        Assert.Equal(string.Empty, sam.State.Draft);
        var answer = Assert.Single(sam.State.SafeEntries, entry => entry.Kind == NativeSamEntryKind.Sam);
        Assert.Equal(CoachLimitationCode.RefusedByDesign, answer.Limitation?.Code);
        Assert.True(answer.HasEvidence);
        Assert.Single(sam.State.SafeEntries, entry => entry.Kind == NativeSamEntryKind.Learner);
        sam.ToggleEvidence(answer.Id);
        Assert.True(sam.State.IsEvidenceExpanded(answer.Id));
    }

    [Fact]
    public async Task PlanReceiptRaisesPlanChangedAndUndoTargetsThatRevision()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        api.TurnResult = (_, _) => Turn(receipt: Receipt("rev-1", CoachRevisionSource.DirectRequest));
        api.UndoResult = request => Turn(receipt: Receipt("rev-2", CoachRevisionSource.Undo, canUndo: false));
        var sam = Create(fixture, api);
        var planChanges = 0;
        sam.PlanChanged += () => planChanges++;
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        sam.SetDraft("Make Today's Plan 10 minutes");
        await sam.SendAsync();
        Assert.Equal("rev-1", sam.State.LatestReceipt?.Revision.RevisionId);
        Assert.True(sam.State.CanUndo);
        Assert.Equal(1, planChanges);

        await sam.UndoAsync();
        Assert.Equal("rev-1", api.LastUndo?.RevisionId);
        Assert.Equal(CoachRevisionSource.Undo, sam.State.LatestReceipt?.Revision.Source);
        Assert.False(sam.State.CanUndo);
        Assert.Equal(2, planChanges);
    }

    [Fact]
    public async Task SuggestionAcceptAndRejectUseTheSessionDecisionRoutes()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        api.TurnResult = (_, _) => Turn(suggestion: Suggestion("s-1"));
        api.AcceptResult = id => Turn(receipt: Receipt("rev-9", CoachRevisionSource.AcceptedSuggestion));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        sam.SetDraft("What should I focus on today?");
        await sam.SendAsync();
        Assert.Equal("s-1", sam.State.Suggestion?.SuggestionId);

        await sam.AcceptSuggestionAsync();
        Assert.Equal("s-1", api.LastAccepted);
        Assert.Null(sam.State.Suggestion);
        Assert.Equal("rev-9", sam.State.LatestReceipt?.Revision.RevisionId);

        api.TurnResult = (_, _) => Turn(suggestion: Suggestion("s-2"));
        sam.SetDraft("Another idea?");
        await sam.SendAsync();
        await sam.RejectSuggestionAsync();
        Assert.Equal("s-2", api.LastRejected);
        Assert.Null(sam.State.Suggestion);
        Assert.Equal("Coach_Rejected", sam.State.NoticeKey);
    }

    [Fact]
    public async Task PendingLocalPracticeMustSyncBeforeATurnIsSent()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        fixture.MarkPendingForTest(NativeSliceFixture.UserA);
        fixture.Sync.Outcome = NativeSyncOutcome.Unconfirmed;
        sam.SetDraft("Make it 5 minutes");
        await sam.SendAsync();
        Assert.Equal(NativeSamStatus.SyncRequired, sam.State.Status);
        Assert.Equal(0, api.Turns);
        Assert.Equal("Make it 5 minutes", sam.State.Draft);

        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        await sam.SendAsync();
        Assert.Equal(1, api.Turns);
        Assert.Contains(NativeSliceFixture.UserA, fixture.Sync.RequestedOwners);
    }

    [Fact]
    public async Task SwitchingConversationsClearsThreadStateAndRefusesUnlistedIds()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("one", minutesAgo: 1));
        api.Conversations.Add(Conversation("two", minutesAgo: 9));
        api.Messages["two"] = [History("x", CoachMessageRole.Coach, "from two", 1)];
        api.TurnResult = (_, _) => Turn(receipt: Receipt("rev-1", CoachRevisionSource.DirectRequest));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        sam.SetDraft("10 minutes");
        await sam.SendAsync();
        Assert.NotNull(sam.State.LatestReceipt);

        await sam.ToggleConversationsAsync();
        Assert.True(sam.State.ConversationsOpen);
        Assert.Equal(2, sam.State.SafeConversations.Length);

        await sam.OpenConversationAsync("someone-elses-thread");
        Assert.Equal("one", sam.State.ConversationId);

        await sam.OpenConversationAsync("two");
        Assert.Equal("two", sam.State.ConversationId);
        Assert.False(sam.State.ConversationsOpen);
        Assert.Null(sam.State.LatestReceipt);
        Assert.Equal(["from two"], sam.State.SafeEntries.Select(entry => entry.Text));
    }

    [Fact]
    public async Task GoneConversationLeavesOnlyTheNotice()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        api.Messages["t"] = [History("m", CoachMessageRole.Coach, "visible", 1)];
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        Assert.NotEmpty(sam.State.SafeEntries);

        api.Gone.Add("t");
        sam.Close();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        Assert.Empty(sam.State.SafeEntries);
        Assert.Null(sam.State.ConversationId);
        Assert.Equal("Coach_ConversationGone", sam.State.NoticeKey);
    }

    [Fact]
    public async Task ApprovedVocabularySetHandsTheResourceToVocabReview()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        api.TurnResult = (_, _) => Turn(vocabularySet: new CoachVocabularySetProposal
        {
            ProposalId = "p-1", Topic = "food", Title = "Food", TargetLanguageTag = "ko",
            Terms = [new CoachVocabularyTermDto { TargetTerm = "밥", NativeTerm = "rice" }]
        });
        api.ApprovalResult = request => new CoachVocabularySetApprovalResponse
        {
            ProposalId = request.ProposalReference, Decision = request.Decision,
            ActivityPath = "/vocab-quiz?resourceIds=res-42"
        };
        var sam = Create(fixture, api);
        (string Owner, string Resource)? launched = null;
        sam.VocabularyReviewRequested += (owner, resource) => launched = (owner, resource);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        sam.SetDraft("start a vocabulary review activity with words about food");
        await sam.SendAsync();
        Assert.Equal("p-1", sam.State.VocabularySet?.ProposalId);

        await sam.ApproveVocabularySetAsync();
        Assert.Equal(ApplicationOperationDecision.Accept, api.LastApproval?.Decision);
        Assert.Null(sam.State.VocabularySet);
        Assert.Equal("res-42", sam.State.ApprovedResourceId);

        sam.StartVocabularyReview();
        Assert.Equal((NativeSliceFixture.UserA, "res-42"), launched);
    }

    [Fact]
    public async Task UnavailableAvailabilityHidesTheEntry()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam { Availability = new CoachAvailabilityResponse
            { IsAvailable = false, State = CoachAvailabilityState.Disabled } };
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        Assert.False(sam.State.IsVisible);
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        Assert.False(sam.State.IsOpen);
        Assert.Single(api.Owners);
    }

    [Theory]
    [InlineData(CoachProblemTypes.PlanVersionConflict, NativeSamStatus.PlanChangedElsewhere)]
    [InlineData(CoachProblemTypes.RateLimited, NativeSamStatus.Limited)]
    [InlineData(CoachProblemTypes.RunCancelled, NativeSamStatus.Ready)]
    [InlineData(CoachProblemTypes.ConversationNotFound, NativeSamStatus.Unavailable)]
    [InlineData(CoachProblemTypes.Timeout, NativeSamStatus.Incomplete)]
    public void ProblemsMapToTheSourceStates(string problem, NativeSamStatus expected) =>
        Assert.Equal(expected, NativeSamPresenter.FromProblem(
            new CoachApiException(HttpStatusCode.Conflict, problem, null, null)));

    [Theory]
    [InlineData("Korean", "ko")]
    [InlineData("ko-KR", "ko")]
    [InlineData("한국어", "ko")]
    [InlineData("Spanish", "en")]
    [InlineData(null, "en")]
    public void PersonaFollowsTheStudyLanguage(string? language, string culture) =>
        Assert.Equal(culture, NativeSamPresenter.PersonaCulture(language));

    // ------------------------------------------------ busy / failure regressions

    public static TheoryData<string, NativeSamStatus> PreflightSessionFailures => new()
    {
        { "network", NativeSamStatus.Offline },
        { "owner", NativeSamStatus.Unavailable },
    };

    [Theory]
    [MemberData(nameof(PreflightSessionFailures))]
    public async Task PlanReReadFailureAfterPreflightSyncReleasesThePanel(string failure, NativeSamStatus expected)
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        fixture.MarkPendingForTest(NativeSliceFixture.UserA);
        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        api.Overrides[nameof(ICoachApiClient.StartSessionAsync)] = _ => Fail<CoachSessionResponse>(
            failure == "network" ? new HttpRequestException("offline")
                : new UnauthorizedAccessException("Sam owner changed."));
        sam.SetDraft("Make it 5 minutes");
        await sam.SendAsync();

        Assert.False(sam.State.Busy);
        Assert.Equal(expected, sam.State.Status);
        Assert.False(sam.State.CanRetry);
        Assert.Equal(0, api.Turns);
        Assert.Equal("Make it 5 minutes", sam.State.Draft);
    }

    [Fact]
    public async Task HttpClientTimeoutIsRecoverableAndNotShownAsStop()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        api.Overrides[nameof(ICoachApiClient.SubmitConversationTurnAsync)] = _ =>
            Fail<CoachTurnOperationDto>(new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException()));
        sam.SetDraft("What should I study?");
        await sam.SendAsync();

        Assert.False(sam.State.Busy);
        Assert.Equal(NativeSamStatus.Incomplete, sam.State.Status);
        Assert.NotEqual("Coach_Stopped", sam.State.NoticeKey);
        Assert.True(sam.State.CanRetry);
        Assert.True(sam.State.CanRecover);

        // The outcome is unknown, so Try Again reuses the keys and the server replays.
        api.Overrides.Remove(nameof(ICoachApiClient.SubmitConversationTurnAsync));
        await sam.RetryAsync();
        Assert.Equal(2, api.Submitted.Count);
        Assert.Equal(api.Submitted[0].OperationId, api.Submitted[1].OperationId);
        Assert.Equal(api.Submitted[0].IdempotencyKey, api.Submitted[1].IdempotencyKey);
        Assert.Equal(NativeSamStatus.Ready, sam.State.Status);
    }

    [Fact]
    public async Task RetryAfterADefinitiveServerFailureSendsUnderNewKeys()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        api.Overrides[nameof(ICoachApiClient.SubmitConversationTurnAsync)] = args =>
            Task.FromResult(Operation((CoachConversationTurnRequest)args[1]!, (string)args[0]!,
                CoachTurnOperationState.Failed));
        sam.SetDraft("Plan my week");
        await sam.SendAsync();
        Assert.False(sam.State.Busy);
        Assert.Equal(NativeSamStatus.Failed, sam.State.Status);
        Assert.True(sam.State.CanRetry);

        api.Overrides.Remove(nameof(ICoachApiClient.SubmitConversationTurnAsync));
        await sam.RetryAsync();
        Assert.Equal(2, api.Submitted.Count);
        Assert.NotEqual(api.Submitted[0].OperationId, api.Submitted[1].OperationId);
        Assert.NotEqual(api.Submitted[0].IdempotencyKey, api.Submitted[1].IdempotencyKey);
        Assert.Equal(NativeSamStatus.Ready, sam.State.Status);
    }

    [Fact]
    public async Task LostResponseRetryKeepsTheSameKeys()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        api.Overrides[nameof(ICoachApiClient.SubmitConversationTurnAsync)] = _ =>
            Fail<CoachTurnOperationDto>(new HttpRequestException("connection reset"));
        sam.SetDraft("Hello");
        await sam.SendAsync();
        Assert.False(sam.State.Busy);
        Assert.Equal(NativeSamStatus.Offline, sam.State.Status);
        Assert.True(sam.State.CanRetry);

        api.Overrides.Remove(nameof(ICoachApiClient.SubmitConversationTurnAsync));
        await sam.RetryAsync();
        Assert.Equal(api.Submitted[0].OperationId, api.Submitted[1].OperationId);
        Assert.Equal(api.Submitted[0].IdempotencyKey, api.Submitted[1].IdempotencyKey);
    }

    [Fact]
    public async Task ConversationGoneMidTurnReleasesBusyAndNewConversationWorks()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        api.Overrides[nameof(ICoachApiClient.SubmitConversationTurnAsync)] = args =>
        {
            api.Gone.Add((string)args[0]!); // the transcript read-back answers 404
            return Task.FromResult(Operation((CoachConversationTurnRequest)args[1]!, (string)args[0]!,
                CoachTurnOperationState.Completed, Turn()));
        };
        sam.SetDraft("Hello");
        await sam.SendAsync();

        Assert.False(sam.State.Busy);
        Assert.False(sam.State.StillWorking);
        Assert.False(sam.State.CanStop);
        Assert.Null(sam.State.ConversationId);
        Assert.Equal("Coach_ConversationGone", sam.State.NoticeKey);

        await sam.NewConversationAsync();
        Assert.Equal(1, api.Created);
        Assert.Equal("created-1", sam.State.ConversationId);
        Assert.Equal(NativeSamStatus.Ready, sam.State.Status);
    }

    public static TheoryData<string, NativeSamStatus> SessionReadFailures => new()
    {
        { "network", NativeSamStatus.Offline },
        { "owner", NativeSamStatus.Unavailable },
    };

    [Theory]
    [MemberData(nameof(SessionReadFailures))]
    public async Task SessionReadFailureOnOpeningAConversationIsAnHonestStatus(
        string failure, NativeSamStatus expected)
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("one", minutesAgo: 1));
        api.Conversations.Add(Conversation("two", minutesAgo: 9));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        await sam.ToggleConversationsAsync();

        api.Overrides[nameof(ICoachApiClient.StartSessionAsync)] = _ => Fail<CoachSessionResponse>(
            failure == "network" ? new HttpRequestException("offline")
                : new UnauthorizedAccessException("token owner mismatch"));
        await sam.OpenConversationAsync("two");
        Assert.Equal(expected, sam.State.Status);
        Assert.False(sam.State.Busy);

        // Re-opening the panel on the current conversation takes the same path.
        sam.Close();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        Assert.Equal(expected, sam.State.Status);
    }

    [Fact]
    public async Task VocabularyDecisionIsRefusedWhileATurnIsRunning()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        api.TurnResult = (_, _) => Turn(vocabularySet: new CoachVocabularySetProposal
        {
            ProposalId = "p-1", Topic = "food", Title = "Food", TargetLanguageTag = "ko",
            Terms = [new CoachVocabularyTermDto { TargetTerm = "밥", NativeTerm = "rice" }]
        });
        api.ApprovalResult = request => new CoachVocabularySetApprovalResponse
        {
            ProposalId = request.ProposalReference, Decision = request.Decision,
            ActivityPath = "/vocab-quiz?resourceIds=res-42"
        };
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        sam.SetDraft("words about food");
        await sam.SendAsync();
        Assert.Equal("p-1", sam.State.VocabularySet?.ProposalId);

        var busyDuringTurn = false;
        api.Overrides[nameof(ICoachApiClient.SubmitConversationTurnAsync)] = args =>
        {
            busyDuringTurn = sam.State.Busy;
            _ = sam.ApproveVocabularySetAsync();
            return Task.FromResult(Operation((CoachConversationTurnRequest)args[1]!, (string)args[0]!,
                CoachTurnOperationState.Completed, Turn()));
        };
        sam.SetDraft("one more question");
        await sam.SendAsync();

        Assert.True(busyDuringTurn);
        Assert.Null(api.LastApproval);
    }

    // ------------------------------------------------------------------ writes, memory, reports, management

    [Fact]
    public async Task SoftWriteAppliesOnlyOnAcceptAndOffersTheReceiptUndo()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.TurnResult = (_, conversation) => Turn(write: Write("w-1", conversation, hard: false));
        var sam = await OpenAsync(fixture, api);

        sam.SetDraft("add 사과 to my words");
        await sam.SendAsync();

        var entry = Assert.Single(sam.State.SafeEntries, item => item.Write is not null);
        Assert.Equal("w-1", sam.State.ActiveWriteId);
        var stage = NativeSamWritePresentation.Stage(entry.Write, true, false, DateTime.UtcNow);
        Assert.Equal(NativeSamWriteStage.Proposed, stage);
        Assert.True(NativeSamWritePresentation.ShowsAccept(entry.Write!, stage));
        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.AcceptWriteAsync)));

        await sam.AcceptWriteAsync("w-1");

        var applied = Assert.Single(sam.State.SafeEntries, item => item.Write is not null).Write!;
        Assert.Equal(CoachWriteStatus.Executed, applied.Status);
        Assert.Null(sam.State.ActiveWriteId);
        Assert.True(NativeSamWritePresentation.ShowsUndo(applied,
            NativeSamWritePresentation.Stage(applied, false, false, DateTime.UtcNow), DateTime.UtcNow));

        await sam.UndoWriteAsync("w-1");

        Assert.Equal(CoachWriteStatus.Undone,
            Assert.Single(sam.State.SafeEntries, item => item.Write is not null).Write!.Status);
        Assert.All(api.Owners, owner => Assert.Equal(NativeSliceFixture.UserA, owner));
    }

    [Fact]
    public async Task HardWriteNeedsTheConfirmStepAndNeverPublishesTheOneUseValue()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.TurnResult = (_, conversation) => Turn(write: Write("w-hard", conversation, hard: true));
        var sam = await OpenAsync(fixture, api);
        sam.SetDraft("remove 사과");
        await sam.SendAsync();

        // The ordinary acceptance channel is refused for a protected change: nothing is sent.
        await sam.AcceptWriteAsync("w-hard");
        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.AcceptWriteAsync)));

        await sam.BeginWriteConfirmationAsync("w-hard");
        Assert.Equal("w-hard", sam.State.ConfirmingWriteId);
        Assert.NotNull(sam.State.ConfirmationExpiresAtUtc);
        Assert.DoesNotContain("one-use-secret-value", sam.State.ToString(), StringComparison.Ordinal);

        sam.CancelWriteConfirmation();
        Assert.Null(sam.State.ConfirmingWriteId);
        await sam.ConfirmWriteAsync();
        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.ConfirmWriteAsync)));

        await sam.BeginWriteConfirmationAsync("w-hard");
        await sam.ConfirmWriteAsync();

        var confirm = Assert.Single(api.Calls, call => call.Name == nameof(ICoachApiClient.ConfirmWriteAsync));
        Assert.Equal("one-use-secret-value", ((CoachWriteConfirmation)confirm.Args[2]!).Value);
        Assert.Null(sam.State.ConfirmingWriteId);
        Assert.Equal(CoachWriteStatus.Executed,
            Assert.Single(sam.State.SafeEntries, item => item.Write is not null).Write!.Status);
    }

    [Fact]
    public async Task AWriteTheServerNoLongerKnowsBecomesUnavailableWithoutGuessingAVerdict()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.TurnResult = (_, conversation) => Turn(write: Write("w-gone", conversation, hard: false));
        var sam = await OpenAsync(fixture, api);
        sam.SetDraft("add a word");
        await sam.SendAsync();
        api.Writes.Remove("w-gone");

        await sam.AcceptWriteAsync("w-gone");

        Assert.True(sam.State.IsWriteUnavailable("w-gone"));
        Assert.Equal("Coach_WriteUnavailable", sam.State.WriteErrorKey);
        Assert.Null(sam.State.ActiveWriteId);
        var write = Assert.Single(sam.State.SafeEntries, item => item.Write is not null).Write;
        Assert.Equal(NativeSamWriteStage.Unavailable,
            NativeSamWritePresentation.Stage(write, false, false, DateTime.UtcNow, isUnavailable: true));
    }

    [Fact]
    public async Task WritesStayHiddenWhenTheServerHasNoWriteTools()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = new FakeSam();
        api.Conversations.Add(Conversation("t"));
        api.TurnResult = (_, conversation) => Turn(write: Write("w-off", conversation, hard: false));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        sam.SetDraft("add a word");
        await sam.SendAsync();

        Assert.False(sam.State.WriteSurfaceEnabled);
        await sam.AcceptWriteAsync("w-off");
        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.AcceptWriteAsync)));
    }

    [Fact]
    public async Task MemoryCandidateIsStoredOnlyOnSaveAndEditApprovesInOneCall()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        var candidate = Memory("m-1", "Pass TOPIK II");
        api.CandidateMemories.Add(candidate);
        api.TurnResult = (_, _) => Turn(memory: candidate);
        var sam = await OpenAsync(fixture, api);
        sam.SetDraft("remember my goal is TOPIK II");
        await sam.SendAsync();

        Assert.Equal("m-1", sam.State.MemoryCandidate?.Id);
        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.ApproveMemoryAsync)));

        sam.BeginMemoryCandidateEdit();
        Assert.Equal("Pass TOPIK II", sam.State.MemoryCandidateEdit);
        sam.SetMemoryCandidateEdit("Pass TOPIK II by June");
        await sam.SaveEditedMemoryCandidateAsync();

        var approve = Assert.Single(api.Calls, call => call.Name == nameof(ICoachApiClient.ApproveMemoryAsync));
        var request = (Contracts.LearnerMemory.CoachMemoryApproveRequest)approve.Args[1]!;
        Assert.Equal(candidate.Version, request.ExpectedVersion);
        Assert.Equal("Pass TOPIK II by June", request.EditedValue?.StudyGoalText);
        Assert.Null(sam.State.MemoryCandidate);
        Assert.Equal("Coach_MemorySaved", sam.State.MemoryCandidateNoticeKey);
    }

    [Fact]
    public async Task NotNowOnAMemoryCandidateRemembersNothing()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        var candidate = Memory("m-2", "Casual examples");
        api.TurnResult = (_, _) => Turn(memory: candidate);
        var sam = await OpenAsync(fixture, api);
        sam.SetDraft("use casual examples");
        await sam.SendAsync();

        await sam.DeclineMemoryCandidateAsync();

        Assert.Single(api.Calls, call => call.Name == nameof(ICoachApiClient.RejectMemoryAsync));
        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.ApproveMemoryAsync)));
        Assert.Null(sam.State.MemoryCandidate);
    }

    [Fact]
    public async Task ReportGoesOnlyToASettledSamResponseAndSurvivesReload()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.Conversations.Add(Conversation("t"));
        api.Messages["t"] = [History("l1", CoachMessageRole.Learner, "hi", 1),
            History("c1", CoachMessageRole.Coach, "answer", 2)];
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        Assert.True(sam.State.ReportingAvailable);

        sam.OpenReport("l1");
        Assert.Null(sam.State.ReportingMessageId);
        sam.OpenReport("c1");
        Assert.Equal("c1", sam.State.ReportingMessageId);
        await sam.SubmitReportAsync();
        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.ReportResponseAsync)));
        sam.SelectReportReason(CoachResponseReportReason.Confusing);
        await sam.SubmitReportAsync();

        var report = Assert.Single(api.Calls, call => call.Name == nameof(ICoachApiClient.ReportResponseAsync));
        Assert.Equal(CoachResponseReportReason.Confusing, ((CoachResponseReportRequest)report.Args[2]!).Reason);
        Assert.True(sam.State.IsReported("c1"));
        Assert.Null(sam.State.ReportingMessageId);

        sam.Close();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        Assert.True(sam.State.IsReported("c1"));
        sam.OpenReport("c1");
        Assert.Null(sam.State.ReportingMessageId);
    }

    [Fact]
    public async Task SwitchingConversationWhileAReportIsSendingFreesTheReportFlag()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.Conversations.Add(Conversation("t"));
        api.Conversations.Add(Conversation("other", minutesAgo: 30));
        api.Messages["t"] = [History("l1", CoachMessageRole.Learner, "hi", 1),
            History("c1", CoachMessageRole.Coach, "answer", 2)];
        var sam = await OpenAsync(fixture, api, addConversation: false);
        var pending = new TaskCompletionSource<CoachResponseReportResponse?>();
        api.Overrides[nameof(ICoachApiClient.ReportResponseAsync)] = _ => pending.Task;

        sam.OpenReport("c1");
        sam.SelectReportReason(CoachResponseReportReason.Confusing);
        var sending = sam.SubmitReportAsync();
        Assert.True(sam.State.ReportBusy);

        await sam.OpenConversationAsync("other");
        pending.SetResult(new CoachResponseReportResponse
        {
            MessageId = "c1", Reason = CoachResponseReportReason.Confusing,
            State = CoachResponseReportState.Recorded, ReportedAtUtc = DateTime.UtcNow,
        });
        await sending;

        Assert.False(sam.State.ReportBusy);
    }

    [Fact]
    public async Task OnlyTheActiveProposalCanBeAccepted()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        var next = 0;
        api.TurnResult = (_, conversation) =>
            Turn(write: Write($"w-{++next}", conversation, hard: false));
        var sam = await OpenAsync(fixture, api);

        sam.SetDraft("add 사과");
        await sam.SendAsync();
        sam.SetDraft("add 배 instead");
        await sam.SendAsync();
        Assert.Equal("w-2", sam.State.ActiveWriteId);

        await sam.AcceptWriteAsync("w-1");
        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.AcceptWriteAsync)));

        await sam.AcceptWriteAsync("w-2");
        Assert.Equal(1, api.CallCount(nameof(ICoachApiClient.AcceptWriteAsync)));
    }

    [Fact]
    public async Task ANewConversationOffersReporting()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        Assert.Equal("created-1", sam.State.ConversationId);
        Assert.True(sam.State.ReportingAvailable);
    }

    [Fact]
    public async Task ReportingSwitchedOffOffersNoControl()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.ReportsEnabled = false;
        api.Conversations.Add(Conversation("t"));
        api.Messages["t"] = [History("c1", CoachMessageRole.Coach, "answer", 1)];
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);

        Assert.False(sam.State.ReportingAvailable);
        sam.OpenReport("c1");
        Assert.Null(sam.State.ReportingMessageId);
    }

    [Fact]
    public async Task DeleteHistoryDeletesTheCurrentSessionOnlyAndClaimsNothingOnFailure()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.Conversations.Add(Conversation("session-1"));
        api.Conversations.Add(Conversation("other", minutesAgo: 30));
        var sam = await OpenAsync(fixture, api, addConversation: false);
        Assert.Equal("session-1", sam.State.ConversationId);

        sam.AskDeleteHistory();
        Assert.True(sam.State.SafeSettings.ConfirmingDelete);
        await sam.ConfirmDeleteHistoryAsync();

        var delete = Assert.Single(api.Calls, call => call.Name == nameof(ICoachApiClient.DeleteSessionAsync));
        Assert.Equal("session-1", delete.Args[0]);
        Assert.Equal("Coach_SettingsDeleted", sam.State.SafeSettings.NoticeKey);
        Assert.Null(sam.State.ConversationId);
        Assert.Equal(["other"], sam.State.SafeConversations.Select(row => row.Id));

        // With no session held, nothing is resumed or created just to be deleted.
        var startsBefore = api.CallCount(nameof(ICoachApiClient.StartSessionAsync));
        sam.AskDeleteHistory();
        await sam.ConfirmDeleteHistoryAsync();
        Assert.Equal("NativeSam_CoachHistoryNothingToDelete", sam.State.SafeSettings.NoticeKey);
        Assert.Equal(startsBefore, api.CallCount(nameof(ICoachApiClient.StartSessionAsync)));
        Assert.Single(api.Calls, call => call.Name == nameof(ICoachApiClient.DeleteSessionAsync));

        await sam.OpenConversationAsync("other");
        api.Overrides[nameof(ICoachApiClient.DeleteSessionAsync)] = _ =>
            Task.FromException(new CoachApiException(HttpStatusCode.InternalServerError, null, null, null));
        sam.AskDeleteHistory();
        await sam.ConfirmDeleteHistoryAsync();
        Assert.Equal("NativeSam_CoachHistoryDeleteFailed", sam.State.SafeSettings.NoticeKey);
    }

    [Fact]
    public async Task RenameCloseAndDeleteSendTheConcurrencyTokenAndDeleteDropsTheOpenThread()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.Conversations.Add(Conversation("t"));
        api.Messages["t"] = [History("c1", CoachMessageRole.Coach, "secret answer", 1)];
        var sam = await OpenAsync(fixture, api, addConversation: false);

        sam.BeginConversationDialog(NativeSamConversationDialogKind.Rename, "t");
        sam.SetRenameText("  Particles  ");
        await sam.ConfirmConversationDialogAsync();
        var rename = (UpdateCoachConversationRequest)Assert.Single(api.Calls,
            call => call.Name == nameof(ICoachApiClient.UpdateConversationAsync)).Args[1]!;
        Assert.Equal(1, rename.ExpectedStateVersion);
        Assert.Equal("Particles", rename.Title);
        Assert.Equal("Particles", sam.State.SafeConversations.Single().Title);
        Assert.Equal("Coach_ConversationRenamed", sam.State.ConversationActionKey);

        sam.BeginConversationDialog(NativeSamConversationDialogKind.Close, "t");
        await sam.ConfirmConversationDialogAsync();
        Assert.True(sam.State.ConversationClosed);
        Assert.False(sam.State.CanSend);
        await sam.ReopenConversationAsync("t");
        Assert.False(sam.State.ConversationClosed);

        sam.BeginConversationDialog(NativeSamConversationDialogKind.Delete, "t");
        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.DeleteConversationAsync)));
        await sam.ConfirmConversationDialogAsync();
        Assert.Single(api.Calls, call => call.Name == nameof(ICoachApiClient.DeleteConversationAsync));
        Assert.Null(sam.State.ConversationId);
        Assert.Empty(sam.State.SafeEntries);
        Assert.Empty(sam.State.SafeConversations);
    }

    [Fact]
    public async Task MemoryPanelHidesWhenUnavailableAndForgetAllRefreshes()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.ActiveMemories.Add(Memory("a-1", "Detailed explanations") with
            { Status = Contracts.LearnerMemory.CoachMemoryStatus.Active });
        var sam = Create(fixture, api);

        await sam.LoadMemoryAsync();
        Assert.Equal(NativeSamMemoryAvailability.Available, sam.State.SafeMemory.Availability);
        Assert.Single(sam.State.SafeMemory.SafeActive);

        sam.AskForgetAllMemories();
        await sam.ForgetAllMemoriesAsync();
        Assert.Single(api.Calls, call => call.Name == nameof(ICoachApiClient.ForgetAllMemoriesAsync));
        Assert.True(sam.State.SafeMemory.IsPaused);
        Assert.Equal("Coach_MemorySaved", sam.State.SafeMemory.NoticeKey);

        api.Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true, State = CoachAvailabilityState.Available, IsSamOverlayAvailable = true,
            IsDurableHistoryAvailable = true, IsMemoryAvailable = false
        };
        await sam.RefreshAvailabilityAsync();
        await sam.LoadMemoryAsync();
        Assert.Equal(NativeSamMemoryAvailability.Unavailable, sam.State.SafeMemory.Availability);
    }

    [Fact]
    public async Task OwnerChangeClearsWritesMemoryReportsAndSettings()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.ActiveMemories.Add(Memory("a-1", "owner A goal"));
        api.TurnResult = (_, conversation) => Turn(write: Write("w-a", conversation, hard: false),
            memory: Memory("m-a", "owner A candidate"));
        var sam = await OpenAsync(fixture, api);
        sam.SetDraft("add a word");
        await sam.SendAsync();
        await sam.LoadMemoryAsync();
        Assert.NotNull(sam.State.MemoryCandidate);
        Assert.NotEmpty(sam.State.SafeMemory.SafeActive);

        fixture.Activate(NativeSliceFixture.UserB);
        api.Owners.Clear();
        await sam.AcceptWriteAsync("w-a");

        Assert.Equal(0, api.CallCount(nameof(ICoachApiClient.AcceptWriteAsync)));
        Assert.Null(sam.State.MemoryCandidate);
        Assert.Null(sam.State.ActiveWriteId);
        Assert.Empty(sam.State.SafeMemory.SafeActive);
        Assert.Null(sam.State.Reported);
        Assert.False(sam.State.SafeSettings.IsAvailable);
        Assert.Empty(api.Owners);
    }

    [Fact]
    public async Task RepairNoteAttachesToTheAnswerButNeverBesideARefusal()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.TurnResult = (_, _) => Turn(repair: CoachRepairDisclosure.AnswerAltered);
        var sam = await OpenAsync(fixture, api);
        sam.SetDraft("how much did I practice this week?");
        await sam.SendAsync();
        Assert.Equal(CoachRepairDisclosure.AnswerAltered,
            sam.State.SafeEntries.Last(entry => entry.Kind == NativeSamEntryKind.Sam).Repair);

        api.TurnResult = (_, _) => Turn(repair: CoachRepairDisclosure.AnswerAltered,
            limitation: new CoachLimitationDto { Code = CoachLimitationCode.UnverifiedClaimWithheld });
        sam.SetDraft("and last week?");
        await sam.SendAsync();
        Assert.Null(sam.State.SafeEntries.Last(entry => entry.Kind == NativeSamEntryKind.Sam).Repair);
    }

    [Fact]
    public async Task ARejectedAnswerShapeShowsAFailureWithTryAgainUnderNewKeys()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var api = WriteEnabled();
        api.TurnResult = (_, _) => Turn(CoachTurnStatus.Rejected, CoachStopReason.ValidationFailed);
        var sam = await OpenAsync(fixture, api);
        sam.SetDraft("What does 좋아하다 mean?");
        await sam.SendAsync();

        Assert.Equal(NativeSamStatus.Failed, sam.State.Status);
        Assert.Equal("NativeSam_AnswerFailed", sam.State.FailureKey);
        Assert.True(sam.State.CanRetry);
        Assert.False(sam.State.Busy);

        api.TurnResult = (_, _) => Turn();
        await sam.RetryAsync();

        Assert.Equal(2, api.Submitted.Count);
        Assert.NotEqual(api.Submitted[0].OperationId, api.Submitted[1].OperationId);
        Assert.NotEqual(api.Submitted[0].IdempotencyKey, api.Submitted[1].IdempotencyKey);
        Assert.NotEqual(api.Submitted[0].Turn.ClientTurnId, api.Submitted[1].Turn.ClientTurnId);
        Assert.Equal(NativeSamStatus.Ready, sam.State.Status);
    }

    [Fact]
    public void ACompletedTurnThatStoppedEarlyWithNothingToShowIsIncomplete()
    {
        Assert.Equal(NativeSamStatus.Incomplete,
            NativeSamPresenter.FromTurn(Turn(stop: CoachStopReason.ToolFailure)));
        Assert.Equal(NativeSamStatus.Ready, NativeSamPresenter.FromTurn(Turn()));
        Assert.Equal(NativeSamStatus.Ready, NativeSamPresenter.FromTurn(Turn(CoachTurnStatus.Rejected,
            CoachStopReason.ValidationFailed,
            limitation: new CoachLimitationDto { Code = CoachLimitationCode.RefusedByDesign })));
    }

    private static FakeSam WriteEnabled() => new()
    {
        Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true, State = CoachAvailabilityState.Available, CanEditPlan = true,
            IsDurableHistoryAvailable = true, IsSamOverlayAvailable = true, IsSamWriteAvailable = true,
            IsMemoryAvailable = true
        }
    };

    private static async Task<NativeSamPresenter> OpenAsync(NativeSliceFixture fixture, FakeSam api,
        bool addConversation = true)
    {
        if (addConversation)
            api.Conversations.Add(Conversation("t"));
        var sam = Create(fixture, api);
        await sam.RefreshAvailabilityAsync();
        await sam.OpenAsync(NativeSamPanelSize.Compact);
        return sam;
    }

    private static CoachWriteOperationDto Write(string id, string conversation, bool hard) => new()
    {
        OperationId = id, ConversationId = conversation,
        ChangeKind = hard ? CoachWriteChangeKind.VocabularyRemove : CoachWriteChangeKind.VocabularyAdd,
        RiskClass = hard ? CoachWriteRiskClass.WriteHard : CoachWriteRiskClass.WriteSoft,
        Status = CoachWriteStatus.Proposed, ApprovalMode = hard ? "confirm" : "accept",
        Summary = hard ? "Remove 사과" : "Add 사과", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        RequiresConfirmation = hard, IsReversible = !hard
    };

    private static Contracts.LearnerMemory.CoachMemoryFactDto Memory(string id, string text) => new(
        id, Contracts.LearnerMemory.CoachMemoryKind.PersistentStudyGoal,
        Contracts.LearnerMemory.CoachMemoryStatus.Candidate, Contracts.LearnerMemory.CoachMemoryScope.TargetLanguage,
        "ko", new Contracts.LearnerMemory.CoachMemoryValueDto
        {
            Kind = Contracts.LearnerMemory.CoachMemoryKind.PersistentStudyGoal, StudyGoalText = text
        },
        text, Contracts.LearnerMemory.CoachMemoryProvenance.UserExplicit, 1, DateTime.UtcNow, DateTime.UtcNow,
        null, null, null, null, 3);

    // ------------------------------------------------------------------ helpers

    private static Task<T> Fail<T>(Exception ex) => Task.FromException<T>(ex);

    private static CoachTurnOperationDto Operation(CoachConversationTurnRequest request, string conversation,
        CoachTurnOperationState state, CoachTurnResponse? result = null) => new()
    {
        OperationId = request.OperationId!, ConversationId = conversation, State = state, Result = result,
        Messages = [], CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
    };

    private static NativeSamPresenter Create(NativeSliceFixture fixture, FakeSam api) =>
        new(api, fixture.UserScope, fixture.Preferences,
            fixture.Provider.GetRequiredService<IConnectivityService>(), fixture.Sync,
            fixture.Provider.GetRequiredService<NativePendingWrites>(),
            fixture.Provider.GetRequiredService<UserProfileRepository>(),
            NullLogger<NativeSamPresenter>.Instance);

    private static CoachConversationDto Conversation(string id, bool closed = false, int minutesAgo = 1) => new()
    {
        ConversationId = id, Title = id, TitleOrigin = CoachConversationTitleOrigin.Generated,
        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo), UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
        HistoryStartsAtUtc = DateTime.UtcNow.AddDays(-1), MessageCount = 0, StateVersion = 1,
        HasActiveCheckpoint = true, IsClosed = closed
    };

    private static CoachHistoryMessageDto History(string id, CoachMessageRole role, string text, long sequence) => new()
    {
        Message = new CoachMessageDto
        {
            MessageId = id, Role = role, Kind = CoachMessageKind.Text, Text = text,
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(sequence)
        },
        Sequence = sequence
    };

    private static CoachConstraintSetDto Constraints() => new()
    {
        AvailableMinutes = 12, AudioAllowed = true, SpeechAllowed = true, TypingAllowed = true,
        EnergyLevel = CoachEnergyLevel.Normal
    };

    private static CoachPlanStateDto Plan(string version = "v1") => new()
    {
        PlanDate = new DateOnly(2026, 10, 31), PlanVersion = version, AppliedConstraints = Constraints(),
        EstimatedTotalMinutes = 12, CompletedCount = 0, TotalCount = 2, CompletionPercentage = 0
    };

    private static CoachPlanDiffDto Diff() => new()
    {
        BeforePlanVersion = "v1", AfterPlanVersion = "v2", IsPreview = false,
        EstimatedMinutesBefore = 12, EstimatedMinutesAfter = 10
    };

    private static CoachChangeReceiptDto Receipt(string revision, CoachRevisionSource source, bool canUndo = true) => new()
    {
        ReceiptId = "r-" + revision,
        Revision = new CoachRevisionDto
        {
            RevisionId = revision, RevisionNumber = 1, Source = source, Summary = "s", BeforePlanVersion = "v1",
            AfterPlanVersion = "v2", CreatedAtUtc = DateTime.UtcNow
        },
        Summary = "updated", AppliedDelta = new CoachConstraintDeltaDto(), Diff = Diff(),
        ReplacedItemCount = 2, PreservedCompletedItemCount = 0, PreservedInProgressItemCount = 0,
        PreservedMinutesSpent = 0, CanUndo = canUndo, UndoLabel = "Undo"
    };

    private static PendingCoachSuggestionDto Suggestion(string id) => new()
    {
        SuggestionId = id, Delta = new CoachConstraintDeltaDto(), Rationale = "Lighter today",
        Preview = Diff() , AcceptLabel = "Include it", RejectLabel = "Not now",
        CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    private static CoachTurnResponse Turn(
        CoachTurnStatus status = CoachTurnStatus.Completed,
        CoachStopReason stop = CoachStopReason.Completed,
        CoachChangeReceiptDto? receipt = null,
        PendingCoachSuggestionDto? suggestion = null,
        CoachLimitationDto? limitation = null,
        IReadOnlyList<CoachEvidenceDto>? evidence = null,
        CoachVocabularySetProposal? vocabularySet = null,
        CoachWriteOperationDto? write = null,
        Contracts.LearnerMemory.CoachMemoryFactDto? memory = null,
        CoachRepairDisclosure? repair = null) => new()
    {
        SessionId = "session-1", TurnId = Guid.NewGuid().ToString("N"), Status = status, StopReason = stop,
        SessionStatus = CoachSessionStatus.Active, ActiveConstraints = Constraints(), PlanState = Plan(),
        ChangeReceipt = receipt, PendingSuggestion = suggestion, Limitation = limitation,
        Evidence = evidence ?? [], ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        VocabularySetProposal = vocabularySet, WriteOperation = write, MemoryCandidate = memory,
        RepairDisclosure = repair
    };

    /// <summary>Fake owner-bound client over a dispatch-proxied ICoachApiClient.</summary>
    private sealed class FakeSam : INativeSamClient
    {
        public List<string> Owners { get; } = [];
        public List<CoachConversationDto> Conversations { get; } = [];
        public Dictionary<string, List<CoachHistoryMessageDto>> Messages { get; } = new();
        public CoachAvailabilityResponse Availability { get; set; } = new()
        {
            IsAvailable = true, State = CoachAvailabilityState.Available, CanEditPlan = true,
            IsDurableHistoryAvailable = true, IsSamOverlayAvailable = true
        };
        public Func<CoachTurnRequest, string, CoachTurnResponse> TurnResult { get; set; } =
            (_, _) => Turn();
        public Func<string, CoachTurnResponse> AcceptResult { get; set; } = _ => Turn();
        public Func<CoachUndoRequest, CoachTurnResponse> UndoResult { get; set; } = _ => Turn();
        public Func<ApproveCoachVocabularySetRequest, CoachVocabularySetApprovalResponse>? ApprovalResult { get; set; }
        public int Turns { get; private set; }
        public int Created { get; private set; }
        public string? LastAccepted { get; private set; }
        public string? LastRejected { get; private set; }
        public CoachUndoRequest? LastUndo { get; private set; }
        public ApproveCoachVocabularySetRequest? LastApproval { get; private set; }
        private CoachVocabularySetProposal? _pendingSet;
        private long _sequence = 100;

        public bool IsConfigured => true;

        public Task<T> CallAsync<T>(string owner, Func<ICoachApiClient, CancellationToken, Task<T>> call,
            CancellationToken cancellationToken)
        {
            Assert.False(string.IsNullOrWhiteSpace(owner));
            Owners.Add(owner);
            var proxy = DispatchProxy.Create<ICoachApiClient, Api>();
            ((Api)(object)proxy).Owner = this;
            return call(proxy, cancellationToken);
        }

        public Dictionary<string, Func<object?[], object?>> Overrides { get; } = new();
        public List<CoachConversationTurnRequest> Submitted { get; } = [];

        private object? Handle(string name, object?[] args)
        {
            if (name == nameof(ICoachApiClient.SubmitConversationTurnAsync))
                Submitted.Add((CoachConversationTurnRequest)args[1]!);
            if (Overrides.TryGetValue(name, out var handler))
                return handler(args);
            switch (name)
            {
                case nameof(ICoachApiClient.GetAvailabilityAsync):
                    return Task.FromResult(Availability);
                case nameof(ICoachApiClient.ListConversationsAsync):
                    return Task.FromResult<CoachConversationPageDto?>(new CoachConversationPageDto
                        { Items = Conversations.ToList() });
                case nameof(ICoachApiClient.CreateConversationAsync):
                    Created++;
                    var created = Conversation("created-" + Created);
                    Conversations.Insert(0, created);
                    return Task.FromResult(created);
                case nameof(ICoachApiClient.StartSessionAsync):
                    return Task.FromResult(new CoachSessionResponse
                    {
                        SessionId = "session-1", Status = CoachSessionStatus.Active,
                        ActiveConstraints = Constraints(), PlanState = Plan(),
                        CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
                    });
                case nameof(ICoachApiClient.GetConversationMessagesAsync):
                    var id = (string)args[0]!;
                    return Task.FromResult(Gone.Contains(id) ? null
                        : new CoachMessagePageDto
                        {
                            ConversationId = id,
                            Items = Messages.TryGetValue(id, out var items) ? items.ToList() : []
                        });
                case nameof(ICoachApiClient.GetConversationVocabularyStateAsync):
                    return Task.FromResult<CoachConversationVocabularyStateDto?>(
                        new CoachConversationVocabularyStateDto { PendingProposal = _pendingSet });
                case nameof(ICoachApiClient.SubmitConversationTurnAsync):
                {
                    Turns++;
                    var conversation = (string)args[0]!;
                    var request = (CoachConversationTurnRequest)args[1]!;
                    var result = TurnResult(request.Turn, conversation);
                    _pendingSet = result.VocabularySetProposal;
                    var list = Messages.TryGetValue(conversation, out var existing) ? existing : Messages[conversation] = [];
                    if (request.Turn.Text is { } text)
                        list.Add(History("l" + _sequence, CoachMessageRole.Learner, text, ++_sequence));
                    var plain = History("a" + _sequence, CoachMessageRole.Coach, "answer", ++_sequence);
                    var answer = result.WriteOperation is null ? plain : new CoachHistoryMessageDto
                    {
                        Message = plain.Message, Sequence = plain.Sequence, WriteOperation = result.WriteOperation
                    };
                    if (result.WriteOperation is { } proposed)
                        Writes[proposed.OperationId] = proposed;
                    list.Add(answer);
                    return Task.FromResult(new CoachTurnOperationDto
                    {
                        OperationId = request.OperationId, ConversationId = conversation,
                        State = CoachTurnOperationState.Completed, Result = result, Messages = [answer],
                        CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
                    });
                }
                case nameof(ICoachApiClient.AcceptSuggestionAsync):
                    LastAccepted = (string)args[1]!;
                    return Task.FromResult(AcceptResult(LastAccepted));
                case nameof(ICoachApiClient.RejectSuggestionAsync):
                    LastRejected = (string)args[1]!;
                    return Task.FromResult(Turn());
                case nameof(ICoachApiClient.UndoAsync):
                    LastUndo = (CoachUndoRequest)args[1]!;
                    return Task.FromResult(UndoResult(LastUndo));
                case nameof(ICoachApiClient.ApproveVocabularySetAsync):
                    LastApproval = (ApproveCoachVocabularySetRequest)args[0]!;
                    _pendingSet = null;
                    return Task.FromResult(ApprovalResult!(LastApproval));
                case nameof(ICoachApiClient.GetReportedResponsesAsync):
                    Calls.Add((name, args));
                    return Task.FromResult(ReportsEnabled
                        ? new CoachReportedResponsesDto { MessageIds = Reported.ToList() } : null);
                case nameof(ICoachApiClient.ReportResponseAsync):
                    Calls.Add((name, args));
                    Reported.Add((string)args[1]!);
                    return Task.FromResult<CoachResponseReportResponse?>(new CoachResponseReportResponse
                    {
                        MessageId = (string)args[1]!, Reason = ((CoachResponseReportRequest)args[2]!).Reason,
                        State = CoachResponseReportState.Recorded, ReportedAtUtc = DateTime.UtcNow
                    });
                case nameof(ICoachApiClient.GetWriteOperationAsync):
                case nameof(ICoachApiClient.AcceptWriteAsync):
                case nameof(ICoachApiClient.RejectWriteAsync):
                case nameof(ICoachApiClient.UndoWriteAsync):
                case nameof(ICoachApiClient.ConfirmWriteAsync):
                {
                    Calls.Add((name, args));
                    var operationId = (string)args[1]!;
                    if (!Writes.TryGetValue(operationId, out var current))
                        return Task.FromResult<CoachWriteOperationDto?>(null);
                    var next = name switch
                    {
                        nameof(ICoachApiClient.AcceptWriteAsync) or nameof(ICoachApiClient.ConfirmWriteAsync)
                            => Settle(current, CoachWriteStatus.Executed),
                        nameof(ICoachApiClient.RejectWriteAsync) => Settle(current, CoachWriteStatus.Rejected),
                        nameof(ICoachApiClient.UndoWriteAsync) => Settle(current, CoachWriteStatus.Undone),
                        _ => current,
                    };
                    Writes[operationId] = next;
                    return Task.FromResult<CoachWriteOperationDto?>(next);
                }
                case nameof(ICoachApiClient.RequestWriteConfirmationAsync):
                    Calls.Add((name, args));
                    return Task.FromResult<CoachWriteConfirmation?>(new CoachWriteConfirmation
                    {
                        OperationId = (string)args[1]!, Value = "one-use-secret-value",
                        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
                    });
                case nameof(ICoachApiClient.ListActiveMemoriesAsync):
                    return Task.FromResult<CoachMemoryPageDto?>(MemoryEnabled
                        ? new CoachMemoryPageDto(ActiveMemories.ToList(), null) : null);
                case nameof(ICoachApiClient.ListMemoryCandidatesAsync):
                    return Task.FromResult<CoachMemoryPageDto?>(MemoryEnabled
                        ? new CoachMemoryPageDto(CandidateMemories.ToList(), null) : null);
                case nameof(ICoachApiClient.ApproveMemoryAsync):
                {
                    Calls.Add((name, args));
                    var factId = (string)args[0]!;
                    var fact = CandidateMemories.Concat(ActiveMemories).FirstOrDefault(item => item.Id == factId);
                    CandidateMemories.RemoveAll(item => item.Id == factId);
                    if (fact is not null)
                        ActiveMemories.Add(fact with { Status = Contracts.LearnerMemory.CoachMemoryStatus.Active });
                    return Task.FromResult(fact);
                }
                case nameof(ICoachApiClient.RejectMemoryAsync):
                    Calls.Add((name, args));
                    CandidateMemories.RemoveAll(item => item.Id == (string)args[0]!);
                    return Task.CompletedTask;
                case nameof(ICoachApiClient.ForgetMemoryAsync):
                    Calls.Add((name, args));
                    ActiveMemories.RemoveAll(item => item.Id == (string)args[0]!);
                    return Task.CompletedTask;
                case nameof(ICoachApiClient.ForgetAllMemoriesAsync):
                {
                    Calls.Add((name, args));
                    var count = ActiveMemories.Count;
                    ActiveMemories.Clear();
                    return Task.FromResult<Contracts.LearnerMemory.CoachMemoryForgetAllResponse?>(new(count));
                }
                case nameof(ICoachApiClient.DeleteSessionAsync):
                    Calls.Add((name, args));
                    Conversations.RemoveAll(item => item.ConversationId == (string)args[0]!);
                    return Task.CompletedTask;
                case nameof(ICoachApiClient.DeleteConversationAsync):
                    Calls.Add((name, args));
                    Conversations.RemoveAll(item => item.ConversationId == (string)args[0]!);
                    return Task.CompletedTask;
                case nameof(ICoachApiClient.UpdateConversationAsync):
                {
                    Calls.Add((name, args));
                    var conversationKey = (string)args[0]!;
                    var request = (UpdateCoachConversationRequest)args[1]!;
                    var index = Conversations.FindIndex(item => item.ConversationId == conversationKey);
                    var existing = Conversations[index];
                    var updated = new CoachConversationDto
                    {
                        ConversationId = conversationKey, Title = request.Title ?? existing.Title,
                        TitleOrigin = request.Title is null ? existing.TitleOrigin : CoachConversationTitleOrigin.Learner,
                        CreatedAtUtc = existing.CreatedAtUtc, UpdatedAtUtc = DateTime.UtcNow,
                        HistoryStartsAtUtc = existing.HistoryStartsAtUtc, MessageCount = existing.MessageCount,
                        StateVersion = existing.StateVersion + 1, HasActiveCheckpoint = existing.HasActiveCheckpoint,
                        IsClosed = request.Close ?? existing.IsClosed
                    };
                    Conversations[index] = updated;
                    return Task.FromResult(updated);
                }
                default:
                    throw new NotSupportedException(name);
            }
        }

        public List<(string Name, object?[] Args)> Calls { get; } = [];
        public Dictionary<string, CoachWriteOperationDto> Writes { get; } = new();
        public bool ReportsEnabled { get; set; } = true;
        public List<string> Reported { get; } = [];
        public bool MemoryEnabled { get; set; } = true;
        public List<Contracts.LearnerMemory.CoachMemoryFactDto> ActiveMemories { get; } = [];
        public List<Contracts.LearnerMemory.CoachMemoryFactDto> CandidateMemories { get; } = [];
        public int CallCount(string name) => Calls.Count(call => call.Name == name);

        private static CoachWriteOperationDto Settle(CoachWriteOperationDto current, CoachWriteStatus status) => new()
        {
            OperationId = current.OperationId, ConversationId = current.ConversationId,
            MessageId = current.MessageId, ChangeKind = current.ChangeKind, RiskClass = current.RiskClass,
            Status = status, ApprovalMode = current.ApprovalMode, Summary = current.Summary,
            Lines = current.Lines, ExpiresAtUtc = current.ExpiresAtUtc,
            RequiresConfirmation = current.RequiresConfirmation, IsReversible = current.IsReversible,
            Receipt = status == CoachWriteStatus.Executed ? new CoachWriteReceiptDto
            {
                OperationId = current.OperationId, ChangeKind = current.ChangeKind, RiskClass = current.RiskClass,
                Status = status, Summary = "Added 사과", ExecutedAtUtc = DateTime.UtcNow,
                CanUndo = current.IsReversible, UndoExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
            } : current.Receipt
        };

        public HashSet<string> Gone { get; } = [];

        public class Api : DispatchProxy
        {
            internal FakeSam Owner = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                Owner.Handle(targetMethod!.Name, args ?? []);
        }
    }
}
