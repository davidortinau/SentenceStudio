using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// Proves the model-facing turn intent can name every decision the application needs,
/// and that it cannot carry identity data or a write command.
/// </summary>
public class CoachTurnIntentContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(CoachIntentKind.DirectConstraintChange)]
    [InlineData(CoachIntentKind.SuggestConstraintChange)]
    [InlineData(CoachIntentKind.AcceptPendingSuggestion)]
    [InlineData(CoachIntentKind.RejectPendingSuggestion)]
    [InlineData(CoachIntentKind.AskClarification)]
    [InlineData(CoachIntentKind.OffTopic)]
    [InlineData(CoachIntentKind.NoChange)]
    public void Every_required_turn_decision_has_a_kind(CoachIntentKind kind)
    {
        Enum.IsDefined(kind).Should().BeTrue();
    }

    [Fact]
    public void The_intent_set_is_closed()
    {
        Enum.GetNames<CoachIntentKind>().Should().BeEquivalentTo(
        [
            nameof(CoachIntentKind.NoChange),
            nameof(CoachIntentKind.DirectConstraintChange),
            nameof(CoachIntentKind.SuggestConstraintChange),
            nameof(CoachIntentKind.AcceptPendingSuggestion),
            nameof(CoachIntentKind.RejectPendingSuggestion),
            nameof(CoachIntentKind.AskClarification),
            nameof(CoachIntentKind.OffTopic),
            // The dual-purpose coach: answering a language question is a no-write turn.
            nameof(CoachIntentKind.PedagogicalAnswer)
        ]);
    }

    [Fact]
    public void The_acceptance_set_names_the_unclear_case()
    {
        Enum.GetNames<CoachAcceptanceState>().Should().BeEquivalentTo(
        [
            nameof(CoachAcceptanceState.NotApplicable),
            nameof(CoachAcceptanceState.Ambiguous),
            nameof(CoachAcceptanceState.Accepted),
            nameof(CoachAcceptanceState.Rejected)
        ]);
    }

    [Fact]
    public void Every_intent_property_carries_a_description_for_the_model()
    {
        var offenders = CoachContractTypes.IntentShapes
            .SelectMany(t => CoachContractTypes.PublicProperties(t).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.GetCustomAttribute<DescriptionAttribute>() is null)
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        offenders.Should().BeEmpty("Microsoft.Extensions.AI builds the schema from the description attributes");
    }

    [Fact]
    public void Every_intent_property_is_settable_for_structured_output()
    {
        var offenders = CoachContractTypes.IntentShapes
            .SelectMany(t => CoachContractTypes.PublicProperties(t).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.SetMethod is null || !x.Property.SetMethod.IsPublic)
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        offenders.Should().BeEmpty("the deserializer must fill the shape from the model output");
    }

    [Fact]
    public void No_intent_property_is_required()
    {
        var offenders = CoachContractTypes.IntentShapes
            .Where(t => t.GetCustomAttributes().Any(a => a.GetType().Name == "RequiredMemberAttribute"))
            .Select(t => t.Name)
            .ToList();

        offenders.Should().BeEmpty("a missing member must fall back to the safe default, not throw");
    }

    [Fact]
    public void The_intent_carries_no_plan_item_selection()
    {
        var names = CoachContractTypes.PublicProperties(typeof(CoachTurnIntent))
            .Select(p => p.Name)
            .ToList();

        names.Should().BeEquivalentTo(
        [
            nameof(CoachTurnIntent.Kind),
            nameof(CoachTurnIntent.ConstraintDelta),
            nameof(CoachTurnIntent.PendingSuggestionId),
            nameof(CoachTurnIntent.AcceptanceState),
            nameof(CoachTurnIntent.ClarifyingQuestion),
            nameof(CoachTurnIntent.CoachMessage),
            nameof(CoachTurnIntent.EvidenceReferences),
            // Bounded, closed, and no-write. It still names no plan item: the deterministic
            // planner owns item selection, and an answer selects nothing at all.
            nameof(CoachTurnIntent.PedagogicalAnswer),
            // Also bounded, closed, and no-write. A proposal is not a write and not an
            // activation: the application re-derives every trusted field, revalidates the
            // evidence span, and stores a candidate the learner must approve separately.
            nameof(CoachTurnIntent.MemoryProposal)
        ], "the deterministic planner owns item selection");
    }

    [Fact]
    public void The_intent_delta_names_the_same_fields_as_the_public_delta()
    {
        var intentFields = CoachContractTypes.PublicProperties(typeof(CoachConstraintDeltaIntent))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var publicFields = CoachContractTypes.PublicProperties(typeof(CoachConstraintDeltaDto))
            .Select(p => p.Name)
            .Where(n => n != nameof(CoachConstraintDeltaDto.ChangedFields))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        intentFields.Should().Equal(publicFields, "the application maps the model delta onto the public delta");
    }

    [Fact]
    public void An_empty_model_answer_falls_back_to_no_change()
    {
        var intent = JsonSerializer.Deserialize<CoachTurnIntent>("{}", Options);

        intent!.Kind.Should().Be(CoachIntentKind.NoChange);
        intent.AcceptanceState.Should().Be(CoachAcceptanceState.NotApplicable);
        intent.ConstraintDelta.Should().BeNull();
        intent.PendingSuggestionId.Should().BeNull();
        intent.EvidenceReferences.Should().BeEmpty();
    }

    [Fact]
    public void A_direct_constraint_change_round_trips()
    {
        const string json = """
            {
              "kind": "DirectConstraintChange",
              "acceptanceState": "NotApplicable",
              "coachMessage": "Today's Plan now fits 10 minutes and uses no audio.",
              "constraintDelta": {
                "availableMinutes": 10,
                "audioAllowed": false,
                "energyLevel": "Low"
              },
              "evidenceReferences": [ { "kind": "PracticeBalance", "windowDays": 14 } ]
            }
            """;

        var intent = JsonSerializer.Deserialize<CoachTurnIntent>(json, Options)!;

        intent.Kind.Should().Be(CoachIntentKind.DirectConstraintChange);
        intent.ConstraintDelta!.AvailableMinutes.Should().Be(10);
        intent.ConstraintDelta.AudioAllowed.Should().BeFalse();
        intent.ConstraintDelta.SpeechAllowed.Should().BeNull("an unnamed field must stay unchanged");
        intent.ConstraintDelta.EnergyLevel.Should().Be(CoachEnergyLevel.Low);
        intent.EvidenceReferences.Should().ContainSingle()
            .Which.Kind.Should().Be(CoachEvidenceKind.PracticeBalance);
    }

    [Fact]
    public void An_unclear_answer_keeps_the_acceptance_state_separate_from_the_kind()
    {
        const string json = """
            {
              "kind": "AskClarification",
              "acceptanceState": "Ambiguous",
              "pendingSuggestionId": "suggestion-1",
              "clarifyingQuestion": "Should I add the speaking activity to Today's Plan now?",
              "coachMessage": "I need one answer first."
            }
            """;

        var intent = JsonSerializer.Deserialize<CoachTurnIntent>(json, Options)!;

        intent.Kind.Should().Be(CoachIntentKind.AskClarification);
        intent.AcceptanceState.Should().Be(CoachAcceptanceState.Ambiguous);
        intent.PendingSuggestionId.Should().Be("suggestion-1");
        intent.ConstraintDelta.Should().BeNull("an unclear answer must never carry a change");
    }

    [Fact]
    public void An_unknown_intent_member_is_refused()
    {
        const string json = """
            {"kind":"DirectConstraintChange","planItemIds":["item-1"]}
            """;

        var act = () => JsonSerializer.Deserialize<CoachTurnIntent>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });

        act.Should().Throw<JsonException>("the application must refuse a member that the contract does not name");
    }
}
