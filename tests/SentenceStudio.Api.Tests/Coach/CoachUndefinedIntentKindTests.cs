using System.Text.Json;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// What happens when the model names an intent kind the contract does not define.
/// </summary>
/// <remarks>
/// <para>
/// <c>CoachIntentKind</c> is an enum, and .NET enums are not closed at runtime: a JSON number
/// outside the declared range casts straight through, so <c>{"kind":99}</c> deserializes to a
/// <c>CoachTurnIntent</c> whose <c>Kind</c> is 99. Nothing about that value is meaningful — it has
/// no reducer branch and no surfacing rule.
/// </para>
/// <para>
/// Two independent guards. The validator refuses the turn outright, and
/// <c>SurfacedModelText</c> fails closed, so even if a kind ever reached the gate it would be
/// scanned rather than waved through. Neither depends on the other.
/// </para>
/// </remarks>
public class CoachUndefinedIntentKindTests
{
    // ---------------------------------------------------------------- binding

    [Fact]
    public void AnUndefinedNumericKind_BindsButIsRefused()
    {
        var intent = JsonSerializer.Deserialize<CoachTurnIntent>(
            """{"kind":99,"coachMessage":"hello"}""",
            CoachAgentTurnRunner.IntentSerializerOptions);

        // It binds — this is exactly why the validator has to check.
        intent.Should().NotBeNull();
        ((int)intent!.Kind).Should().Be(99);
        Enum.IsDefined(intent.Kind).Should().BeFalse();

        var result = new CoachIntentValidator().ValidateIntent(intent);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "undefined_intent_kind");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(99)]
    [InlineData(int.MaxValue)]
    public void EveryUndefinedKind_IsRefused(int raw)
    {
        var intent = new CoachTurnIntent { Kind = (CoachIntentKind)raw, CoachMessage = "hello" };

        new CoachIntentValidator().ValidateIntent(intent)
            .Violations.Should().Contain(v => v.Code == "undefined_intent_kind");
    }

    [Fact]
    public void EveryDefinedKind_PassesTheKindCheck()
    {
        foreach (var kind in Enum.GetValues<CoachIntentKind>())
        {
            var intent = new CoachTurnIntent { Kind = kind, CoachMessage = "hello" };

            new CoachIntentValidator().ValidateIntent(intent)
                .Violations.Should().NotContain(
                    v => v.Code == "undefined_intent_kind",
                    $"{kind} is declared by the contract");
        }
    }

    // ---------------------------------------------------------------- fail closed

    [Fact]
    public async Task AnUndefinedKind_NeverSurfacesModelTextAndWritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("SENTINELDUEWORD", "sentinel gloss")];

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = (CoachIntentKind)99,
                CoachMessage = "Start with SENTINELDUEWORD today.",
                ClarifyingQuestion = "Or SENTINELDUEWORD instead?"
            }
        };

        var planBefore = harness.PlanService.Current.Version;
        var result = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "10 minutes"
        });

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);

        var surfaced = string.Join(" ", result.Value.Messages.Select(m => m.Text));
        surfaced.Should().NotContain("SENTINELDUEWORD");
        result.Value.ClarifyingQuestion.Should().NotContain("SENTINELDUEWORD");

        result.Value.ChangeReceipt.Should().BeNull();
        result.Value.PendingSuggestion.Should().BeNull();
        harness.PlanService.Current.Version.Should().Be(planBefore);
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public void AnUndefinedKind_IsScannedNotExempted()
    {
        // The gate's own fail-closed behaviour, independent of the validator in front of it.
        SurfacedFor((CoachIntentKind)99).Should().NotBeEmpty(
            "an unknown kind must be treated as if its prose were surfaced");
    }

    // ---------------------------------------------------------------- the mapping

    [Theory]
    [InlineData(CoachIntentKind.NoChange, true)]
    [InlineData(CoachIntentKind.OffTopic, true)]
    [InlineData(CoachIntentKind.AskClarification, true)]
    [InlineData(CoachIntentKind.DirectConstraintChange, false)]
    [InlineData(CoachIntentKind.SuggestConstraintChange, false)]
    [InlineData(CoachIntentKind.AcceptPendingSuggestion, false)]
    [InlineData(CoachIntentKind.RejectPendingSuggestion, false)]
    [InlineData(CoachIntentKind.PedagogicalAnswer, false)]
    public void EveryDefinedKind_MapsToItsReducerBranch(CoachIntentKind kind, bool surfacesModelProse)
    {
        // One row per declared kind, so adding a kind without deciding this fails here.
        SurfacedFor(kind).Any(t => !string.IsNullOrEmpty(t)).Should().Be(surfacesModelProse);
    }

    [Fact]
    public void TheMappingCoversEveryDeclaredKind()
    {
        var covered = typeof(CoachUndefinedIntentKindTests)
            .GetMethod(nameof(EveryDefinedKind_MapsToItsReducerBranch))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false)
            .Cast<InlineDataAttribute>()
            .Select(a => (CoachIntentKind)a.GetData(null!).First()[0]!)
            .ToHashSet();

        covered.Should().BeEquivalentTo(Enum.GetValues<CoachIntentKind>());
    }

    /// <summary>Reads the gate's decision through the reducer's own behaviour.</summary>
    private static IReadOnlyList<string?> SurfacedFor(CoachIntentKind kind)
    {
        var method = typeof(CoachSessionService).GetMethod(
            "SurfacedModelText",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return (IReadOnlyList<string?>)method.Invoke(null, [new CoachTurnIntent
        {
            Kind = kind,
            CoachMessage = "model prose",
            ClarifyingQuestion = "model question"
        }])!;
    }
}
