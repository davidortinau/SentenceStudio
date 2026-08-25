using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Validation;

/// <summary>
/// The reducer's safety gates, exercised through the real <c>CoachSessionService</c>.
/// A leaked due word and an unowned preview must both stop the turn before the plan is
/// written and before any model text reaches the learner.
/// </summary>
public class CoachSafetyGateWiringTests
{
    private const string DueTerm = "사과";
    private const string DueGloss = "apple";

    private static void SeedDueWord(CoachApplicationHarness harness) =>
        harness.ValidationData.EmbargoedItems.Add(
            new CoachEmbargoedItem(DueTerm, DueGloss, Lemma: DueTerm, Examples: ["사과를 먹었습니다."]));

    private static CoachTurnIntent DirectChange(string message) => new()
    {
        Kind = CoachIntentKind.DirectConstraintChange,
        AcceptanceState = CoachAcceptanceState.NotApplicable,
        CoachMessage = message,
        ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10 }
    };

    private static CoachTurnRequest TextTurn(string text) => new()
    {
        InputKind = CoachTurnInputKind.Text,
        Text = text
    };

    // ------------------------------------------------------------------ leak gate

    /// <remarks>
    /// A direct change surfaces a deterministic receipt built from the validated delta, so the
    /// model's own message is discarded. Discarded text cannot leak, and refusing the turn over it
    /// would throw away a change the application validated itself — so the guarantee here is that
    /// the message never reaches the learner, not that the turn dies.
    /// </remarks>
    [Theory]
    [InlineData("Start with 사과 today.")]
    [InlineData("오늘은 사과를 복습합니다.")]
    [InlineData("One of your due words means apple.")]
    [InlineData("Try this sentence: 사과를 먹었습니다.")]
    public async Task ALeakyDirectChangeMessageIsDiscardedAndNeverSurfaced(string leakedMessage)
    {
        using var harness = new CoachApplicationHarness();
        SeedDueWord(harness);
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = DirectChange(leakedMessage),
            AgentSessionJson = """{"state":"after-leak"}"""
        };

        var result = await harness.Service.SubmitTurnAsync(sessionId, TextTurn("10 minutes"));

        result.IsOk.Should().BeTrue();

        var surfaced = string.Join(" ", result.Value!.Messages.Select(m => m.Text));
        surfaced.Should().NotContain(DueTerm);
        surfaced.Should().NotContain(DueGloss);
        surfaced.Should().NotContain(leakedMessage);
        result.Value.ClarifyingQuestion.Should().BeNull();

        // The receipt is application-owned, so the change still lands.
        result.Value.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.ChangeReceipt.Should().NotBeNull();
    }

    [Fact]
    public async Task ALeakyDirectChangeNeverSurfacesTheTermOrItsGloss()
    {
        using var harness = new CoachApplicationHarness();
        SeedDueWord(harness);
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = DirectChange($"Start with {DueTerm} today.")
        };

        var result = await harness.Service.SubmitTurnAsync(sessionId, TextTurn("10 minutes"));

        var text = string.Join(" ", result.Value!.Messages.Select(m => m.Text));
        text.Should().NotContain(DueTerm);
        text.Should().NotContain(DueGloss);
        result.Value.ClarifyingQuestion.Should().BeNull("a refusal never asks the model to try again");
    }

    [Fact]
    public async Task ALeakedClarifyingQuestionIsAlsoRefused()
    {
        using var harness = new CoachApplicationHarness();
        SeedDueWord(harness);
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.AskClarification,
                AcceptanceState = CoachAcceptanceState.Ambiguous,
                CoachMessage = "One question first.",
                ClarifyingQuestion = $"Should I start with {DueTerm}?"
            }
        };

        var result = await harness.Service.SubmitTurnAsync(sessionId, TextTurn("maybe"));

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        string.Join(" ", result.Value.Messages.Select(m => m.Text)).Should().NotContain(DueTerm);
    }

    [Fact]
    public async Task TheGateRunsOnlyOnceAndDoesNotRePrompt()
    {
        using var harness = new CoachApplicationHarness();
        SeedDueWord(harness);
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = DirectChange($"Start with {DueTerm}.")
        };

        await harness.Service.SubmitTurnAsync(sessionId, TextTurn("10 minutes"));

        harness.Coach.RunCount.Should().Be(1, "a refusal is terminal; the coach never asks the model again");
    }

    [Fact]
    public async Task ADirectChangeDoesNotQueryTheDueQueueAtAll()
    {
        using var harness = new CoachApplicationHarness();
        SeedDueWord(harness);
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = DirectChange("Today's Plan now fits 10 minutes.")
        };

        var result = await harness.Service.SubmitTurnAsync(sessionId, TextTurn("10 minutes"));

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        harness.ValidationData.EmbargoQueryCount.Should().Be(0,
            "nothing the model wrote is surfaced on this path, so there is nothing to scan");
    }

    [Fact]
    public async Task AnEmptyEmbargoSetSkipsTheCheckWithoutFailing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = DirectChange($"Start with {DueTerm}.")
        };

        var result = await harness.Service.SubmitTurnAsync(sessionId, TextTurn("10 minutes"));

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed,
            "nothing is due, so no word is embargoed");
    }

    // ------------------------------------------------------------------ ownership gate

    [Fact]
    public async Task AModelDerivedDirectChangeChecksPreviewOwnershipBeforeTheWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = DirectChange("Today's Plan now fits 10 minutes.")
        };

        await harness.Service.SubmitTurnAsync(sessionId, TextTurn("10 minutes"));

        harness.ValidationData.OwnershipQueryCount.Should().BeGreaterThan(0,
            "a model-derived change must be ownership-checked before it is applied");
    }

    [Fact]
    public async Task AnUnownedPreviewRefusesTheWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // The learner owns nothing, so every resource the preview names is foreign.
        harness.ValidationData.OwnedProvider = () => Array.Empty<string>();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = DirectChange("Today's Plan now fits 10 minutes.")
        };

        var before = harness.PlanService.Current.Version;
        var result = await harness.Service.SubmitTurnAsync(sessionId, TextTurn("10 minutes"));

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        result.Value.ChangeReceipt.Should().BeNull();
        harness.PlanService.Current.Version.Should().Be(before);
    }

    [Fact]
    public async Task AnUnownedSuggestionPreviewIsNeverStoredAsPending()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.ValidationData.OwnedProvider = () => Array.Empty<string>();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                AcceptanceState = CoachAcceptanceState.NotApplicable,
                CoachMessage = "Add a short speaking activity?",
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 12 }
            }
        };

        var result = await harness.Service.SubmitTurnAsync(sessionId, TextTurn("what should I do?"));

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.PendingSuggestion.Should().BeNull("an unverified preview never becomes a pending suggestion");
    }

    [Fact]
    public async Task AStructuredConstraintActionSkipsTheModelOutputChecks()
    {
        using var harness = new CoachApplicationHarness();
        SeedDueWord(harness);
        var sessionId = await harness.StartSessionAsync();

        // The UI action is deterministic input: no model output to leak, and no
        // model-derived preview to verify. Ownership stays with plan validation.
        harness.ValidationData.OwnedProvider = () => Array.Empty<string>();

        var result = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.ConstraintAction,
            ConstraintAction = new CoachConstraintDeltaDto
            {
                AvailableMinutes = 12,
                ChangedFields = [CoachConstraintField.AvailableMinutes]
            }
        });

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        harness.Coach.RunCount.Should().Be(0, "a structured action never calls the model");
        harness.ValidationData.EmbargoQueryCount.Should().Be(0, "there is no model text to check");
        harness.ValidationData.OwnershipQueryCount.Should().Be(0, "there is no model-derived preview to verify");
    }
}
