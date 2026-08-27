using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Typed acceptance and rejection of an open suggestion.
/// </summary>
/// <remarks>
/// Regression cover for an end-to-end defect. "Maybe" correctly asked for clarification and
/// kept the suggestion open; the follow-up "Yes, update it" — with the right suggestion id and
/// the right plan version — came back <c>Rejected</c>/<c>ValidationFailed</c> with the pending
/// offer gone and nothing written. The clear yes was routed through the model first, and the
/// intent validator requires the model to echo the suggestion id in an exact shape. It did
/// not, so an unmistakable acceptance was thrown away.
///
/// The classifier is the authority for that decision, so it now runs before any model work.
/// </remarks>
public class CoachTypedAcceptanceTests
{
    // ---------------------------------------------------------------- the reported sequence

    [Fact]
    public async Task Maybe_ThenYesUpdateIt_Applies()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);

        // "Maybe" is not a decision. The model answers and asks one question.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "Should I update Today\u2019s Plan with that change now?",
            CoachMessage = "I need one detail."
        });

        var maybe = await TypedAsync(harness, sessionId, "Maybe", suggestion.SuggestionId);

        maybe.Value!.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        maybe.Value.PendingSuggestion.Should().NotBeNull();
        harness.Db.CoachPlanRevisions.Should().BeEmpty();

        // The model would answer with an intent the validator refuses. It is never asked.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AcceptPendingSuggestion,
            PendingSuggestionId = null,
            AcceptanceState = CoachAcceptanceState.NotApplicable,
            CoachMessage = "Updated."
        });

        var modelCallsBefore = harness.Coach.RunCount;
        var yes = await TypedAsync(harness, sessionId, "Yes, update it", suggestion.SuggestionId);

        yes.IsOk.Should().BeTrue();
        yes.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        yes.Value.ChangeReceipt.Should().NotBeNull();
        yes.Value.PendingSuggestion.Should().BeNull();
        harness.Coach.RunCount.Should().Be(modelCallsBefore, "a clear yes never reaches the model");
        harness.Db.CoachPlanRevisions.Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- phrase bank

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes, update it")]
    [InlineData("Yes.")]
    [InlineData("ok")]
    [InlineData("do it")]
    [InlineData("go ahead")]
    [InlineData("sounds good")]
    [InlineData("\uB124")]
    [InlineData("\uC88B\uC544\uC694")]
    [InlineData("\uD574\uC8FC\uC138\uC694")]
    public async Task ClearAffirmatives_ApplyWithoutTheModel(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);
        var modelCallsBefore = harness.Coach.RunCount;

        var result = await TypedAsync(harness, sessionId, text, suggestion.SuggestionId);

        result.Value!.ChangeReceipt.Should().NotBeNull();
        harness.Coach.RunCount.Should().Be(modelCallsBefore);
        harness.PlanService.ApplyCallCount.Should().Be(1);
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
    }

    [Theory]
    [InlineData("no")]
    [InlineData("no thanks")]
    [InlineData("not now")]
    [InlineData("skip it")]
    [InlineData("\uC544\uB2C8\uC694")]
    [InlineData("\uB098\uC911\uC5D0")]
    public async Task ClearNegatives_ClearThePendingSuggestionWithoutTheModel(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);
        var modelCallsBefore = harness.Coach.RunCount;

        var result = await TypedAsync(harness, sessionId, text, suggestion.SuggestionId);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.PendingSuggestion.Should().BeNull();
        harness.Coach.RunCount.Should().Be(modelCallsBefore);
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- gates

    [Fact]
    public async Task AMismatchedSuggestionId_DoesNotShortcutAndDoesNotWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        await OfferSuggestionAsync(harness, sessionId);
        var modelCallsBefore = harness.Coach.RunCount;

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.NoChange,
            CoachMessage = "Which change do you mean?"
        });

        var result = await TypedAsync(harness, sessionId, "yes", "a-suggestion-that-is-not-open");

        harness.Coach.RunCount.Should().Be(modelCallsBefore + 1, "a mismatched id is not a decision");
        harness.PlanService.ApplyCallCount.Should().Be(0);
        result.Value!.PendingSuggestion.Should().NotBeNull("the open offer survives");
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task NoOpenSuggestion_DoesNotShortcut()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var modelCallsBefore = harness.Coach.RunCount;

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.NoChange,
            CoachMessage = "There is nothing waiting for an answer."
        });

        var result = await TypedAsync(harness, sessionId, "yes", "anything");

        harness.Coach.RunCount.Should().Be(modelCallsBefore + 1);
        harness.PlanService.ApplyCallCount.Should().Be(0);
        result.IsOk.Should().BeTrue();
    }

    [Theory]
    // No question mark, but unmistakably a question about a word.
    [InlineData("\uC88B\uC544\uC694 \uB73B\uC774 \uBB50\uC608\uC694")]
    [InlineData("does \uC88B\uC544\uC694 mean good")]
    [InlineData("what does yes mean")]
    // With a question mark, and quoted.
    [InlineData("\uC88B\uC544\uC694?")]
    [InlineData("\"yes\"")]
    public async Task ALexicalQuestion_NeverAppliesThePendingOffer(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);
        var modelCallsBefore = harness.Coach.RunCount;

        // The model is given the chance to call it an acceptance. The classifier decides.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AcceptPendingSuggestion,
            PendingSuggestionId = suggestion.SuggestionId,
            AcceptanceState = CoachAcceptanceState.Accepted,
            CoachMessage = "Updated Today\u2019s Plan."
        });

        var result = await TypedAsync(harness, sessionId, text, suggestion.SuggestionId);

        // It reached the model rather than the deterministic write path, and asked instead.
        harness.Coach.RunCount.Should().Be(modelCallsBefore + 1);
        result.Value!.StopReason.Should().Be(CoachStopReason.ClarificationRequested);

        harness.PlanService.ApplyCallCount.Should().Be(0, "a question never applies a plan change");
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.Db.CoachSessions.Single().PendingSuggestionId
            .Should().Be(suggestion.SuggestionId, "the offer is preserved");
        result.Value.PendingSuggestion!.SuggestionId.Should().Be(suggestion.SuggestionId);
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("not sure")]
    [InlineData("yes but not the writing one")]
    [InlineData("\uAE00\uC30E\uC694")]
    public async Task AnUnclearAnswer_GoesToTheModelAndKeepsTheSuggestion(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);
        var modelCallsBefore = harness.Coach.RunCount;

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "Should I update Today\u2019s Plan with that change now?",
            CoachMessage = "I need one detail."
        });

        var result = await TypedAsync(harness, sessionId, text, suggestion.SuggestionId);

        harness.Coach.RunCount.Should().Be(modelCallsBefore + 1);
        harness.PlanService.ApplyCallCount.Should().Be(0);
        result.Value!.PendingSuggestion.Should().NotBeNull();
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().Be(suggestion.SuggestionId);
    }

    // ---------------------------------------------------------------- parity with the taps

    [Fact]
    public async Task TypedYes_ProducesTheSameResultAsTappingAccept()
    {
        using var typedHarness = new CoachApplicationHarness();
        using var tappedHarness = new CoachApplicationHarness();

        var typedSession = await typedHarness.StartSessionAsync();
        var tappedSession = await tappedHarness.StartSessionAsync();

        var typedSuggestion = await OfferSuggestionAsync(typedHarness, typedSession);
        var tappedSuggestion = await OfferSuggestionAsync(tappedHarness, tappedSession);

        var typed = await TypedAsync(typedHarness, typedSession, "Yes, update it", typedSuggestion.SuggestionId);
        var tapped = await tappedHarness.Service.AcceptSuggestionAsync(
            tappedSession, tappedSuggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        var typedReceipt = typed.Value!.ChangeReceipt!;
        var tappedReceipt = tapped.Value!.ChangeReceipt!;

        typedReceipt.Diff.AfterPlanVersion.Should().Be(tappedReceipt.Diff.AfterPlanVersion);
        typedReceipt.Diff.BeforePlanVersion.Should().Be(tappedReceipt.Diff.BeforePlanVersion);
        typedReceipt.Summary.Should().Be(tappedReceipt.Summary);
        typedReceipt.ReplacedItemCount.Should().Be(tappedReceipt.ReplacedItemCount);
        typedReceipt.PreservedCompletedItemCount.Should().Be(tappedReceipt.PreservedCompletedItemCount);
        typedReceipt.PreservedInProgressItemCount.Should().Be(tappedReceipt.PreservedInProgressItemCount);
        typedReceipt.PreservedMinutesSpent.Should().Be(tappedReceipt.PreservedMinutesSpent);
        typedReceipt.CanUndo.Should().Be(tappedReceipt.CanUndo);

        typedReceipt.Diff.Items.Select(i => (i.Id, i.ChangeKind))
            .Should().BeEquivalentTo(tappedReceipt.Diff.Items.Select(i => (i.Id, i.ChangeKind)));

        typedHarness.PlanService.Current.Version.Should().Be(tappedHarness.PlanService.Current.Version);

        var typedRevision = typedHarness.Db.CoachPlanRevisions.Single();
        var tappedRevision = tappedHarness.Db.CoachPlanRevisions.Single();
        typedRevision.Source.Should().Be(tappedRevision.Source);
        typedRevision.IntentKind.Should().Be(tappedRevision.IntentKind);
        typedRevision.AcceptedConstraintDeltaJson.Should().Be(tappedRevision.AcceptedConstraintDeltaJson);
        typedRevision.AfterPlanHash.Should().Be(tappedRevision.AfterPlanHash);
    }

    [Fact]
    public async Task TypedNo_ProducesTheSameResultAsTappingNotNow()
    {
        using var typedHarness = new CoachApplicationHarness();
        using var tappedHarness = new CoachApplicationHarness();

        var typedSession = await typedHarness.StartSessionAsync();
        var tappedSession = await tappedHarness.StartSessionAsync();

        var typedSuggestion = await OfferSuggestionAsync(typedHarness, typedSession);
        var tappedSuggestion = await OfferSuggestionAsync(tappedHarness, tappedSession);

        var typed = await TypedAsync(typedHarness, typedSession, "no thanks", typedSuggestion.SuggestionId);
        var tapped = await tappedHarness.Service.RejectSuggestionAsync(
            tappedSession, tappedSuggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        typed.Value!.Status.Should().Be(tapped.Value!.Status);
        typed.Value.StopReason.Should().Be(tapped.Value.StopReason);
        typed.Value.SessionStatus.Should().Be(tapped.Value.SessionStatus);
        typed.Value.PendingSuggestion.Should().BeNull();
        tapped.Value.PendingSuggestion.Should().BeNull();

        typed.Value.Messages.Select(m => (m.Role, m.Kind, m.Text))
            .Should().BeEquivalentTo(tapped.Value.Messages.Select(m => (m.Role, m.Kind, m.Text)));

        typedHarness.PlanService.Current.Version.Should().Be(tappedHarness.PlanService.Current.Version);
        typedHarness.Db.CoachPlanRevisions.Should().BeEmpty();
        tappedHarness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- budget

    [Fact]
    public async Task ADeterministicDecision_ChargesNoRunAndNoTokens()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);

        var runsBefore = (await harness.Service.GetAvailabilityAsync()).Value!.RunsRemainingToday;

        var accepted = await TypedAsync(harness, sessionId, "yes", suggestion.SuggestionId);

        // The caps bound model cost, and nothing here calls a model. Charging a run would also
        // make typing "yes" cost more than tapping Accept for the same learner action, and
        // could strand a suggestion the learner is no longer allowed to answer.
        accepted.Value!.RunsRemainingToday.Should().Be(runsBefore);
        (await harness.Service.GetAvailabilityAsync()).Value!.RunsRemainingToday.Should().Be(runsBefore);
        harness.Db.CoachUsages.Should().BeEmpty("no tokens were spent");
    }

    [Fact]
    public async Task ADeterministicDecision_WorksEvenWhenTheRunBudgetIsSpent()
    {
        using var harness = new CoachApplicationHarness(new SentenceStudio.Api.Coach.Runtime.CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = { CoachApplicationHarness.OwnerUserId },
            MaxRunsPerDay = 1,
            MaxRunsPerWeek = 1
        });

        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);

        // That suggestion consumed the learner's only run for the day.
        var spent = await TypedAsync(harness, sessionId, "Maybe", suggestion.SuggestionId);
        spent.Status.Should().Be(CoachOperationStatus.RateLimited);

        var accepted = await TypedAsync(harness, sessionId, "yes", suggestion.SuggestionId);

        accepted.IsOk.Should().BeTrue("a learner must always be able to answer an offer already made");
        accepted.Value!.ChangeReceipt.Should().NotBeNull();
    }

    // ---------------------------------------------------------------- idempotency and safety

    [Fact]
    public async Task RepeatingATypedAcceptWithTheSameClientTurnId_WritesOnce()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);

        var request = new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Yes, update it",
            PendingSuggestionId = suggestion.SuggestionId,
            ClientTurnId = "typed-accept-1"
        };

        var first = await harness.Service.SubmitTurnAsync(sessionId, request);
        var second = await harness.Service.SubmitTurnAsync(sessionId, request);

        second.Value!.TurnId.Should().Be(first.Value!.TurnId);
        harness.PlanService.ApplyCallCount.Should().Be(1);
        harness.Db.CoachPlanRevisions.Should().HaveCount(1);
    }

    [Fact]
    public async Task AStalePlanVersion_KeepsTheSuggestionOpenAndWritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);

        var result = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Yes, update it",
            PendingSuggestionId = suggestion.SuggestionId,
            ExpectedPlanVersion = "v1:someone-else-changed-the-plan"
        });

        result.Status.Should().Be(CoachOperationStatus.PlanChangedElsewhere);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.Db.CoachSessions.Single().PendingSuggestionId
            .Should().Be(suggestion.SuggestionId, "a refused apply never withdraws the offer");

        // And the learner can still accept it against the current plan version.
        var retry = await TypedAsync(harness, sessionId, "Yes, update it", suggestion.SuggestionId);
        retry.Value!.ChangeReceipt.Should().NotBeNull();
    }

    [Fact]
    public async Task AnInvalidModelAcceptanceIntent_DoesNotWithdrawTheSuggestion()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);

        // Ambiguous text reaches the model, and the model answers with an acceptance intent
        // the validator refuses. The offer must survive that.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AcceptPendingSuggestion,
            PendingSuggestionId = null,
            AcceptanceState = CoachAcceptanceState.NotApplicable,
            CoachMessage = "Updated."
        });

        var result = await TypedAsync(harness, sessionId, "Maybe", suggestion.SuggestionId);

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        result.Value.SessionStatus.Should().Be(CoachSessionStatus.SuggestionPending);
        result.Value.PendingSuggestion.Should().NotBeNull();
        result.Value.PendingSuggestion!.SuggestionId.Should().Be(suggestion.SuggestionId);

        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().Be(suggestion.SuggestionId);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task ATypedYesForAnotherLearnersSession_Returns404()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferSuggestionAsync(harness, sessionId);

        harness.UserScope.Current = CoachApplicationHarness.OtherUserId;
        var result = await TypedAsync(harness, sessionId, "yes", suggestion.SuggestionId);

        result.Status.Should().Be(CoachOperationStatus.SessionNotFound);
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    // ---------------------------------------------------------------- helpers

    private static CoachAgentTurnResult Completed(CoachTurnIntent intent) =>
        new() { Outcome = CoachAgentOutcome.Completed, Intent = intent };

    private static Task<CoachOperationResult<CoachTurnResponse>> TypedAsync(
        CoachApplicationHarness harness, string sessionId, string text, string? pendingSuggestionId) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text,
            PendingSuggestionId = pendingSuggestionId
        });

    /// <summary>Puts a valid Writing-emphasis suggestion in front of the learner.</summary>
    private static async Task<PendingCoachSuggestionDto> OfferSuggestionAsync(
        CoachApplicationHarness harness, string sessionId)
    {
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.SuggestConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { SkillEmphasis = CoachSkillEmphasis.Writing },
            CoachMessage = "Would you like to add a short writing activity today?"
        });

        var offered = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "what should I do today?"
        });

        return offered.Value!.PendingSuggestion!;
    }
}
