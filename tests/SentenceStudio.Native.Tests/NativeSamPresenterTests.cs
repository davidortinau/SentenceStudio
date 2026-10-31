using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Contracts.Coach;
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

    // ------------------------------------------------------------------ helpers

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
        CoachVocabularySetProposal? vocabularySet = null) => new()
    {
        SessionId = "session-1", TurnId = Guid.NewGuid().ToString("N"), Status = status, StopReason = stop,
        SessionStatus = CoachSessionStatus.Active, ActiveConstraints = Constraints(), PlanState = Plan(),
        ChangeReceipt = receipt, PendingSuggestion = suggestion, Limitation = limitation,
        Evidence = evidence ?? [], ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        VocabularySetProposal = vocabularySet
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

        private object? Handle(string name, object?[] args)
        {
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
                    var answer = History("a" + _sequence, CoachMessageRole.Coach, "answer", ++_sequence);
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
                default:
                    throw new NotSupportedException(name);
            }
        }

        public HashSet<string> Gone { get; } = [];

        public class Api : DispatchProxy
        {
            internal FakeSam Owner = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                Owner.Handle(targetMethod!.Name, args ?? []);
        }
    }
}
