using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// What the session row says about how a turn ended.
/// </summary>
/// <remarks>
/// <para>
/// Regression cover for a live persistence defect. A session that had once hit the output-token
/// limit kept <c>StopReason = ValidationFailed</c> forever: the successful suggestion that
/// followed never cleared it, and the ambiguous "Maybe." after that never wrote its own. The row
/// read <c>Status = SuggestionPending</c> with a stale failure attached, while the response and
/// the UI correctly showed a focused clarification and an open offer.
/// </para>
/// <para>
/// Stored state now answers two independent questions honestly: what the session is waiting for
/// (<c>Status</c>), and why the last turn ended (<c>StopReason</c>, cleared by any turn that
/// succeeds). An open offer is a third, separate fact carried by <c>PendingSuggestionId</c>.
/// </para>
/// </remarks>
public class CoachSessionStateTransitionTests
{
    // ---------------------------------------------------------------- the reported sequence

    [Fact]
    public async Task OutputLimit_ThenSuggestion_ThenMaybe_LeavesAnHonestRow()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // 1. A turn that runs out of output tokens.
        harness.Coach.NextResult = await OutputLimitResultAsync();
        var capped = await SubmitAsync(harness, sessionId, "Could you suggest one useful change?");

        capped.Value!.StopReason.Should().Be(CoachStopReason.OutputTokenLimit);
        Row(harness).StopReason.Should().Be(CoachStopReason.OutputTokenLimit);

        // 2. A successful suggestion. The earlier failure must not survive it.
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        var afterSuggestion = Row(harness);
        afterSuggestion.StopReason.Should().BeNull("a turn that succeeded clears what an earlier one left");
        afterSuggestion.Status.Should().Be(CoachSessionStatus.SuggestionPending);
        afterSuggestion.PendingSuggestionId.Should().Be(suggestion.SuggestionId);

        // 3. "Maybe." — ambiguous, so the coach asks and nothing is written.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "Should I update Today\u2019s Plan with that change now?",
            CoachMessage = "I need one detail."
        });

        var maybe = await SubmitAsync(harness, sessionId, "Maybe.", suggestion.SuggestionId);

        // The response the UI already got right.
        maybe.Value!.Status.Should().Be(CoachTurnStatus.Incomplete);
        maybe.Value.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        maybe.Value.SessionStatus.Should().Be(CoachSessionStatus.AwaitingClarification);
        maybe.Value.ClarifyingQuestion.Should().NotBeNullOrEmpty();
        maybe.Value.PendingSuggestion!.SuggestionId.Should().Be(suggestion.SuggestionId);

        // And now the row agrees with it.
        var afterMaybe = Row(harness);
        afterMaybe.Status.Should().Be(CoachSessionStatus.AwaitingClarification);
        afterMaybe.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        afterMaybe.ClarificationCount.Should().Be(1);
        afterMaybe.PendingSuggestionId.Should().Be(suggestion.SuggestionId);
        afterMaybe.PendingSuggestionDeltaJson.Should().NotBeNullOrEmpty();

        harness.Db.CoachPlanRevisions.Should().BeEmpty("nothing in this sequence changes the plan");
    }

    [Fact]
    public async Task AfterMaybe_AReloadStillShowsTheClarificationAndTheOffer()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "Should I update Today\u2019s Plan with that change now?",
            CoachMessage = "I need one detail."
        });
        await SubmitAsync(harness, sessionId, "Maybe.", suggestion.SuggestionId);

        var reloaded = (await harness.Service.GetSessionAsync(sessionId)).Value!;

        reloaded.Status.Should().Be(CoachSessionStatus.AwaitingClarification);
        reloaded.PendingSuggestion!.SuggestionId.Should().Be(suggestion.SuggestionId);
        reloaded.PendingSuggestion.Delta.ChangedFields.Should().NotBeEmpty();
        reloaded.ClarificationsRemaining.Should().Be(1);
    }

    [Fact]
    public async Task AfterMaybe_ThePendingOfferCanStillBeAccepted()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "Should I update Today\u2019s Plan now?",
            CoachMessage = "I need one detail."
        });
        await SubmitAsync(harness, sessionId, "Maybe.", suggestion.SuggestionId);

        // The exact follow-up from the live session: a clear yes after the clarification.
        var yes = await SubmitAsync(harness, sessionId, "Yes, update it", suggestion.SuggestionId);

        yes.Value!.ChangeReceipt.Should().NotBeNull();

        var row = Row(harness);
        row.Status.Should().Be(CoachSessionStatus.Active);
        row.StopReason.Should().BeNull();
        row.PendingSuggestionId.Should().BeNull();
    }

    // ---------------------------------------------------------------- clearing, per path

    [Fact]
    public async Task AnAppliedDirectChange_ClearsAnEarlierFailure()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = await OutputLimitResultAsync();
        await SubmitAsync(harness, sessionId, "Suggest a change to Today's Plan.");
        Row(harness).StopReason.Should().Be(CoachStopReason.OutputTokenLimit);

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10 },
            CoachMessage = "Done."
        });
        await SubmitAsync(harness, sessionId, "make Today's Plan 10 minutes");

        var row = Row(harness);
        row.StopReason.Should().BeNull();
        row.Status.Should().Be(CoachSessionStatus.Active);
    }

    [Fact]
    public async Task ARejectedOffer_ClearsAnEarlierFailureAndReturnsToActive()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        // Ambiguity first, so the row is on AwaitingClarification when the offer is declined.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "Should I update Today\u2019s Plan now?",
            CoachMessage = "I need one detail."
        });
        await SubmitAsync(harness, sessionId, "Maybe.", suggestion.SuggestionId);
        Row(harness).Status.Should().Be(CoachSessionStatus.AwaitingClarification);

        await harness.Service.RejectSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        var row = Row(harness);
        row.Status.Should().Be(CoachSessionStatus.Active, "declining an offer leaves nothing outstanding");
        row.StopReason.Should().BeNull();
        row.PendingSuggestionId.Should().BeNull();
    }

    [Fact]
    public async Task AnUndo_ClearsAnEarlierFailure()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10 },
            CoachMessage = "Done."
        });
        await SubmitAsync(harness, sessionId, "make Today's Plan 10 minutes");

        harness.Coach.NextResult = await OutputLimitResultAsync();
        await SubmitAsync(harness, sessionId, "Suggest a change to Today's Plan.");
        Row(harness).StopReason.Should().Be(CoachStopReason.OutputTokenLimit);

        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var row = Row(harness);
        row.StopReason.Should().BeNull();
        row.Status.Should().Be(CoachSessionStatus.Active);
    }

    [Fact]
    public async Task AnOffTopicTurnWithAnOpenOffer_KeepsTheOfferAndClearsTheFailure()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.OffTopic,
            CoachMessage = "I can only adjust today\u2019s study settings."
        });
        var result = await SubmitAsync(harness, sessionId, "how do I say hello?");

        result.Value!.SessionStatus.Should().Be(CoachSessionStatus.SuggestionPending);

        var row = Row(harness);
        row.Status.Should().Be(CoachSessionStatus.SuggestionPending);
        row.StopReason.Should().BeNull();
        row.PendingSuggestionId.Should().Be(suggestion.SuggestionId);
    }

    [Fact]
    public async Task ARefusedModelAnswer_RecordsValidationFailedAndKeepsTheOffer()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        // A direct change with no delta fails the intent-shape rule.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            CoachMessage = "Changing the plan."
        });
        await SubmitAsync(harness, sessionId, "change something");

        var row = Row(harness);
        row.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        row.Status.Should().Be(CoachSessionStatus.SuggestionPending);
        row.PendingSuggestionId.Should().Be(suggestion.SuggestionId);
    }

    [Fact]
    public async Task TheClarificationCap_RecordsItsOwnStopReason()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "How many minutes do you have?",
            CoachMessage = "I need one detail."
        });

        await SubmitAsync(harness, sessionId, "not much time");
        await SubmitAsync(harness, sessionId, "some time");
        var third = await SubmitAsync(harness, sessionId, "a bit");

        third.Value!.ClarifyingQuestion.Should().BeNull("the cap is two in a row");

        var row = Row(harness);
        row.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        row.ClarificationCount.Should().Be(2);
    }

    /// <summary>
    /// Every refusal logs why it refused; the cap has to as well, or a flagged notice cannot be
    /// traced to it (issue 227).
    /// </summary>
    [Fact]
    public async Task TheClarificationCap_IsLogged()
    {
        var log = new Memory.CapturingLoggerProvider();
        using var harness = new CoachApplicationHarness(loggerProvider: log);
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "How many minutes do you have?"
        });

        await SubmitAsync(harness, sessionId, "not much time");
        await SubmitAsync(harness, sessionId, "some time");
        log.Lines.Should().NotContain(line => line.Contains("clarifying question after", StringComparison.Ordinal),
            "the first two questions are asked");

        var third = await SubmitAsync(harness, sessionId, "a bit");

        third.Value!.Messages.Should().ContainSingle().Which.Kind.Should().Be(CoachMessageKind.Notice);
        log.Lines.Should().ContainSingle(line => line.Contains(
            "the model asked a clarifying question after 2 of the 2 clarification(s) allowed in a row were used",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// The cap is on questions in a row. Counted over the whole session it never came back, and
    /// a conversation that is used every day keeps its session: after two questions, ever, each
    /// later one was withheld behind a notice (issue 227).
    /// </summary>
    [Fact]
    public async Task AReply_StartsTheClarificationCountAgain()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var question = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "How many minutes do you have?"
        });

        harness.Coach.NextResult = question;
        await SubmitAsync(harness, sessionId, "not much time");
        var second = await SubmitAsync(harness, sessionId, "some time");
        second.Value!.ClarificationsRemaining.Should().Be(0);

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = "You have 248 words."
        });
        var reply = await SubmitAsync(harness, sessionId, "How many words do I have?");

        reply.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        reply.Value.ClarificationsRemaining.Should().Be(2, "a turn that ended in a reply starts the count again");
        Row(harness).ClarificationCount.Should().Be(0);

        harness.Coach.NextResult = question;
        var later = await SubmitAsync(harness, sessionId, "change my plan a bit");

        later.Value!.Messages.Should().ContainSingle().Which.Kind.Should().Be(CoachMessageKind.Clarification);
        later.Value.ClarifyingQuestion.Should().Be("How many minutes do you have?");
        later.Value.ClarificationsRemaining.Should().Be(1);
        harness.Coach.Requests[^1].ClarificationsRemaining.Should().Be(2, "the model is told the count started again");
    }

    /// <summary>
    /// A control's change that changes nothing is a completed turn like any other. It skipped the
    /// recorder, so it neither cleared an earlier stop reason nor started the count again.
    /// </summary>
    [Fact]
    public async Task AControlChangeThatChangesNothing_IsRecordedAsAReply()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "How many minutes do you have?"
        });
        await SubmitAsync(harness, sessionId, "not much time");
        await SubmitAsync(harness, sessionId, "some time");
        Row(harness).StopReason.Should().Be(CoachStopReason.ClarificationRequested);

        var unchanged = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.ConstraintAction,
            ConstraintAction = new CoachConstraintDeltaDto()
        });

        unchanged.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        Row(harness).StopReason.Should().BeNull();
        Row(harness).ClarificationCount.Should().Be(0);
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    /// <summary>A refused reply and a cut-off one leave the count where it was.</summary>
    [Fact]
    public async Task ARefusedReply_DoesNotStartTheClarificationCountAgain()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "How many minutes do you have?"
        });
        await SubmitAsync(harness, sessionId, "not much time");
        await SubmitAsync(harness, sessionId, "some time");

        // A direct change with no delta fails the intent-shape rule: a refused reply, recorded
        // through the same place a successful one is.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            CoachMessage = "Changing the plan."
        });
        var refused = await SubmitAsync(harness, sessionId, "change something");

        refused.Value!.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        Row(harness).ClarificationCount.Should().Be(2);

        // A reply that was cut off is recorded by a different path and leaves it too.
        harness.Coach.NextResult = await OutputLimitResultAsync();
        var capped = await SubmitAsync(harness, sessionId, "a bit");

        capped.Value!.StopReason.Should().Be(CoachStopReason.OutputTokenLimit);
        Row(harness).ClarificationCount.Should().Be(2);
    }

    /// <summary>
    /// At the cap the learner used to read the refused-reply notice, which told them to say it a
    /// little differently. Nothing was refused and rewording does not help; the notice now says
    /// what happened.
    /// </summary>
    [Fact]
    public async Task TheClarificationCap_SaysWhatHappened()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "How many minutes do you have?"
        });
        await SubmitAsync(harness, sessionId, "not much time");
        await SubmitAsync(harness, sessionId, "some time");
        var third = await SubmitAsync(harness, sessionId, "a bit");

        var notice = third.Value!.Messages.Should().ContainSingle().Subject;
        notice.Kind.Should().Be(CoachMessageKind.Notice);
        notice.Text.Should().Be(CoachDeterministicCopy.ClarificationLimitReached);
        notice.Text.Should().NotBe(CoachDeterministicCopy.ValidationFailedNeutral);
        third.Value.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        harness.Coach.Requests[^1].ClarificationsRemaining.Should().Be(0);
    }

    /// <summary>
    /// Issue 227: "create a new plan for today". Sam now proposes the new plan with a tool and
    /// says so in a plain reply. This holds the server's side of that reply: it is shown, and
    /// nothing about the request being about the plan refuses it. It passed before the
    /// instructions changed; what the model does with them is checked on a running Sam.
    /// </summary>
    [Fact]
    public async Task APlainReply_ToARequestForANewPlan_IsShown()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        const string Reply = "A new plan for today is ready for you to approve.";

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = Reply
        });
        var turn = await SubmitAsync(harness, sessionId, "Create a new plan for today");

        turn.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        turn.Value.Messages.Should().ContainSingle().Which.Text.Should().Be(Reply);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    private static CoachSession Row(CoachApplicationHarness harness) => harness.Db.CoachSessions.Single();

    private static CoachAgentTurnResult Completed(CoachTurnIntent intent) =>
        new() { Outcome = CoachAgentOutcome.Completed, Intent = intent };

    /// <summary>The real turn runner's result for a response that stopped at the cap.</summary>
    private static Task<CoachAgentTurnResult> OutputLimitResultAsync() =>
        Task.FromResult(new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.OutputLimitReached,
            AgentSessionJson = """{"turn":1}""",
            FailureReason = "The answer stopped at the output token limit."
        });

    private static Task<CoachOperationResult<CoachTurnResponse>> SubmitAsync(
        CoachApplicationHarness harness, string sessionId, string text, string? pendingSuggestionId = null) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text,
            PendingSuggestionId = pendingSuggestionId
        });

    private static Task<CoachOperationResult<CoachTurnResponse>> SuggestAsync(
        CoachApplicationHarness harness, string sessionId)
    {
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.SuggestConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { SkillEmphasis = CoachSkillEmphasis.Writing },
            CoachMessage = "Would you like a short writing activity?"
        });

        return SubmitAsync(harness, sessionId, "Suggest a change to Today's Plan.");
    }
}
