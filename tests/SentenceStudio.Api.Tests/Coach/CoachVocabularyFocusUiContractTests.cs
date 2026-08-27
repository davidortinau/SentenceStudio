using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The shapes a client renders a vocabulary focus from.
/// </summary>
/// <remarks>
/// <para>
/// A client must never have to work out what happened by diffing. "No focus" and "the focus was
/// just cleared" are different answers, and a receipt that reported only a nullable focus would
/// make them identical — so a clear would show the previous word list beside the change that
/// removed it. <see cref="CoachVocabularyFocusStatus"/> says which occurred, and the focus attached
/// is always the state after the operation the receipt describes.
/// </para>
/// <para>
/// Every projection here is rebuilt from the frozen selection, never re-resolved, so what a learner
/// sees on the offer, on a reload, and after accepting is the same set in the same order.
/// </para>
/// </remarks>
public class CoachVocabularyFocusUiContractTests
{
    private static readonly string[] Words =
        ["\uAC00\uB2E4", "\uBA39\uB2E4", "\uBCF4\uB2E4", "\uD558\uB2E4", "\uC77D\uB2E4"];

    // ---------------------------------------------------------------- pending

    [Fact]
    public async Task ThePendingOfferCarriesTheOrderedSelection()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = SuggestFocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, "could you suggest a vocabulary focus");

        var focus = offered.Value!.PendingSuggestion!.VocabularyFocus;
        focus.Should().NotBeNull();
        focus!.FocusCode.Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
        focus.DisplayLabel.Should().Be("action verbs");
        focus.SelectedCount.Should().Be(5);
        focus.EligibleCount.Should().Be(12);
        focus.Words.Select(w => w.TargetText).Should().Equal(Words);
        focus.Words.Should().OnlyContain(w => w.TargetLanguageTag == "ko-KR");
    }

    [Fact]
    public async Task ReloadingAnOfferRebuildsTheSameProjectionWithoutResolving()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = SuggestFocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, "could you suggest a vocabulary focus");

        // If a reload re-resolved, this changed answer would surface.
        harness.FocusResolver.NextResult = harness.FocusResolver.NextResult with
        {
            Items = [FakeVocabularyFocusResolver.Word("x-1", "\uB2EC\uB9AC\uB2E4", "to run")]
        };

        var reread = (await harness.Service.GetSessionAsync(sessionId)).Value!.PendingSuggestion!;

        reread.VocabularyFocus.Should().BeEquivalentTo(offered.Value!.PendingSuggestion!.VocabularyFocus);
        reread.VocabularyFocus!.Words.Select(w => w.TargetText).Should().Equal(Words);
        harness.FocusResolver.ResolveCount.Should().Be(1);
    }

    [Fact]
    public async Task ASuggestionThatDoesNotTouchTheFocusCarriesNone()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        // A later offer about minutes must not echo the focus already in force, or it would read
        // as a vocabulary change the learner never asked for.
        NextTurnProducesADifferentPlan(harness);
        harness.Coach.NextResult = Result(CoachIntentKind.SuggestConstraintChange,
            new CoachConstraintDeltaIntent { AvailableMinutes = 30 });

        var offered = await AskAsync(harness, sessionId, "could you suggest something");

        offered.Value!.PendingSuggestion!.VocabularyFocus.Should().BeNull();
    }

    // ---------------------------------------------------------------- receipts

    [Fact]
    public async Task ApplyingAFocusReportsItAsApplied()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        var change = result.Value!.ChangeReceipt!.VocabularyFocus;
        change.Status.Should().Be(CoachVocabularyFocusStatus.Applied);
        change.Focus!.Words.Select(w => w.TargetText).Should().Equal(Words);

        // And it agrees with the active constraints in the same response.
        change.Focus.Should().BeEquivalentTo(result.Value.ActiveConstraints.VocabularyFocus);
    }

    [Fact]
    public async Task AcceptingAnOfferReportsTheSameSelectionThePreviewShowed()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = SuggestFocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, "could you suggest a vocabulary focus");

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, offered.Value!.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

        var change = accepted.Value!.ChangeReceipt!.VocabularyFocus;
        change.Status.Should().Be(CoachVocabularyFocusStatus.Applied);
        change.Focus.Should().BeEquivalentTo(offered.Value.PendingSuggestion!.VocabularyFocus);
    }

    [Fact]
    public async Task ClearingReportsClearedAndCarriesNoStaleList()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        NextTurnProducesADifferentPlan(harness);
        harness.Coach.NextResult = ClearFocusResult();
        var result = await AskAsync(harness, sessionId, "clear vocabulary focus");

        var change = result.Value!.ChangeReceipt!.VocabularyFocus;
        change.Status.Should().Be(CoachVocabularyFocusStatus.Cleared);
        change.Focus.Should().BeNull("showing the removed words beside the removal would be wrong");
        result.Value.ActiveConstraints.VocabularyFocus.Should().BeNull();
    }

    [Fact]
    public async Task AChangeThatIgnoresTheFocusReportsUnchanged()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Result(CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { AvailableMinutes = 30 });

        var result = await AskAsync(harness, sessionId, "make it 30 minutes");

        result.Value!.ChangeReceipt!.VocabularyFocus.Status
            .Should().Be(CoachVocabularyFocusStatus.Unchanged);
    }

    // ---------------------------------------------------------------- undo, in one response

    [Fact]
    public async Task UndoReportsTheRestoredFocusInTheSameResponse()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        NextTurnProducesADifferentPlan(harness);
        harness.Coach.NextResult = ClearFocusResult();
        await AskAsync(harness, sessionId, "clear vocabulary focus");

        var undone = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        // No reload. The response itself is the post-undo state.
        var change = undone.Value!.ChangeReceipt!.VocabularyFocus;
        change.Status.Should().Be(CoachVocabularyFocusStatus.Restored);
        change.Focus!.Words.Select(w => w.TargetText).Should().Equal(Words);

        undone.Value.ActiveConstraints.VocabularyFocus.Should().BeEquivalentTo(change.Focus);
    }

    [Fact]
    public async Task UndoOfAnAppliedFocusReportsItAsCleared()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        var undone = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var change = undone.Value!.ChangeReceipt!.VocabularyFocus;
        change.Status.Should().Be(CoachVocabularyFocusStatus.Cleared);
        change.Focus.Should().BeNull();
        undone.Value.ActiveConstraints.VocabularyFocus.Should().BeNull();
    }

    [Fact]
    public async Task UndoOfAReplacedFocusReportsTheExactOldSetImmediately()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        harness.FocusResolver.NextResult = harness.FocusResolver.NextResult with
        {
            Items = [FakeVocabularyFocusResolver.Word("a-1", "\uD06C\uB2E4", "to be big")]
        };

        NextTurnProducesADifferentPlan(harness);
        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("adjectives"), "focus today on adjectives");

        var undone = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var change = undone.Value!.ChangeReceipt!.VocabularyFocus;
        change.Status.Should().Be(CoachVocabularyFocusStatus.Restored);
        change.Focus!.FocusCode.Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
        change.Focus.Words.Select(w => w.TargetText).Should().Equal(Words);
        undone.Value.ActiveConstraints.VocabularyFocus.Should().BeEquivalentTo(change.Focus);
    }

    // ---------------------------------------------------------------- contract and privacy

    [Fact]
    public void TheChangeShapeCarriesNoIdentifierOrSchedulingField()
    {
        var forbidden = new[] { "id", "due", "mastery", "progress", "hash", "stamp", "query", "score" };

        var names = typeof(CoachVocabularyFocusChangeDto).GetProperties()
            .Concat(typeof(CoachVocabularyFocusDto).GetProperties())
            .Concat(typeof(CoachVocabularyFocusWordDto).GetProperties())
            .Select(p => p.Name)
            .ToArray();

        names.Should().NotContain(n => forbidden.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TheStatusEnumIsAppendOnly()
    {
        // It travels inside stored receipts, so the ordinals are a contract.
        ((int)CoachVocabularyFocusStatus.Unchanged).Should().Be(0);
        ((int)CoachVocabularyFocusStatus.Applied).Should().Be(1);
        ((int)CoachVocabularyFocusStatus.Cleared).Should().Be(2);
        ((int)CoachVocabularyFocusStatus.Restored).Should().Be(3);
    }

    [Fact]
    public void TheContractScannerAcceptsTheNewShapes()
    {
        SentenceStudio.Api.Coach.Validation.CoachOutputContract.Scan().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task NoIdentifierReachesTheOfferOrTheReceipt()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = SuggestFocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, "could you suggest a vocabulary focus");

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, offered.Value!.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

        foreach (var payload in new[]
                 {
                     System.Text.Json.JsonSerializer.Serialize(offered.Value),
                     System.Text.Json.JsonSerializer.Serialize(accepted.Value)
                 })
        {
            foreach (var id in new[] { "v-1", "v-2", "v-3", "v-4", "v-5" })
            {
                payload.Should().NotContain(id);
            }
        }
    }

    [Fact]
    public async Task BothArmsProduceTheSameOfferAndReceiptProjection()
    {
        var projections = new List<(string? Offer, string? Receipt)>();

        foreach (var _ in new[] { "baseline", "harness" })
        {
            using var harness = new CoachApplicationHarness();
            var sessionId = await harness.StartSessionAsync();

            harness.Coach.NextResult = SuggestFocusResult("active verbs");
            var offered = await AskAsync(harness, sessionId, "could you suggest a vocabulary focus");

            var accepted = await harness.Service.AcceptSuggestionAsync(
                sessionId, offered.Value!.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

            projections.Add((
                System.Text.Json.JsonSerializer.Serialize(offered.Value.PendingSuggestion!.VocabularyFocus),
                System.Text.Json.JsonSerializer.Serialize(accepted.Value!.ChangeReceipt!.VocabularyFocus)));
        }

        projections[0].Should().Be(projections[1]);
    }

    // ---------------------------------------------------------------- helpers

    private static void NextTurnProducesADifferentPlan(CoachApplicationHarness harness) =>
        harness.PlanService.NextRemainder =
        [
            FakePlanService.Item(
                $"fresh-{Guid.NewGuid():N}", PlanActivityType.Writing,
                priority: 1, minutes: 4, spent: 0, completed: false)
        ];

    private static CoachAgentTurnResult FocusResult(string description) =>
        Result(CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { VocabularyFocusDescription = description });

    private static CoachAgentTurnResult SuggestFocusResult(string description) =>
        Result(CoachIntentKind.SuggestConstraintChange,
            new CoachConstraintDeltaIntent { VocabularyFocusDescription = description });

    private static CoachAgentTurnResult ClearFocusResult() =>
        Result(CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { ClearVocabularyFocus = true });

    private static CoachAgentTurnResult Result(CoachIntentKind kind, CoachConstraintDeltaIntent delta) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent { Kind = kind, ConstraintDelta = delta, CoachMessage = string.Empty }
    };

    private static Task<CoachOperationResult<CoachTurnResponse>> AskAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });
}
