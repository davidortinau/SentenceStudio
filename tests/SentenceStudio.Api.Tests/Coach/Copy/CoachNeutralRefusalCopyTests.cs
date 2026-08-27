using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Opportunities.Detection;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Copy;

/// <summary>
/// Proves the neutral refusal copy rules introduced by Wash defect 6 fix.
/// Every changed refusal site must use neutral copy for non-plan intents
/// and plan-specific copy only for actual plan mutations.
/// </summary>
public class CoachNeutralRefusalCopyTests
{
    // ─── A7: General pedagogical/learner-state validation failure uses neutral copy ───

    [Theory]
    [InlineData(CoachIntentKind.NoChange)]
    [InlineData(CoachIntentKind.PedagogicalAnswer)]
    [InlineData(CoachIntentKind.AskClarification)]
    [InlineData(CoachIntentKind.OffTopic)]
    public void Non_plan_intents_produce_neutral_copy(CoachIntentKind kind)
    {
        var result = CoachDeterministicCopy.ValidationFailedNotice(kind);

        result.Should().Be(CoachDeterministicCopy.ValidationFailedNeutral);
        result.Should().NotContain("Plan", because: "non-plan intents must not mention the plan");
    }

    // ─── A7: Actual plan mutation retains plan-specific unchanged copy ───

    [Theory]
    [InlineData(CoachIntentKind.DirectConstraintChange)]
    [InlineData(CoachIntentKind.SuggestConstraintChange)]
    [InlineData(CoachIntentKind.AcceptPendingSuggestion)]
    [InlineData(CoachIntentKind.RejectPendingSuggestion)]
    public void Plan_mutation_intents_produce_plan_specific_copy(CoachIntentKind kind)
    {
        var result = CoachDeterministicCopy.ValidationFailedNotice(kind);

        result.Should().Be(CoachDeterministicCopy.NoChange);
        result.Should().Contain("Plan", because: "plan intents get plan-specific copy");
    }

    // ─── A7: Null intent defaults to neutral ───

    [Fact]
    public void Null_intent_produces_neutral_copy()
    {
        var result = CoachDeterministicCopy.ValidationFailedNotice(null);

        result.Should().Be(CoachDeterministicCopy.ValidationFailedNeutral);
    }

    // ─── A7: Neutral constants are well-formed ───

    [Fact]
    public void ValidationFailedNeutral_does_not_mention_plan()
    {
        CoachDeterministicCopy.ValidationFailedNeutral
            .Should().NotContainEquivalentOf("plan");
    }

    [Fact]
    public void IncompleteNeutral_does_not_mention_plan()
    {
        CoachDeterministicCopy.IncompleteNeutral
            .Should().NotContainEquivalentOf("plan");
    }

    [Fact]
    public void NoChange_copy_mentions_plan()
    {
        CoachDeterministicCopy.NoChange
            .Should().ContainEquivalentOf("plan",
                because: "plan-specific copy is deliberately about the plan");
    }

    // ─── A7: IsSettingsChange helper correctness ───

    [Theory]
    [InlineData(CoachIntentKind.DirectConstraintChange, true)]
    [InlineData(CoachIntentKind.SuggestConstraintChange, true)]
    [InlineData(CoachIntentKind.AcceptPendingSuggestion, true)]
    [InlineData(CoachIntentKind.RejectPendingSuggestion, true)]
    [InlineData(CoachIntentKind.NoChange, false)]
    [InlineData(CoachIntentKind.PedagogicalAnswer, false)]
    [InlineData(CoachIntentKind.AskClarification, false)]
    [InlineData(CoachIntentKind.OffTopic, false)]
    public void IsSettingsChange_classifies_intents_correctly(CoachIntentKind kind, bool expected)
    {
        CoachActionIntent.IsSettingsChange(kind).Should().Be(expected);
    }

    [Fact]
    public void IsSettingsChange_null_returns_false()
    {
        CoachActionIntent.IsSettingsChange(null).Should().BeFalse();
    }
}
