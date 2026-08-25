using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using Xunit;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression tests for the defects found in Phase 2 verification.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>a turn's structured answer landed inside an earlier bubble instead of on the message
/// this turn produced;</item>
/// <item>a refusal read as though the change had been applied, and the authoritative
/// "no change applied" marker only existed in durable mode;</item>
/// <item>a turn that said nothing about an open suggestion was read as withdrawing it, and a
/// stale suggestion was cleared field by field instead of re-read from the server.</item>
/// </list>
/// <para>
/// Every assertion reads an authoritative field - the operation's status, stop reason and
/// receipt, the ledger's reason code, the session read-back. None reads Sam's prose.
/// </para>
/// </remarks>
public sealed class CoachPhase2DefectRegressionTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        return (new CoachWorkspaceState(client, new CoachConversationDirectory(client)), client);
    }

    private static (CoachWorkspaceState State, FakeCoachApiClient Client) CreateSessionOnly()
    {
        var client = new FakeCoachApiClient();
        return (new CoachWorkspaceState(client), client);
    }

    // ================================================================ defect 1
    // The structured answer belongs to the message this turn produced, in this turn.

    /// <summary>
    /// Drives a real durable turn - submit, settle, ledger merge, apply - with both a notice and a
    /// pedagogical answer resident, and an older unanswered message still on screen.
    /// </summary>
    /// <remarks>
    /// This is the shape the server produces when it answers a question it will not act on:
    /// the answer, then the notice. The ledger rows deliberately carry no structured answer, so
    /// the only way it can reach the screen is the pairing running for real.
    /// </remarks>
    [Fact]
    public async Task StructuredAnswerLandsOnThisTurnsAnswer_NotOnANoticeAndNotOnAnEarlierTurn()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");

        // Earlier turns, already on screen when the learner asks again.
        client.ConversationMessages["c-1"].Add(History(
            Message("m-ask-old", CoachMessageKind.Text, "Why?", CoachMessageRole.Learner), 1));
        client.ConversationMessages["c-1"].Add(History(
            Message("m-notice-old", CoachMessageKind.Notice, "Today's plan is unchanged."), 2, reasonCode: "MetadataUnavailable"));

        // Left unanswered on purpose: an unbounded backward scan lands this turn's answer here.
        client.ConversationMessages["c-1"].Add(History(
            Message("m-answer-old", CoachMessageKind.PedagogicalAnswer, "An older explanation."), 3));

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        Entry(state, "m-answer-old").Should().NotBeNull("the earlier turns are on screen before this one runs");
        Entry(state, "m-answer-old")!.Answer.Should().BeNull("the earlier answer arrived without structured blocks");

        var answer = StructuredAnswer();

        client.OnSubmitConversationTurn = (_, request) => new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = "c-1",
            State = CoachTurnOperationState.Completed,

            // The response body carries the structured answer; the ledger rows do not. That split
            // is the real one - a ledger row does not always bring an answer with it.
            Result = CoachStateMachineTests.Turn(
                status: CoachTurnStatus.Rejected,
                stopReason: CoachStopReason.ValidationFailed,
                answer: answer),
            Messages = new[]
            {
                History(Message("m-ask-new", CoachMessageKind.Text, "Explain this", CoachMessageRole.Learner), 4),
                History(Message("m-answer-new", CoachMessageKind.PedagogicalAnswer, "Here is the explanation."), 5),

                // The notice is the newest coach row, so the search has to walk past it.
                History(Message("m-notice-new", CoachMessageKind.Notice, "I did not change your plan."), 6, reasonCode: "MetadataUnavailable")
            },
            FirstResponseSequence = 4,
            LastResponseSequence = 6,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        state.Draft = "Explain this";
        await state.SendDraftAsync();

        Entry(state, "m-answer-new").Should().NotBeNull("the turn's canonical answer row is on screen");
        Entry(state, "m-answer-new")!.Answer.Should().BeSameAs(answer,
            "the structured answer belongs to the pedagogical message this turn produced");

        Entry(state, "m-notice-new")!.Answer.Should().BeNull(
            "a notice is the server saying it did not act, never the answer to what was asked");

        Entry(state, "m-answer-old")!.Answer.Should().BeNull(
            "the search stops at this turn's boundary and never backfills an earlier exchange");
        Entry(state, "m-notice-old")!.Answer.Should().BeNull();

        state.LatestAnswer.Should().BeSameAs(answer);
    }

    /// <summary>A turn that produced only a refusal leaves every earlier message as it was.</summary>
    [Fact]
    public async Task RefusalOnlyTurn_DoesNotBackfillAnEarlierUnansweredMessage()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");

        client.ConversationMessages["c-1"].Add(History(
            Message("m-ask-old", CoachMessageKind.Text, "Why?", CoachMessageRole.Learner), 1));
        client.ConversationMessages["c-1"].Add(History(
            Message("m-answer-old", CoachMessageKind.PedagogicalAnswer, "An older explanation."), 2));

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        client.OnSubmitConversationTurn = (_, request) => new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = "c-1",
            State = CoachTurnOperationState.Completed,
            Result = CoachStateMachineTests.Turn(
                status: CoachTurnStatus.Rejected,
                stopReason: CoachStopReason.InputRejected,
                answer: StructuredAnswer()),
            Messages = new[]
            {
                History(Message("m-ask-new", CoachMessageKind.Text, "Change my plan", CoachMessageRole.Learner), 3),
                History(Message("m-notice-new", CoachMessageKind.Notice, "I did not change your plan."), 4, reasonCode: "MetadataUnavailable")
            },
            FirstResponseSequence = 3,
            LastResponseSequence = 4,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        state.Draft = "Change my plan";
        await state.SendDraftAsync();

        Entry(state, "m-answer-old")!.Answer.Should().BeNull(
            "this turn produced no message that can carry an answer, so nothing older may absorb it");
        Entry(state, "m-notice-new")!.Answer.Should().BeNull();
    }

    /// <summary>The same rule in session-only mode, where the response body is the whole turn.</summary>
    [Fact]
    public async Task PairAnswerSkipsNoticeMessages()
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        var answer = StructuredAnswer();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages: new[]
            {
                Message("m-n1", CoachMessageKind.Notice, "Plan unchanged."),
                Message("m-a1", CoachMessageKind.PedagogicalAnswer, "Explanation text.")
            },
            answer: answer);

        state.Draft = "explain something";
        await state.SendDraftAsync();

        Entry(state, "m-n1")!.Answer.Should().BeNull("notices never receive structured answers");
        Entry(state, "m-a1")!.Answer.Should().BeSameAs(answer);
    }

    // ================================================================ defect 2
    // "No change applied" is an operation fact, and it has to render in both modes.

    /// <summary>
    /// The durable code is read through the closed vocabulary, not tested for presence. Every
    /// notice the server writes carries a code, so "has a code" would mark informational notices
    /// and receipt-bearing turns as refusals.
    /// </summary>
    [Theory]
    [InlineData(CoachNoticeReasonCodes.ValidationFailed, true)]
    [InlineData(CoachNoticeReasonCodes.InputRejected, true)]
    [InlineData(CoachNoticeReasonCodes.ToolFailure, true)]
    [InlineData(CoachNoticeReasonCodes.Timeout, true)]
    [InlineData(CoachNoticeReasonCodes.Default, false)]
    public async Task DurableNotice_MarksOnlyForARefusalCode(string reasonCode, bool expectsMarker)
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        client.ConversationMessages["c-1"].Add(History(
            Message("m-refusal", CoachMessageKind.Notice, "I changed the vocabulary focus to reading."),
            3,
            reasonCode: reasonCode));

        await state.LoadTranscriptAsync();

        var entry = Entry(state, "m-refusal")!;
        entry.NoticeReasonCode.Should().Be(reasonCode, "the durable row carries the server's own code");
        entry.ShowsNoChangeMarker.Should().Be(expectsMarker,
            "only the closed refusal set marks; an informational notice carries a code too");
    }

    /// <summary>
    /// A code this client has never heard of is not treated as a refusal. A newer server inventing
    /// a code must leave an older client silent rather than asserting something about plan data it
    /// cannot interpret.
    /// </summary>
    [Fact]
    public async Task DurableNotice_WithAnUnknownCode_DoesNotClaimNoChange()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        client.ConversationMessages["c-1"].Add(History(
            Message("m-future", CoachMessageKind.Notice, "Something new happened."),
            3,
            reasonCode: "quota_paused_for_maintenance"));

        await state.LoadTranscriptAsync();

        Entry(state, "m-future")!.ShowsNoChangeMarker.Should().BeFalse();
    }

    [Fact]
    public async Task RefusalTurn_HasNoReceipt_TimelineNoticeStillDistinguishable()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var refusal = Message("m-refused", CoachMessageKind.Notice, "I changed only the vocabulary focus.");

        client.OnSubmitConversationTurn = (_, request) => new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = "c-1",
            State = CoachTurnOperationState.Completed,
            Result = CoachStateMachineTests.Turn(
                status: CoachTurnStatus.Rejected,
                stopReason: CoachStopReason.ValidationFailed,
                messages: new[] { refusal }),
            Messages = new[] { History(refusal, 5, reasonCode: CoachNoticeReasonCodes.ValidationFailed) },
            FirstResponseSequence = 5,
            LastResponseSequence = 5,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        state.Draft = "focus on listening";
        await state.SendDraftAsync();

        state.Receipts.Should().BeEmpty("a refused turn produces no receipt");
        Entry(state, "m-refused")!.NoticeReasonCode.Should().Be(CoachNoticeReasonCodes.ValidationFailed);
    }

    /// <summary>
    /// A session-only notice is stamped with the same code the server would have written, so the
    /// marker is a read of one field in both modes rather than two parallel derivations.
    /// </summary>
    /// <remarks>
    /// Status is varied independently of stop reason, including the Completed rows, because the
    /// stop reason is what names the outcome. A turn can report Completed and still have stopped on
    /// a failed check; that is a refusal and has to mark.
    /// </remarks>
    [Theory]
    [InlineData(CoachTurnStatus.Rejected, CoachStopReason.InputRejected, CoachNoticeReasonCodes.InputRejected)]
    [InlineData(CoachTurnStatus.Rejected, CoachStopReason.ValidationFailed, CoachNoticeReasonCodes.ValidationFailed)]
    [InlineData(CoachTurnStatus.Incomplete, CoachStopReason.ValidationFailed, CoachNoticeReasonCodes.ValidationFailed)]
    [InlineData(CoachTurnStatus.Failed, CoachStopReason.ToolFailure, CoachNoticeReasonCodes.ToolFailure)]
    [InlineData(CoachTurnStatus.Completed, CoachStopReason.ValidationFailed, CoachNoticeReasonCodes.ValidationFailed)]
    [InlineData(CoachTurnStatus.Completed, CoachStopReason.ToolFailure, CoachNoticeReasonCodes.ToolFailure)]
    [InlineData(CoachTurnStatus.Completed, CoachStopReason.RateLimit, CoachNoticeReasonCodes.RateLimited)]
    [InlineData(CoachTurnStatus.Incomplete, CoachStopReason.Timeout, CoachNoticeReasonCodes.Timeout)]
    public async Task SessionOnlyRefusal_StampsTheSameCodeTheServerWouldHave(
        CoachTurnStatus status,
        CoachStopReason stopReason,
        string expectedCode)
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            status: status,
            stopReason: stopReason,
            messages: new[] { Message("m-refused", CoachMessageKind.Notice, "I switched your plan to listening.") });

        state.Draft = "focus on listening";
        await state.SendDraftAsync();

        var entry = Entry(state, "m-refused")!;
        entry.NoticeReasonCode.Should().Be(expectedCode,
            "the session-only path stamps the identical code the ledger would carry");
        entry.IsNoChangeNotice.Should().BeTrue("the turn's own stop reason and missing receipt say so");
        entry.ShowsNoChangeMarker.Should().BeTrue("the badge has to render in session-only mode too");
    }

    /// <summary>
    /// Status alone never marks. A turn that did not complete but stopped to ask a question has
    /// refused nothing, and the durable row for that same outcome would carry the informational
    /// code — so claiming "no change applied" here would break parity as well as being wrong.
    /// </summary>
    [Theory]
    [InlineData(CoachTurnStatus.Completed, CoachStopReason.Completed)]
    [InlineData(CoachTurnStatus.Completed, CoachStopReason.ClarificationRequested)]
    [InlineData(CoachTurnStatus.Incomplete, CoachStopReason.ClarificationRequested)]
    public async Task SessionOnlyTurnThatRefusedNothing_DoesNotClaimNoChange(
        CoachTurnStatus status,
        CoachStopReason stopReason)
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            status: status,
            stopReason: stopReason,
            messages: new[] { Message("m-info", CoachMessageKind.Notice, "You have two clarifications left.") });

        state.Draft = "how many questions can you ask?";
        await state.SendDraftAsync();

        var entry = Entry(state, "m-info")!;
        entry.NoticeReasonCode.Should().Be(CoachNoticeReasonCodes.Default,
            "every notice carries a code; this one says the notice is informational");
        entry.IsNoChangeNotice.Should().BeFalse("nothing here refused anything");
        entry.ShowsNoChangeMarker.Should().BeFalse();
    }

    [Fact]
    public async Task SessionOnlyRefusalThatStillWrote_DoesNotClaimNoChange()
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            status: CoachTurnStatus.Incomplete,
            stopReason: CoachStopReason.ValidationFailed,
            messages: new[] { Message("m-partial", CoachMessageKind.Notice, "Part of that was out of scope.") },
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));

        state.Draft = "shorten today and also do something impossible";
        await state.SendDraftAsync();

        var partial = Entry(state, "m-partial")!;
        partial.NoticeReasonCode.Should().Be(CoachNoticeReasonCodes.Default,
            "a turn that wrote is not a refusal, whatever the stop reason says");
        partial.ShowsNoChangeMarker.Should().BeFalse(
            "a receipt is the server saying it did write, which outranks the stop reason");
    }

    // ================================================================ defect 3
    // A turn that says nothing about the suggestion must not be read as withdrawing it.

    /// <summary>
    /// The server is explicit that refusing a change never withdraws an offer the learner has not
    /// answered, and an operation that settles with no result body carries no suggestion state at
    /// all. Clearing the card on that silence is inventing state.
    /// </summary>
    [Fact]
    public async Task NullResultTurn_PreservesPendingSuggestionTheServerStillHolds()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var suggestion = CoachStateMachineTests.Suggestion("sug-still-open");
        OfferSuggestion(client, suggestion);

        state.Draft = "suggest something";
        await state.SendDraftAsync();

        state.PendingSuggestion.Should().NotBeNull();
        var anchoredTurn = state.PendingSuggestionTurn;
        anchoredTurn.Should().NotBeNull();

        // The server still holds the offer open.
        client.OnGetSession = sessionId => FakeCoachApiClient.Session(
            sessionId,
            status: CoachSessionStatus.SuggestionPending,
            suggestion: suggestion);

        SettleWithNoResult(client, "m-reply", 10);

        state.Draft = "what does this word mean?";
        await state.SendDraftAsync();

        state.PendingSuggestion.Should().NotBeNull(
            "the server preserved the open offer, and a result of null says nothing to the contrary");
        state.PendingSuggestion!.SuggestionId.Should().Be("sug-still-open");
        state.PendingSuggestionTurn.Should().Be(anchoredTurn, "the card stays anchored to the turn that offered it");
        state.State.Should().Be(CoachUiState.SuggestionPending);
    }

    [Fact]
    public async Task NullResultTurn_ClearsSuggestionOnlyWhenTheServerSaysItIsGone()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        OfferSuggestion(client, CoachStateMachineTests.Suggestion("sug-answered-elsewhere"));

        state.Draft = "suggest something";
        await state.SendDraftAsync();
        state.PendingSuggestion.Should().NotBeNull();

        // The authoritative read says the offer is gone.
        client.OnGetSession = sessionId => FakeCoachApiClient.Session(sessionId);

        SettleWithNoResult(client, "m-reply-2", 11);

        state.Draft = "do something else";
        await state.SendDraftAsync();

        state.PendingSuggestion.Should().BeNull("the session read is what withdrew the offer, not a guess");
        state.PendingSuggestionTurn.Should().BeNull();
        state.State.Should().Be(CoachUiState.Ready);
    }

    // ================================================================ defect 4
    // SuggestionNotFound means the whole picture is stale, not just two fields.

    [Fact]
    public async Task SuggestionNotFound_RefreshesAuthoritativeStateInPlace()
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            suggestion: CoachStateMachineTests.Suggestion("sug-stale"),
            sessionStatus: CoachSessionStatus.SuggestionPending,
            messages: new[] { Message("m-offer", CoachMessageKind.Text, "Want to add speaking?") });

        state.Draft = "suggest a change";
        await state.SendDraftAsync();

        state.PendingSuggestion!.SuggestionId.Should().Be("sug-stale");
        state.PlanState!.PlanVersion.Should().Be("v1");
        state.Revisions.Should().BeEmpty();

        // While the card sat on screen, the plan moved on somewhere else.
        client.OnGetSession = _ => MovedOnSession();
        client.OnAccept = () => throw NotFound();

        await state.AcceptSuggestionAsync();

        state.PendingSuggestion.Should().BeNull("the authoritative read no longer holds the offer");
        state.PendingSuggestionTurn.Should().BeNull();

        state.PlanState!.PlanVersion.Should().Be("v9", "the plan the card was written against has moved on");
        state.ActiveConstraints!.AvailableMinutes.Should().Be(45);
        state.Revisions.Should().ContainSingle(r => r.RevisionId == "rev-refresh",
            "revisions are server-owned and come back in full");
        state.ClarificationsRemaining.Should().Be(0);

        state.Timeline.Should().NotBeEmpty("the refresh happens in place - the learner keeps what they can see");
        state.State.Should().Be(CoachUiState.Ready, "the workspace stays usable");
    }

    [Fact]
    public async Task SuggestionNotFound_OnReject_AlsoRefreshesAuthoritativeState()
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            suggestion: CoachStateMachineTests.Suggestion("sug-reject-stale"),
            sessionStatus: CoachSessionStatus.SuggestionPending);

        state.Draft = "suggest";
        await state.SendDraftAsync();
        state.PendingSuggestion.Should().NotBeNull();

        client.OnGetSession = _ => MovedOnSession();
        client.OnReject = () => throw NotFound();

        await state.RejectSuggestionAsync();

        state.PendingSuggestion.Should().BeNull();
        state.PlanState!.PlanVersion.Should().Be("v9");
        state.Revisions.Should().ContainSingle(r => r.RevisionId == "rev-refresh");
        state.State.Should().Be(CoachUiState.Ready);
    }

    /// <summary>
    /// A refresh that cannot reach the server leaves the last known state alone rather than
    /// replacing it with an invented one.
    /// </summary>
    [Fact]
    public async Task SuggestionNotFound_WhenTheRefreshFails_LeavesTheWorkspaceUsable()
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            suggestion: CoachStateMachineTests.Suggestion("sug-stale"),
            sessionStatus: CoachSessionStatus.SuggestionPending);

        state.Draft = "suggest";
        await state.SendDraftAsync();

        client.OnGetSession = _ => throw new HttpRequestException("offline");
        client.OnAccept = () => throw NotFound();

        await state.AcceptSuggestionAsync();

        state.PlanState!.PlanVersion.Should().Be("v1", "nothing was learned, so nothing is changed");
        state.State.Should().Be(CoachUiState.Ready);
    }

    /// <summary>
    /// The durable path has a second authoritative source. A stale suggestion there has to
    /// refresh both: the session read for the plan, the ledger read for the transcript.
    /// </summary>
    [Fact]
    public async Task DurableSuggestionNotFound_RefreshesTheSessionAndTheLedger()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        client.ConversationMessages["c-1"].Add(History(
            Message("m-ask", CoachMessageKind.Text, "Shorten today", CoachMessageRole.Learner), 1));

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        OfferSuggestion(client, CoachStateMachineTests.Suggestion("sug-durable-stale"));
        state.Draft = "shorten today";
        await state.SendDraftAsync();

        state.PendingSuggestion!.SuggestionId.Should().Be("sug-durable-stale");
        state.PlanState!.PlanVersion.Should().Be("v1");

        // Somewhere else, the plan moved on and the thread grew a row explaining it.
        client.ConversationMessages["c-1"].Add(History(
            Message("m-elsewhere", CoachMessageKind.Text, "Shortened today from another device."), 20));
        client.OnGetSession = _ => MovedOnSession();
        client.OnAccept = () => throw NotFound();

        await state.AcceptSuggestionAsync();

        state.PendingSuggestion.Should().BeNull("the session read no longer holds the offer");
        state.PlanState!.PlanVersion.Should().Be("v9", "the session read is what supplies the plan");
        state.Revisions.Should().ContainSingle(r => r.RevisionId == "rev-refresh");

        Entry(state, "m-elsewhere").Should().NotBeNull(
            "the ledger read is what supplies the transcript, and durable mode does both");
        Entry(state, "m-ask").Should().NotBeNull("reconciling is not a reset");
        state.State.Should().Be(CoachUiState.Ready, "the workspace stays usable");
    }

    // ================================================================ denied read-back
    // A read that comes back "not yours" must not leave the denied session on screen.

    [Theory]
    [InlineData(CoachProblemTypes.SessionNotFound, CoachUiState.Expired)]
    [InlineData(CoachProblemTypes.SessionExpired, CoachUiState.Expired)]
    [InlineData(CoachProblemTypes.Unavailable, CoachUiState.SessionDeleted)]
    public async Task ARefreshTheServerDenies_DropsTheStaleViewInsteadOfKeepingIt(
        string problemType,
        CoachUiState expected)
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            suggestion: CoachStateMachineTests.Suggestion("sug-denied"),
            sessionStatus: CoachSessionStatus.SuggestionPending,
            messages: new[] { Message("m-offer", CoachMessageKind.Text, "Want to add speaking?") });

        state.Draft = "suggest a change";
        await state.SendDraftAsync();

        state.PendingSuggestion.Should().NotBeNull();
        state.PlanState.Should().NotBeNull();
        state.Timeline.Should().NotBeEmpty();

        client.OnGetSession = _ => throw new CoachApiException(
            HttpStatusCode.NotFound, problemType, null, null);
        client.OnAccept = () => throw NotFound();

        await state.AcceptSuggestionAsync();

        state.State.Should().Be(expected,
            "the denial names the outcome, and 'carry on' would overwrite it with a friendlier lie");
        state.PendingSuggestion.Should().BeNull("the card describes a session the server just denied");
        state.PlanState.Should().BeNull();
        state.ActiveConstraints.Should().BeNull();
        state.SessionId.Should().BeNull();
        state.Timeline.Should().BeEmpty("the transcript belonged to the denied session too");
        state.Receipts.Should().BeEmpty();
    }

    /// <summary>
    /// The same denial on the settled-with-no-result path, which has no exception of its own and
    /// would otherwise report a clean Ready over a session that is gone.
    /// </summary>
    [Fact]
    public async Task ASettleWithNoResult_WhoseReadBackIsDenied_DoesNotReportReady()
    {
        var (state, client) = Create();
        client.AddConversation("c-1");
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        SettleWithNoResult(client, "m-reply", 5);
        client.OnGetSession = _ => throw new CoachApiException(
            HttpStatusCode.NotFound, CoachProblemTypes.SessionNotFound, null, null);

        state.Draft = "do something";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Expired);
        state.PendingSuggestion.Should().BeNull();
        state.SessionId.Should().BeNull();
    }

    /// <summary>
    /// A denial is not a sign of a bad learner, only a bad session id: the workspace stays open so
    /// the expiry notice has somewhere to render, and nothing here reaches for authentication.
    /// </summary>
    [Fact]
    public async Task ADeniedRefresh_LeavesTheWorkspaceOpenAndDoesNotSignTheLearnerOut()
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            suggestion: CoachStateMachineTests.Suggestion("sug-open"),
            sessionStatus: CoachSessionStatus.SuggestionPending);

        state.Draft = "suggest";
        await state.SendDraftAsync();

        client.OnGetSession = _ => throw new CoachApiException(
            HttpStatusCode.NotFound, CoachProblemTypes.SessionNotFound, null, null);
        client.OnAccept = () => throw NotFound();

        await state.AcceptSuggestionAsync();

        state.IsOpen.Should().BeTrue("the learner is still here - it is the session that is gone");
        state.State.Should().Be(CoachUiState.Expired);
    }

    /// <summary>
    /// A failure that says nothing about ownership still leaves the last known state alone. Only
    /// the denials clear, and only because they are the ones that make the view someone else's.
    /// </summary>
    [Fact]
    public async Task ARefreshThatFailsForAnUnrelatedReason_StillLeavesTheViewAlone()
    {
        var (state, client) = CreateSessionOnly();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            suggestion: CoachStateMachineTests.Suggestion("sug-keep"),
            sessionStatus: CoachSessionStatus.SuggestionPending);

        state.Draft = "suggest";
        await state.SendDraftAsync();

        client.OnGetSession = _ => throw new CoachApiException(
            HttpStatusCode.ServiceUnavailable, CoachProblemTypes.ToolFailure, null, null);
        client.OnAccept = () => throw NotFound();

        await state.AcceptSuggestionAsync();

        state.PlanState!.PlanVersion.Should().Be("v1", "nothing was learned, so nothing is changed");
        state.SessionId.Should().NotBeNull();
        state.State.Should().Be(CoachUiState.Ready);
    }

    // ---------------------------------------------------------------- builders

    private static CoachTimelineEntry? Entry(CoachWorkspaceState state, string messageId) =>
        state.Timeline.FirstOrDefault(e =>
            string.Equals(e.Message?.MessageId, messageId, StringComparison.Ordinal));

    private static void OfferSuggestion(FakeCoachApiClient client, PendingCoachSuggestionDto suggestion)
    {
        client.OnSubmitConversationTurn = (_, request) => new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = "c-1",
            State = CoachTurnOperationState.Completed,
            Result = CoachStateMachineTests.Turn(
                suggestion: suggestion,
                sessionStatus: CoachSessionStatus.SuggestionPending),
            Messages = Array.Empty<CoachHistoryMessageDto>(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>The settle shape at the heart of defect 3: completed, rows written, no result body.</summary>
    private static void SettleWithNoResult(FakeCoachApiClient client, string messageId, long sequence)
    {
        client.OnSubmitConversationTurn = (_, request) => new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = "c-1",
            State = CoachTurnOperationState.Completed,
            Result = null,
            Messages = new[] { History(Message(messageId, CoachMessageKind.Text, "Here is what I can tell you."), sequence) },
            FirstResponseSequence = sequence,
            LastResponseSequence = sequence,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private static CoachApiException NotFound() =>
        new(HttpStatusCode.NotFound, CoachProblemTypes.SuggestionNotFound, null, null);

    private static CoachMessageDto Message(
        string messageId,
        CoachMessageKind kind,
        string text,
        CoachMessageRole role = CoachMessageRole.Coach) => new()
        {
            MessageId = messageId,
            Role = role,
            Kind = kind,
            Text = text,
            CreatedAtUtc = DateTime.UtcNow
        };

    private static CoachHistoryMessageDto History(
        CoachMessageDto message,
        long sequence,
        string? reasonCode = null) => new()
        {
            Message = message,
            Sequence = sequence,
            IsReadable = true,
            NoticeReasonCode = reasonCode
        };

    private static CoachAnswerDto StructuredAnswer() => new()
    {
        Topic = CoachAnswerTopic.Grammar,
        PlainText = "Here is the structured explanation.",
        TargetLanguageTag = "ko",
        DisplayLanguageTag = "en",
        Blocks =
        [
            CoachAnswerStateTests.Block(CoachAnswerBlockKind.Answer,
                new CoachAnswerSpanDto { Text = "구조", Language = CoachLanguageRole.Target, LanguageTag = "ko" })
        ]
    };

    /// <summary>The session as the server holds it after the plan moved on without this client.</summary>
    private static CoachSessionResponse MovedOnSession() => new()
    {
        SessionId = "session-1",
        Status = CoachSessionStatus.Active,
        Messages = Array.Empty<CoachMessageDto>(),
        Evidence = Array.Empty<CoachEvidenceDto>(),
        Revisions = new[]
        {
            new CoachRevisionDto
            {
                RevisionId = "rev-refresh",
                RevisionNumber = 4,
                Source = CoachRevisionSource.DirectRequest,
                Summary = "Shortened today",
                BeforePlanVersion = "v1",
                AfterPlanVersion = "v9",
                CreatedAtUtc = DateTime.UtcNow,
                CanUndo = true
            }
        },
        ActiveConstraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 45,
            AudioAllowed = true,
            SpeechAllowed = true,
            TypingAllowed = true,
            EnergyLevel = CoachEnergyLevel.Normal
        },
        PlanState = CoachStateMachineTests.PlanState("v9"),
        PendingSuggestion = null,
        ClarificationsRemaining = 0,
        CreatedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
    };
}
