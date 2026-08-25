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
        await SubmitAsync(harness, sessionId, "suggest something");
        Row(harness).StopReason.Should().Be(CoachStopReason.OutputTokenLimit);

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10 },
            CoachMessage = "Done."
        });
        await SubmitAsync(harness, sessionId, "make it 10 minutes");

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
        await SubmitAsync(harness, sessionId, "make it 10 minutes");

        harness.Coach.NextResult = await OutputLimitResultAsync();
        await SubmitAsync(harness, sessionId, "suggest something");
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

        third.Value!.ClarifyingQuestion.Should().BeNull("the cap is two per session");

        var row = Row(harness);
        row.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        row.ClarificationCount.Should().Be(2);
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

        return SubmitAsync(harness, sessionId, "what should I do today?");
    }
}
