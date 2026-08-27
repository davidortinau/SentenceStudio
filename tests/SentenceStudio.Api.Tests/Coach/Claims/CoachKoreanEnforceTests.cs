using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// F1: Enforce refuses a Korean answer. The two axes are stage and substitution, not one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these close.</b> A Korean answer at Enforce was evaluated "one rung down" as
/// Observe, because substitution could not run in a language the repair constants are not written
/// in. The engine's refusal test reads the stage it was handed, so downgrading the stage disabled
/// refusal — for the majority learner population, on the rung whose entire job is refusing. The
/// comment above the code said Enforce still refuses. The code disagreed, and the only coverage
/// tested the language predicate in isolation, so the contradiction survived a review.
/// </para>
/// <para>
/// <b>What replaces it.</b> Refusal comes from the real stage; substitution comes from a policy
/// flag; and a finding whose substitute was withheld for language is distinguished from one that
/// never had a substitute. Only the second refuses. So a Korean turn carrying nothing but
/// substitutable findings ships unaltered with the suppression recorded, and a Korean turn carrying
/// a structural finding refuses exactly as an English one would.
/// </para>
/// <para>
/// <b>The other half of the job is not firing.</b> Korean teaching content is full of numerals,
/// dates and times, and a claim rule that mistook "칠십" or "3시" for a claim about the learner
/// would refuse lessons. Those fixtures are here rather than in the classifier tests because the
/// rung is what makes the consequence severe.
/// </para>
/// </remarks>
public sealed class CoachKoreanEnforceTests
{
    private static CoachClaimRuleEngine Engine() =>
        new(new StubCapabilityResolver(), new StubCapabilityManifest());

    private static CoachAnswerDto Answer(string text, string displayLanguageTag) => new()
    {
        Topic = CoachAnswerTopic.Vocabulary,
        Blocks =
        [
            new CoachAnswerBlockDto
            {
                Kind = CoachAnswerBlockKind.Answer,
                Spans =
                [
                    new CoachAnswerSpanDto
                    {
                        Text = text,
                        Language = CoachLanguageRole.Display,
                        LanguageTag = displayLanguageTag
                    }
                ]
            }
        ],
        PlainText = text,
        TargetLanguageTag = "ko",
        DisplayLanguageTag = displayLanguageTag
    };

    /// <summary>A structural finding: rows withheld and the answer does not say so. No substitute.</summary>
    private static CoachClaimRuleContext StructuralFinding(string displayLanguageTag) => new()
    {
        Answer = Answer("Here are your words.", displayLanguageTag),
        Evidence = [ClaimFixture.Evidence(withheld: 5)]
    };

    /// <summary>A substitutable finding: an unverified learner-state claim on a span.</summary>
    private static CoachClaimRuleContext SubstitutableFinding(string displayLanguageTag) => new()
    {
        Answer = Answer("You have been practising verbs a lot lately.", displayLanguageTag),
        Trace = ClaimFixture.EmptyTrace()
    };

    // ── The fix ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The regression itself. A Korean learner at Enforce gets the same refusal an English one does.
    /// </summary>
    [Fact]
    public void Enforce_refuses_a_structural_finding_in_korean()
    {
        var outcome = Engine().Evaluate(
            StructuralFinding("ko-KR"), CoachGroundingStage.Enforce, substitutionAllowed: false);

        outcome.Refused.Should().BeTrue(
            "refusal carries no new copy \u2014 it takes the notice path the shape validator has "
            + "always used, which is already localized on the client. A language that blocks "
            + "substitution has no bearing on it");

        outcome.Answer.Should().BeNull();
        outcome.Findings.Should().Contain(finding =>
            finding.Action == CoachClaimRepairAction.Refused);
    }

    /// <summary>The English control, so the Korean case above is a comparison and not a hope.</summary>
    [Fact]
    public void Enforce_refuses_the_same_finding_in_english()
    {
        var outcome = Engine().Evaluate(
            StructuralFinding("en-US"), CoachGroundingStage.Enforce, substitutionAllowed: true);

        outcome.Refused.Should().BeTrue();
    }

    /// <summary>
    /// A Korean turn whose findings are all substitutable ships, unaltered, with the suppression on
    /// the record.
    /// </summary>
    /// <remarks>
    /// The ceremony's carve-out, and the reason suppression is not severity. The prose the learner
    /// reads is the model's own — honest enough that the rules only wanted to soften it — so taking
    /// the whole turn away over copy that could not be localized would cost the learner more than
    /// the finding did.
    /// </remarks>
    [Fact]
    public void Enforce_ships_a_korean_answer_whose_findings_were_all_substitutable()
    {
        var outcome = Engine().Evaluate(
            SubstitutableFinding("ko-KR"), CoachGroundingStage.Enforce, substitutionAllowed: false);

        outcome.Refused.Should().BeFalse(
            "a withheld substitute is a language fact, not a severity one");

        outcome.Answer.Should().NotBeNull();
        outcome.Answer!.PlainText.Should().Contain(
            "practising verbs",
            "the answer ships unaltered: no English constant was written into a Korean answer");

        outcome.HasFindings.Should().BeTrue("the finding is still recorded in full");
        outcome.Findings.Should().OnlyContain(finding =>
            finding.Action == CoachClaimRepairAction.ObservedOnly);
    }

    /// <summary>The same turn in English is substituted, which is the control for the case above.</summary>
    [Fact]
    public void Enforce_substitutes_the_same_finding_in_english()
    {
        var outcome = Engine().Evaluate(
            SubstitutableFinding("en-US"), CoachGroundingStage.Enforce, substitutionAllowed: true);

        outcome.Refused.Should().BeFalse();
        outcome.Answer!.PlainText.Should().Contain(CoachDeterministicCopy.UncheckedLearnerState);
    }

    /// <summary>Repair evaluates and records in Korean, and alters nothing.</summary>
    [Fact]
    public void Repair_records_without_altering_a_korean_answer()
    {
        var outcome = Engine().Evaluate(
            SubstitutableFinding("ko-KR"), CoachGroundingStage.Repair, substitutionAllowed: false);

        outcome.Refused.Should().BeFalse();
        outcome.HasFindings.Should().BeTrue(
            "promoting a Korean deployment to Repair must produce the same measurements Observe "
            + "would, which is the honest state of affairs until the repair sentences exist as "
            + "client resource keys");
        outcome.Answer!.PlainText.Should().Contain("practising verbs");
    }

    /// <summary>Repair never refuses, in any language. That is Enforce's job alone.</summary>
    [Theory]
    [InlineData("ko-KR", false)]
    [InlineData("en-US", true)]
    public void Repair_never_refuses_a_structural_finding(string tag, bool substitutionAllowed)
    {
        Engine().Evaluate(StructuralFinding(tag), CoachGroundingStage.Repair, substitutionAllowed)
            .Refused.Should().BeFalse();
    }

    /// <summary>Observe is unchanged by the policy, in either language.</summary>
    [Theory]
    [InlineData("ko-KR", false)]
    [InlineData("en-US", true)]
    public void Observe_is_unchanged_by_the_substitution_policy(string tag, bool substitutionAllowed)
    {
        var outcome = Engine().Evaluate(
            SubstitutableFinding(tag), CoachGroundingStage.Observe, substitutionAllowed);

        outcome.Refused.Should().BeFalse();
        outcome.Answer!.PlainText.Should().Contain("practising verbs");
        outcome.Findings.Should().OnlyContain(finding =>
            finding.Action == CoachClaimRepairAction.ObservedOnly);
    }

    [Theory]
    [InlineData("ko-KR", false)]
    [InlineData("en-US", true)]
    public void Off_scans_nothing_regardless_of_the_policy(string tag, bool substitutionAllowed)
    {
        Engine().Evaluate(StructuralFinding(tag), CoachGroundingStage.Off, substitutionAllowed)
            .HasFindings.Should().BeFalse();
    }

    // ── The collapse, as an explicit comparison ──────────────────────────────

    /// <summary>
    /// The old behaviour, reproduced, so the difference is visible rather than argued.
    /// </summary>
    /// <remarks>
    /// This is what the previous code did: downgrade the stage when substitution could not run.
    /// Passing Observe with a structural finding ships the answer; passing the real Enforce refuses
    /// it. Same context, same policy, one argument different.
    /// </remarks>
    [Fact]
    public void The_old_collapse_would_have_shipped_what_enforce_refuses()
    {
        var context = StructuralFinding("ko-KR");

        var collapsed = Engine().Evaluate(
            context, CoachGroundingStage.Observe, substitutionAllowed: false);

        var correct = Engine().Evaluate(
            context, CoachGroundingStage.Enforce, substitutionAllowed: false);

        collapsed.Refused.Should().BeFalse("Observe never refuses \u2014 that was the whole bug");
        correct.Refused.Should().BeTrue("Enforce refuses, and the language does not change that");
    }

    /// <summary>The language predicate still suppresses, which is the half that was correct.</summary>
    [Theory]
    [InlineData("ko-KR", true)]
    [InlineData("ko", true)]
    [InlineData("", true)]
    [InlineData("ja-JP", true)]
    [InlineData("english", true)]
    [InlineData("en-US", false)]
    [InlineData("en", false)]
    [InlineData("en-GB", false)]
    public void The_language_predicate_is_unchanged(string tag, bool expectedSuppression)
    {
        CoachTurnGroundingEvaluator
            .SuppressRepairForLanguage(CoachGroundingStage.Enforce, Answer("x", tag))
            .Should().Be(
                expectedSuppression,
                "an answer that does not say what language it is in is not evidence that it is in "
                + "English");
    }

    [Theory]
    [InlineData(CoachGroundingStage.Off)]
    [InlineData(CoachGroundingStage.Observe)]
    public void Suppression_is_irrelevant_below_repair(CoachGroundingStage stage)
    {
        CoachTurnGroundingEvaluator
            .SuppressRepairForLanguage(stage, Answer("x", "ko-KR"))
            .Should().BeFalse("nothing is substituted below Repair, so nothing is suppressed");
    }

    // ── Korean teaching content must not fire ────────────────────────────────

    /// <summary>
    /// Numerals, dates and times in Korean lessons. None of these is a claim about the learner.
    /// </summary>
    /// <remarks>
    /// Korean teaching content is dense with numbers — two counting systems, sino-Korean dates,
    /// native-Korean hours — and a count rule that mistook them for learner-state claims would
    /// refuse lessons at Enforce. The consequence is what puts these fixtures at this rung rather
    /// than beside the classifier: a false positive here does not soften a sentence, it deletes a
    /// turn.
    /// </remarks>
    [Theory]
    [InlineData("Korean has two number systems: \uC0AC\uC694 and \uACE0\uC720\uC5B4.")]
    [InlineData("3\uC2DC 30\uBD84 means half past three.")]
    [InlineData("\uCE60\uC2ED is 70 in sino-Korean.")]
    [InlineData("2026\uB144 8\uC6D4 22\uC77C is how a date is written.")]
    [InlineData("The counter \uAC1C is used for 12 items.")]
    [InlineData("\uD558\uB098, \uB458, \uC14B counts to 3 in native Korean.")]
    [InlineData("Use \uBA87 \uC0B4 to ask an age, and 40 would answer it.")]
    public void Korean_teaching_content_does_not_fire_at_enforce(string text)
    {
        var context = new CoachClaimRuleContext
        {
            Answer = Answer(text, "en-US"),
            Evidence = [ClaimFixture.Evidence(matched: 84, returned: 20)],
            Trace = ClaimFixture.SuccessfulTrace()
        };

        var outcome = Engine().Evaluate(context, CoachGroundingStage.Enforce);

        outcome.HasFindings.Should().BeFalse(
            "a number in a lesson is a fact about the language. B6 forbids a digit ban precisely so "
            + "the coach can still teach, and at Enforce a false positive deletes the turn");
        outcome.Refused.Should().BeFalse();
    }

    /// <summary>The same content with a Korean display tag is equally safe.</summary>
    [Theory]
    [InlineData("\uD55C\uAD6D\uC5B4\uC5D0\uB294 \uC22B\uC790 \uCCB4\uACC4\uAC00 2\uAC1C \uC788\uC5B4\uC694.")]
    [InlineData("3\uC2DC 30\uBD84\uC740 \uC138 \uC2DC \uBC18\uC774\uC5D0\uC694.")]
    [InlineData("\uCE60\uC2ED\uC740 70\uC774\uC5D0\uC694.")]
    public void Korean_display_teaching_content_does_not_fire_at_enforce(string text)
    {
        var context = new CoachClaimRuleContext
        {
            Answer = Answer(text, "ko-KR"),
            Evidence = [ClaimFixture.Evidence(matched: 84)],
            Trace = ClaimFixture.SuccessfulTrace()
        };

        var outcome = Engine().Evaluate(
            context, CoachGroundingStage.Enforce, substitutionAllowed: false);

        outcome.HasFindings.Should().BeFalse();
        outcome.Refused.Should().BeFalse();
    }

    /// <summary>
    /// The control: a real learner-state claim in the same answer shape still fires.
    /// </summary>
    /// <remarks>
    /// Without this, every test above could be passing because the engine had been switched off.
    /// </remarks>
    [Fact]
    public void A_real_learner_claim_in_the_same_shape_still_fires()
    {
        var context = new CoachClaimRuleContext
        {
            Answer = Answer("You have 42 words due this week.", "en-US"),
            Evidence = [ClaimFixture.Evidence(matched: 84, returned: 20)],
            Trace = ClaimFixture.SuccessfulTrace()
        };

        Engine().Evaluate(context, CoachGroundingStage.Enforce).HasFindings.Should().BeTrue(
            "the teaching fixtures above are only meaningful if the engine was awake for them");
    }

    // ── End to end, through the evaluator ────────────────────────────────────

    /// <summary>
    /// The evaluator hands the engine the real stage, not a downgraded one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this test exists separately from the engine ones above.</b> The engine tests call
    /// <c>Evaluate(context, stage, substitutionAllowed)</c> directly and prove the engine is right.
    /// The collapse never lived in the engine — it lived one layer up, in what the evaluator chose
    /// to pass. A suite that only tested the engine would go green against a caller that still
    /// downgraded the stage, which is precisely the shape of the original defect: the predicate was
    /// tested in isolation and the rung was never exercised end to end.
    /// </para>
    /// <para>
    /// A mutation confirmed the gap. Restoring the collapse in the evaluator left every engine test
    /// passing.
    /// </para>
    /// </remarks>
    private static CoachTurnGroundingEvaluator Evaluator() =>
        new(Engine(),
            new StubCapabilityResolver(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    private static CoachTurnGroundingResult RunEvaluator(
        CoachGroundingStage stage,
        CoachAnswerDto answer,
        IReadOnlyList<CoachEvidenceDto> evidence) =>
        Evaluator().Evaluate(
            stage,
            answer,
            evidence,
            observations: null,
            proposedCapabilities: [],
            capabilityStage: CoachCapabilityStage.Off,
            handshake: null);

    [Fact]
    public void The_evaluator_refuses_a_korean_structural_finding_at_enforce()
    {
        var result = RunEvaluator(
            CoachGroundingStage.Enforce,
            Answer("Here are your words.", "ko-KR"),
            [ClaimFixture.Evidence(withheld: 5)]);

        result.Refused.Should().BeTrue(
            "the evaluator must hand the engine the rung the operator promoted to. Downgrading it "
            + "when substitution cannot run is the F1 defect, and it disabled refusal for every "
            + "Korean learner");

        result.Record!.Stage.Should().Be(
            CoachGroundingStage.Enforce, "the record carries the requested rung, never a collapsed one");
        result.Record.RepairSuppressedForLanguage.Should().BeTrue();
    }

    [Fact]
    public void The_evaluator_ships_a_korean_substitutable_finding_unaltered_at_enforce()
    {
        var result = RunEvaluator(
            CoachGroundingStage.Enforce,
            Answer("You have been practising verbs a lot lately.", "ko-KR"),
            []);

        result.Refused.Should().BeFalse();
        result.Answer!.PlainText.Should().Contain(
            "practising verbs",
            "no English constant may be written into a Korean answer");
        result.Record!.RepairSuppressedForLanguage.Should().BeTrue();
        result.Record.AnswerAltered.Should().BeFalse();
    }

    /// <summary>The English control through the same path.</summary>
    [Fact]
    public void The_evaluator_refuses_an_english_structural_finding_at_enforce()
    {
        var result = RunEvaluator(
            CoachGroundingStage.Enforce,
            Answer("Here are your words.", "en-US"),
            [ClaimFixture.Evidence(withheld: 5)]);

        result.Refused.Should().BeTrue();
        result.Record!.RepairSuppressedForLanguage.Should().BeFalse();
    }

    /// <summary>The evaluator projects the durable summary with the requested rung intact.</summary>
    [Fact]
    public void The_evaluator_projects_a_summary_carrying_the_requested_rung()
    {
        var result = RunEvaluator(
            CoachGroundingStage.Enforce,
            Answer("Here are your words.", "ko-KR"),
            [ClaimFixture.Evidence(withheld: 5)]);

        result.Grounding.Should().NotBeNull(
            "an evaluated turn must persist what the layer did, refused or not");

        result.Grounding!.RequestedStage.Should().Be(CoachGroundingStage.Enforce);
        result.Grounding.Refused.Should().BeTrue();
        result.Grounding.SubstitutionAllowed.Should().BeFalse();
        result.Grounding.RepairSuppressedForLanguage.Should().BeTrue();
    }

    [Fact]
    public void The_evaluator_at_off_produces_no_record_and_no_summary()
    {
        var result = RunEvaluator(
            CoachGroundingStage.Off,
            Answer("Here are your words.", "ko-KR"),
            [ClaimFixture.Evidence(withheld: 5)]);

        result.Refused.Should().BeFalse();
        result.Record.Should().BeNull();
        result.Grounding.Should().BeNull("Off must be indistinguishable from a build with no layer");
    }

    // ── Suppression reaches the record ───────────────────────────────────────

    /// <summary>
    /// A structural Korean refusal records the suppression alongside the refusal.
    /// </summary>
    /// <remarks>
    /// Both facts, not one. A record that showed only "refused" would lose the reason the answer
    /// was never softened first, and R2 persists that distinction into the report.
    /// </remarks>
    [Fact]
    public void A_korean_enforce_turn_records_both_refusal_and_suppression()
    {
        var record = new CoachClaimTurnRecord(
            CoachGroundingStage.Enforce,
            Engine().Evaluate(
                StructuralFinding("ko-KR"),
                CoachGroundingStage.Enforce,
                substitutionAllowed: false).Findings,
            Refused: true,
            AnswerAltered: false,
            ShadowLabel: CoachShadowRouteLabel.Unknown,
            Limitation: null,
            RepairSuppressedForLanguage: true);

        record.Stage.Should().Be(
            CoachGroundingStage.Enforce,
            "the record carries the rung the deployment asked for, never a collapsed value");
        record.Refused.Should().BeTrue();
        record.RepairSuppressedForLanguage.Should().BeTrue();
        record.AnswerAltered.Should().BeFalse();
    }
}
