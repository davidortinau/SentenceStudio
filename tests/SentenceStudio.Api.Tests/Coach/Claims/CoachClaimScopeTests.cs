using System.Reflection;
using FluentAssertions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// Which spans a claim rule may read: Display, outside Example, Form and Contrast.
/// </summary>
/// <remarks>
/// <para>
/// This is the boundary between auditing the coach and censoring the lesson. An <c>Example</c>
/// block containing "you don't have any brothers" as a sample sentence is an unbounded negative
/// about the learner by every syntactic measure this code has, and it is not a claim at all — it is
/// the thing the learner is here to read.
/// </para>
/// <para>
/// The exclusion is coarse on purpose. Parsing intent would be more precise on the easy cases and
/// wrong in the dangerous direction on the hard ones; excluding by block kind cannot be.
/// </para>
/// </remarks>
public sealed class CoachClaimScopeTests
{
    private const string LearnerClaim = "You have been practising verbs a lot lately.";

    /// <summary>
    /// The full matrix: every block kind against every language role.
    /// </summary>
    /// <remarks>
    /// Generated rather than listed, so a new block kind or language role is covered the day it is
    /// added instead of the day somebody remembers to extend a list.
    /// </remarks>
    public static TheoryData<CoachAnswerBlockKind, CoachLanguageRole, bool> Matrix()
    {
        var data = new TheoryData<CoachAnswerBlockKind, CoachLanguageRole, bool>();

        foreach (var kind in Enum.GetValues<CoachAnswerBlockKind>())
        {
            foreach (var language in Enum.GetValues<CoachLanguageRole>())
            {
                var scannable = language == CoachLanguageRole.Display
                    && kind is not (CoachAnswerBlockKind.Example
                        or CoachAnswerBlockKind.Form
                        or CoachAnswerBlockKind.Contrast);

                data.Add(kind, language, scannable);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void The_scope_matrix_holds(
        CoachAnswerBlockKind kind,
        CoachLanguageRole language,
        bool expected)
    {
        CoachClaimScope.IsScannable(kind, language).Should().Be(expected);

        var spans = CoachClaimScope.Scannable(ClaimFixture.Answer(LearnerClaim, kind, language));

        spans.Should().HaveCount(
            expected ? 1 : 0,
            "block {0} in language role {1} should {2} be scanned",
            kind,
            language,
            expected ? string.Empty : "not");
    }

    /// <summary>The matrix must actually contain both outcomes, or it proves nothing.</summary>
    [Fact]
    public void The_matrix_covers_both_outcomes_and_every_member()
    {
        var rows = Matrix().Select(row => row.ToArray()).ToArray();

        var blockKinds = Enum.GetValues<CoachAnswerBlockKind>().Length;
        var languages = Enum.GetValues<CoachLanguageRole>().Length;

        rows.Should().HaveCount(blockKinds * languages, "every combination is exercised");
        rows.Should().Contain(row => (bool)row[2]!, "a matrix with no scannable row is vacuous");
        rows.Should().Contain(row => !(bool)row[2]!, "a matrix with no excluded row is vacuous");
    }

    [Fact]
    public void The_excluded_set_is_exactly_example_form_and_contrast()
    {
        CoachClaimScope.ExcludedBlockKinds.Should().BeEquivalentTo(
            [CoachAnswerBlockKind.Example, CoachAnswerBlockKind.Form, CoachAnswerBlockKind.Contrast],
            "the plan names these three. Adding a fourth is a decision about what the coach is "
            + "allowed to say unaudited");
    }

    /// <summary>
    /// The concrete case the exclusion exists for: a sample sentence that looks like a claim.
    /// </summary>
    [Theory]
    [InlineData(CoachAnswerBlockKind.Example)]
    [InlineData(CoachAnswerBlockKind.Form)]
    [InlineData(CoachAnswerBlockKind.Contrast)]
    public void No_rule_fires_inside_an_excluded_block(CoachAnswerBlockKind kind)
    {
        var engine = new CoachClaimRuleEngine(new StubCapabilityResolver(), new StubCapabilityManifest());

        var context = new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(
                "You don't have any brothers, and you have 42 books.",
                kind),
            Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.PageOfOwnedSet, matched: 7)],
            Trace = ClaimFixture.EmptyTrace()
        };

        engine.Scan(context).Should().BeEmpty(
            "a {0} block is teaching material; running claim rules over it is how a grounding "
            + "layer starts deleting the lesson",
            kind);
    }

    /// <summary>The same sentence in an Answer block is a claim, and fires.</summary>
    [Fact]
    public void The_same_sentence_in_an_answer_block_does_fire()
    {
        var engine = new CoachClaimRuleEngine(new StubCapabilityResolver(), new StubCapabilityManifest());

        var context = new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(
                "You don't have any brothers, and you have 42 books.",
                CoachAnswerBlockKind.Answer),
            Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.PageOfOwnedSet, matched: 7)],
            Trace = ClaimFixture.EmptyTrace()
        };

        engine.Scan(context).Should().NotBeEmpty(
            "this is the control for the exclusion test above: if it were empty here too, the "
            + "exclusion test would be passing for the wrong reason");
    }

    [Fact]
    public void A_null_answer_yields_no_spans()
    {
        CoachClaimScope.Scannable(null).Should().BeEmpty();
    }

    [Fact]
    public void Blank_spans_are_skipped()
    {
        var answer = ClaimFixture.AnswerWith("   ", LearnerClaim);

        CoachClaimScope.Scannable(answer).Should().ContainSingle()
            .Which.SpanIndex.Should().Be(1, "the blank span is skipped and the index still locates the real one");
    }

    [Fact]
    public void Span_coordinates_locate_the_span()
    {
        var answer = ClaimFixture.AnswerWith("First.", "Second.", "Third.");

        var spans = CoachClaimScope.Scannable(answer);

        spans.Select(span => span.SpanIndex).Should().Equal([0, 1, 2]);
        spans.Should().OnlyContain(span => span.BlockIndex == 0);
    }
}

/// <summary>
/// The shadow router exists, and no rule can see it. Plan D4 and B5.
/// </summary>
/// <remarks>
/// <para>
/// D4 permits an optional router in Shadow only. B5 forbids a rule from reading a router label. The
/// two together mean the router must be provably inert, and "provably" is doing real work here: a
/// router that gates rule execution can silence a rule by mislabelling a turn, and the model is the
/// thing under audit.
/// </para>
/// <para>
/// The strongest form of the proof is structural — the rule context has no member a label could
/// occupy — and the equivalence test is the behavioural backstop. If either fails, the correct
/// response is to delete the router, which is why it lives in one removable file.
/// </para>
/// </remarks>
public sealed class CoachShadowRouterTests
{
    private static CoachClaimRuleEngine Engine() =>
        new(new StubCapabilityResolver().Declare("set_theme", CoachCapabilityAvailability.Present),
            new StubCapabilityManifest().Declare("apply_plan_change", CoachCapabilityEffectClass.LearnerData));

    /// <summary>
    /// One context per rule, so the equivalence test below is a sweep rather than a spot check.
    /// </summary>
    private static IReadOnlyList<(CoachClaimRuleCode Rule, CoachClaimRuleContext Context)> OneTurnPerRule() =>
    [
        (CoachClaimRuleCode.UnverifiedLearnerStateClaim, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You have been practising verbs a lot lately."),
            Trace = ClaimFixture.EmptyTrace()
        }),
        (CoachClaimRuleCode.NegativeClaimWithoutCoverage, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You don't have any verbs to review."),
            Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.PageOfOwnedSet)]
        }),
        (CoachClaimRuleCode.FabricatedCheck, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I checked your vocabulary."),
            Trace = ClaimFixture.FailedTrace()
        }),
        (CoachClaimRuleCode.OrderClaimMismatch, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Your most-practised resource is the news reader."),
            Evidence = [ClaimFixture.Evidence(order: CoachEvidenceOrder.Unordered)]
        }),
        (CoachClaimRuleCode.CountClaimMismatch, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You have 42 words due this week."),
            Evidence = [ClaimFixture.Evidence(matched: 84, returned: 20)]
        }),
        (CoachClaimRuleCode.WithheldNotDisclosed, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Here are your words."),
            Evidence = [ClaimFixture.Evidence(withheld: 5)]
        }),
        (CoachClaimRuleCode.CapabilityAbsent, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I'll do that for you."),
            ProposedCapabilities = ["apply_plan_change"]
        }),
        (CoachClaimRuleCode.FalseLimitation, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I can't change your theme."),
            ProposedCapabilities = ["set_theme"]
        }),
        (CoachClaimRuleCode.SideEffectNotDisclosed, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Sounds good."),
            ProposedCapabilities = ["apply_plan_change"]
        }),

        // W8. An open dispute plus an answer that re-states the disputed claim from the same read:
        // Case D, which is the shape the rule was written for.
        (CoachClaimRuleCode.RepeatedDisputedClaim, new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You definitely have twelve words due this week."),
            Trace = ClaimFixture.SuccessfulTrace(),
            Dispute = new SentenceStudio.Api.Coach.Persistence.History.CoachTurnDisputeState(
                SentenceStudio.Api.Coach.Application.CoachCorrectionSignal.NotWhatIAsked,
                "3f1c9a44-0d3e-4c1b-9a5e-77b2c1d0e912",
                new DateTime(2026, 8, 22, 2, 5, 0, DateTimeKind.Utc),
                ResolvedAtUtc: null,
                SentenceStudio.Api.Coach.Persistence.History.CoachDisputeResolution.Open,
                [SentenceStudio.Api.Coach.Tools.CoachScopeDefinition.TrackedVocabularyDueSummary])
        })
    ];

    /// <summary>The sweep is non-vacuous: one turn per rule, and each one fires its rule.</summary>
    [Fact]
    public void Every_rule_fires_with_the_router_absent()
    {
        var engine = Engine();
        var turns = OneTurnPerRule();

        turns.Should().HaveCount(
            Enum.GetValues<CoachClaimRuleCode>().Length - 1,
            "one fixture per rule; a rule with no fixture is a rule this sweep does not cover");

        foreach (var (rule, context) in turns)
        {
            engine.Scan(context).Select(finding => finding.Rule).Should().Contain(
                rule,
                "{0} must fire from its own fixture with no router in the picture at all",
                rule);
        }
    }

    /// <summary>The equivalence test. A label changes nothing.</summary>
    [Fact]
    public void Every_rule_fires_identically_with_the_router_present()
    {
        var engine = Engine();
        var router = new CoachShadowClaimRouter();

        foreach (var (rule, context) in OneTurnPerRule())
        {
            var withoutRouter = engine.Scan(context);

            // Classifying is the entire extent of what the router does on the turn path. If this
            // call could affect the scan, that would be the bug.
            var label = router.Classify(context);

            var withRouter = engine.Scan(context);

            withRouter.Should().BeEquivalentTo(
                withoutRouter,
                "{0} produced a different result once the router had labelled the turn as {1}. "
                + "B5 forbids a rule from reading a router label, and the correct response to this "
                + "failure is to delete the router rather than to fix it",
                rule,
                label);
        }
    }

    /// <summary>
    /// The structural proof: there is nowhere on the context for a label to live.
    /// </summary>
    [Fact]
    public void The_rule_context_exposes_no_router_label()
    {
        var members = typeof(CoachClaimRuleContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        members.Should().NotBeEmpty();

        foreach (var forbidden in new[] { "Route", "Router", "Label", "Intent", "Classification" })
        {
            members.Should().NotContain(
                member => member.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "B5: honesty rules trigger on claim shape and on the trace, never on a router "
                + "label. A member named for {0} would make that a convention instead of a fact",
                forbidden);
        }
    }

    [Fact]
    public void No_rule_type_references_the_router()
    {
        var ruleTypes = Engine().Rules.Select(rule => rule.GetType());

        foreach (var type in ruleTypes)
        {
            var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(field => field.FieldType.Name)
                .ToArray();

            fields.Should().NotContain(
                name => name.Contains("Router", StringComparison.Ordinal),
                "{0} holds a router reference; a rule that can consult a label can be silenced by "
                + "one",
                type.Name);
        }
    }

    [Fact]
    public void The_router_labels_without_influencing()
    {
        var router = new CoachShadowClaimRouter();

        router.Classify(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You have been practising verbs."),
            Trace = ClaimFixture.EmptyTrace()
        }).Should().Be(CoachShadowRouteLabel.LearnerState);

        router.Classify(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Sounds good."),
            ProposedCapabilities = ["set_theme"]
        }).Should().Be(CoachShadowRouteLabel.CapabilityProposal);

        router.Classify(new CoachClaimRuleContext()).Should().Be(
            CoachShadowRouteLabel.Unknown,
            "no answer, no label; the router never guesses");
    }
}
