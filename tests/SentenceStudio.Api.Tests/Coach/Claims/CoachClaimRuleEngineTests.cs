using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// The engine: which rules exist, what each stage does, and what a repair is allowed to say.
/// </summary>
/// <remarks>
/// <para>
/// The census test is the load-bearing one. A rule that exists in the tree but never runs is
/// indistinguishable from a rule that does not exist, and it is worse than absent because a
/// reviewer reading the file believes it is protecting something. The engine builds its own set for
/// exactly this reason — a DI-resolved set can lose a rule to a missing registration and still go
/// green.
/// </para>
/// <para>
/// The stage tests encode the escalation the plan insists on: substitute first, refuse last. That
/// ordering is not politeness. A grounding layer that refuses freely gets turned off, and a
/// grounding layer that is turned off protects nothing at all.
/// </para>
/// </remarks>
public sealed class CoachClaimRuleEngineTests
{
    private static CoachClaimRuleEngine Engine(
        StubCapabilityResolver? resolver = null,
        StubCapabilityManifest? manifest = null) =>
        new(resolver ?? new StubCapabilityResolver(), manifest ?? new StubCapabilityManifest());

    // ── Census ───────────────────────────────────────────────────────────────

    /// <summary>Every code in the enum is a rule the engine actually runs.</summary>
    [Fact]
    public void Every_rule_code_has_a_registered_rule()
    {
        var expected = Enum.GetValues<CoachClaimRuleCode>()
            .Where(code => code != CoachClaimRuleCode.Unknown)
            .ToArray();

        expected.Should().HaveCount(
            10,
            "six foundation rules, the three capability rules §5.6 creates, and W8's "
            + "RepeatedDisputedClaim");

        Engine().Rules.Select(rule => rule.Code).Should().BeEquivalentTo(
            expected,
            "a rule in the enum but not in the engine is a rule a reviewer believes is protecting "
            + "something and which never runs");
    }

    [Fact]
    public void No_rule_is_registered_twice()
    {
        Engine().Rules.Select(rule => rule.Code).Should().OnlyHaveUniqueItems(
            "a duplicated rule doubles every count it contributes to a metric");
    }

    /// <summary>§5.6: none of the three capability rules existed before W6.</summary>
    [Theory]
    [InlineData(CoachClaimRuleCode.CapabilityAbsent)]
    [InlineData(CoachClaimRuleCode.FalseLimitation)]
    [InlineData(CoachClaimRuleCode.SideEffectNotDisclosed)]
    public void The_three_capability_rules_are_registered(CoachClaimRuleCode code)
    {
        Engine().Rules.Should().ContainSingle(rule => rule.Code == code);
    }

    /// <summary>
    /// The telemetry mapper is not a repair rule, and the plan says so explicitly.
    /// </summary>
    [Fact]
    public void The_unsupported_capability_opportunity_is_not_a_claim_rule()
    {
        Engine().Rules.Should().NotContain(
            rule => rule.GetType().Name.Contains("Opportunity", StringComparison.Ordinal),
            "CoachOpportunityKind.UnsupportedCapability counts what happened; it does not repair "
            + "it, and the plan says it does not satisfy AC-F2");
    }

    // ── Stage ladder ─────────────────────────────────────────────────────────

    private static CoachClaimRuleContext UnverifiedTurn() => new()
    {
        Answer = ClaimFixture.Answer("You have been practising verbs a lot lately."),
        Trace = ClaimFixture.EmptyTrace()
    };

    [Fact]
    public void Off_scans_nothing()
    {
        var outcome = Engine().Evaluate(UnverifiedTurn(), CoachGroundingStage.Off);

        outcome.HasFindings.Should().BeFalse();
        outcome.Refused.Should().BeFalse();
        outcome.Answer!.PlainText.Should().Contain("practising verbs", "the answer is untouched");
    }

    [Fact]
    public void Observe_records_and_never_alters()
    {
        var context = UnverifiedTurn();
        var original = context.Answer!.PlainText;

        var outcome = Engine().Evaluate(context, CoachGroundingStage.Observe);

        outcome.HasFindings.Should().BeTrue();
        outcome.Findings.Should().OnlyContain(
            finding => finding.Action == CoachClaimRepairAction.ObservedOnly);
        outcome.Answer!.PlainText.Should().Be(
            original,
            "Observe is the rung that runs in production for weeks producing nothing but counts");
        outcome.Refused.Should().BeFalse();
    }

    [Fact]
    public void Repair_substitutes_the_offending_span()
    {
        var outcome = Engine().Evaluate(UnverifiedTurn(), CoachGroundingStage.Repair);

        outcome.Refused.Should().BeFalse();
        outcome.Findings.Should().Contain(finding =>
            finding.Action == CoachClaimRepairAction.Substituted);

        outcome.Answer!.PlainText.Should().NotContain("practising verbs");
        outcome.Answer.PlainText.Should().Contain(CoachDeterministicCopy.UncheckedLearnerState);
    }

    /// <summary>The substitution reaches the projection too, not just the blocks.</summary>
    [Fact]
    public void Repair_rebuilds_plain_text_from_the_repaired_spans()
    {
        var outcome = Engine().Evaluate(UnverifiedTurn(), CoachGroundingStage.Repair);

        var fromBlocks = string.Join(
            " ",
            outcome.Answer!.Blocks.SelectMany(block => block.Spans).Select(span => span.Text));

        outcome.Answer.PlainText.Should().Be(
            fromBlocks,
            "several surfaces read PlainText directly; a repaired panel over an unrepaired "
            + "projection looks fixed and is not, which is worse than no repair at all");
    }

    [Fact]
    public void Repair_leaves_the_original_answer_untouched()
    {
        var context = UnverifiedTurn();
        var original = context.Answer!;

        var outcome = Engine().Evaluate(context, CoachGroundingStage.Repair);

        original.PlainText.Should().Contain(
            "practising verbs",
            "the engine rebuilds rather than mutates; a caller holding the original must still see it");
        outcome.Answer.Should().NotBeSameAs(original);
    }

    /// <summary>Substitution before refusal, asserted as an ordering rather than a hope.</summary>
    [Fact]
    public void Enforce_still_substitutes_what_substitution_can_fix()
    {
        var outcome = Engine().Evaluate(UnverifiedTurn(), CoachGroundingStage.Enforce);

        outcome.Refused.Should().BeFalse(
            "a repairable finding is repaired even at Enforce; refusal is the last resort, not the "
            + "response to any finding");
        outcome.Answer!.PlainText.Should().Contain(CoachDeterministicCopy.UncheckedLearnerState);
    }

    /// <summary>The unrepairable case: an absence has no span to replace.</summary>
    private static CoachClaimRuleContext UndisclosedWithholdTurn() => new()
    {
        Answer = ClaimFixture.Answer("Here are your words."),
        Evidence = [ClaimFixture.Evidence(withheld: 5)]
    };

    [Fact]
    public void Repair_ships_an_unrepairable_finding()
    {
        var outcome = Engine().Evaluate(UndisclosedWithholdTurn(), CoachGroundingStage.Repair);

        outcome.Refused.Should().BeFalse("Repair records what it cannot fix and still answers");
        outcome.Answer.Should().NotBeNull();
        outcome.Findings.Should().Contain(finding =>
            finding.Rule == CoachClaimRuleCode.WithheldNotDisclosed
            && finding.Action == CoachClaimRepairAction.ObservedOnly);
    }

    [Fact]
    public void Enforce_refuses_only_what_substitution_could_not_fix()
    {
        var outcome = Engine().Evaluate(UndisclosedWithholdTurn(), CoachGroundingStage.Enforce);

        outcome.Refused.Should().BeTrue();
        outcome.Answer.Should().BeNull();
        outcome.Findings.Should().Contain(finding =>
            finding.Action == CoachClaimRepairAction.Refused);
    }

    [Fact]
    public void A_clean_answer_passes_every_stage_unchanged()
    {
        foreach (var stage in Enum.GetValues<CoachGroundingStage>())
        {
            var context = new CoachClaimRuleContext
            {
                Answer = ClaimFixture.Answer("Korean has 7 speech levels."),
                Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.CompleteOwnedSet)],
                Trace = ClaimFixture.SuccessfulTrace()
            };

            var outcome = Engine().Evaluate(context, stage);

            outcome.HasFindings.Should().BeFalse("stage {0} must not invent a finding", stage);
            outcome.Refused.Should().BeFalse();
            outcome.Answer.Should().NotBeNull();
        }
    }

    // ── Findings are content-free ────────────────────────────────────────────

    /// <summary>
    /// A finding travels into logs and into the protected outcome. It must not carry the sentence.
    /// </summary>
    [Fact]
    public void A_finding_has_no_text_member()
    {
        var strings = typeof(CoachClaimFinding)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToArray();

        strings.Should().BeEmpty(
            "the moment a finding can carry the offending sentence, every log and report that "
            + "records one inherits the answer's embargo. Offending: {0}",
            string.Join(", ", strings));
    }

    [Fact]
    public void A_serialized_finding_repeats_no_answer_text()
    {
        var outcome = Engine().Evaluate(UnverifiedTurn(), CoachGroundingStage.Observe);

        var json = JsonSerializer.Serialize(outcome.Findings);

        json.Should().NotContain("practising", "the finding names a rule and a location, never a span");
        json.Should().Contain("UnverifiedLearnerStateClaim");
    }

    [Fact]
    public void Counts_by_rule_is_codes_and_numbers_only()
    {
        var context = new CoachClaimRuleContext
        {
            Answer = ClaimFixture.AnswerWith(
                "You have been practising verbs a lot lately.",
                "You don't have any nouns left."),
            Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.PageOfOwnedSet)],
            Trace = ClaimFixture.EmptyTrace()
        };

        var counts = Engine().Evaluate(context, CoachGroundingStage.Observe).CountsByRule;

        counts.Should().NotBeEmpty();
        counts.Keys.Should().OnlyContain(code => Enum.IsDefined(code));
        counts.Values.Should().OnlyContain(count => count > 0);
    }

    // ── Repair copy is deterministic and count-free ──────────────────────────

    /// <summary>
    /// A repair that stated a number would be the original defect wearing the fix's clothes.
    /// </summary>
    [Theory]
    [InlineData(nameof(CoachDeterministicCopy.UncheckedLearnerState))]
    [InlineData(nameof(CoachDeterministicCopy.PartialCoverageNegative))]
    [InlineData(nameof(CoachDeterministicCopy.NoReadHappened))]
    [InlineData(nameof(CoachDeterministicCopy.UnrankedResult))]
    [InlineData(nameof(CoachDeterministicCopy.UnsupportedCount))]
    [InlineData(nameof(CoachDeterministicCopy.CapableAfterAll))]
    public void Repair_copy_is_count_free(string name)
    {
        var field = typeof(CoachDeterministicCopy).GetField(name, BindingFlags.Public | BindingFlags.Static);

        field.Should().NotBeNull();

        var value = (string)field!.GetRawConstantValue()!;

        value.Should().NotBeNullOrWhiteSpace();
        value.Should().NotMatchRegex(@"\d");
        value.Should().NotContain("{", "an interpolation placeholder is a count in disguise");
    }

    /// <summary>
    /// Every substituted finding puts a known constant in place, never generated prose.
    /// </summary>
    /// <remarks>
    /// Counted by distinct span rather than by finding, because two rules can fire on one sentence
    /// and one sentence gets one replacement. See
    /// <see cref="Two_rules_on_one_span_produce_one_substitution"/> for why that is the right
    /// behaviour rather than an accident of the dictionary.
    /// </remarks>
    [Fact]
    public void Every_substitution_is_a_deterministic_constant()
    {
        var known = typeof(CoachDeterministicCopy)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false })
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var context = new CoachClaimRuleContext
        {
            Answer = ClaimFixture.AnswerWith(
                "You have been practising verbs a lot lately.",
                "Your most-practised resource is the news reader."),
            Evidence = [ClaimFixture.Evidence(order: CoachEvidenceOrder.Unordered)],
            Trace = ClaimFixture.EmptyTrace()
        };

        var outcome = Engine().Evaluate(context, CoachGroundingStage.Repair);

        var replacedSpans = outcome.Answer!.Blocks
            .SelectMany(block => block.Spans)
            .Select(span => span.Text)
            .Where(text => known.Contains(text))
            .ToArray();

        replacedSpans.Should().NotBeEmpty("the fixture is built to trigger substitutions");

        var substitutedFindings = outcome.Findings
            .Where(finding => finding.Action == CoachClaimRepairAction.Substituted)
            .ToArray();

        substitutedFindings.Should().NotBeEmpty();

        substitutedFindings
            .Select(finding => (finding.BlockIndex, finding.SpanIndex))
            .Distinct()
            .Should().HaveCount(
                replacedSpans.Length,
                "every span reported as substituted is a span that actually holds a constant now");
    }

    /// <summary>
    /// Two rules on one sentence produce one replacement, deterministically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Your most-practised resource is the news reader" is both an unverified learner-state claim
    /// and an order claim over an unordered read. Substituting twice would mean replacing a
    /// replacement — the second rule would overwrite honest deterministic copy with different
    /// honest deterministic copy, and which one survived would depend on iteration order.
    /// </para>
    /// <para>
    /// One replacement per span, chosen by the engine's fixed rule order. Both findings are still
    /// reported, because the metric should show that two rules fired even though one sentence
    /// changed.
    /// </para>
    /// </remarks>
    [Fact]
    public void Two_rules_on_one_span_produce_one_substitution()
    {
        var context = new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Your most-practised resource is the news reader."),
            Evidence = [ClaimFixture.Evidence(order: CoachEvidenceOrder.Unordered)],
            Trace = ClaimFixture.EmptyTrace()
        };

        var outcome = Engine().Evaluate(context, CoachGroundingStage.Repair);

        outcome.Findings.Select(finding => finding.Rule).Should().Contain(
            [CoachClaimRuleCode.UnverifiedLearnerStateClaim, CoachClaimRuleCode.OrderClaimMismatch],
            "the sentence breaks both rules and both are worth counting");

        outcome.Answer!.Blocks.Should().ContainSingle()
            .Which.Spans.Should().ContainSingle()
            .Which.Text.Should().Be(
                CoachDeterministicCopy.UncheckedLearnerState,
                "the first rule in the engine's fixed order wins, so the result is deterministic "
                + "rather than dependent on which rule happened to run last");
    }

    // ── The stage ladder is ordered ──────────────────────────────────────────

    [Fact]
    public void The_stage_ladder_is_ordered_by_severity()
    {
        ((int)CoachGroundingStage.Off).Should().BeLessThan((int)CoachGroundingStage.Observe);
        ((int)CoachGroundingStage.Observe).Should().BeLessThan((int)CoachGroundingStage.Repair);
        ((int)CoachGroundingStage.Repair).Should().BeLessThan((int)CoachGroundingStage.Enforce);

        Enum.GetValues<CoachGroundingStage>().Should().HaveCount(
            4,
            "B9 names exactly four: Off, Observe, Repair, Enforce. A fifth would need a decision "
            + "about where it sits in the comparison");
    }
}
