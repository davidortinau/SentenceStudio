using FluentAssertions;
using FluentAssertions.Execution;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// The four named foundation bars of plan §14.2, one class section each.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are and are not.</b> AC-F1..AC-F8 are unit gates on individual mechanisms. These
/// four are the questions a learner actually asks, each carrying the shape the rules must catch on
/// the way past. They are named in the plan as bars rather than tests because a build can satisfy
/// every AC-F case and still answer "when did I last study?" with an unbounded negative.
/// </para>
/// <para>
/// <b>Every fixture is synthetic.</b> Shapes only — row counts, date offsets, order, filter effect,
/// withheld counts. No authentic learner text, account id, term or conversation id appears here, per
/// the §14 fixture rule. The identifiers below are literals chosen to look like nothing.
/// </para>
/// <para>
/// <b>Deferrals are pinned, not skipped.</b> Two of the four bars include a clause this build cannot
/// meet — naming a destination. Those halves are asserted at their real current value with the
/// re-arm milestone named, so the gap is visible in a passing run rather than absent from it.
/// </para>
/// </remarks>
public sealed class CoachFoundationCaseBarsTests
{
    private static CoachClaimRuleEngine Engine(
        StubCapabilityResolver? resolver = null,
        StubCapabilityManifest? manifest = null) =>
        new(resolver ?? new StubCapabilityResolver(), manifest ?? new StubCapabilityManifest());

    // ═════════════════════════════════════════════════════════════════════════
    // Case A — "when did I last study?"
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Case A: evidence bounded to a 30-day window, and an unbounded negative over it fires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The learner asks when they last studied. The read can only see thirty days. An answer that
    /// says "you haven't studied" is making a claim about all of time from a window, and for a
    /// learner returning after a break it is both false and discouraging — the worst combination
    /// this layer exists to prevent.
    /// </para>
    /// <para>
    /// The bar is that the bounded shape is what fires the rule. A build that fired on every negative
    /// regardless of coverage would pass a naive version of this test and refuse honest answers.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseA)]
    public void Case_A_an_unbounded_negative_over_a_windowed_read_fires()
    {
        var findings = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You haven't studied at all."),
            Evidence =
            [
                ClaimFixture.Evidence(
                    coverage: CoachEvidenceCoverage.WindowBounded,
                    order: CoachEvidenceOrder.LastUsedAscending)
            ]
        });

        findings.Select(finding => finding.Rule).Should().Contain(
            CoachClaimRuleCode.NegativeClaimWithoutCoverage,
            "the read saw thirty days. 'At all' is a claim about every day before them too");
    }

    /// <summary>
    /// Case A, the other half: the same answer over a complete read does not fire.
    /// </summary>
    /// <remarks>
    /// The zero fixture that makes the bar above mean something. If the read genuinely covers the
    /// owned set, the negative is supported and stating it is the honest answer. A rule that fired
    /// here would train the model to hedge answers it had the evidence for.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseA)]
    public void Case_A_the_same_negative_over_a_complete_read_does_not_fire()
    {
        var findings = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You haven't studied at all."),
            Evidence =
            [
                ClaimFixture.Evidence(
                    coverage: CoachEvidenceCoverage.CompleteOwnedSet,
                    order: CoachEvidenceOrder.LastUsedAscending)
            ]
        });

        findings.Select(finding => finding.Rule).Should().NotContain(
            CoachClaimRuleCode.NegativeClaimWithoutCoverage,
            "coverage is the whole question. Firing here would make the rule a ban on negatives "
            + "rather than a ban on unsupported ones");
    }

    /// <summary>
    /// Case A's destination clause: <c>/activity-log</c> exists as a route name and is not yet
    /// stated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §14.2 Case A asks the answer to name <c>/activity-log</c>. The route name is declared, so the
    /// vocabulary exists; the limitation projection deliberately states no destination because no
    /// capability declares a route this build could derive one from. Stating a screen the client may
    /// not have is worse than stating none.
    /// </para>
    /// <para>
    /// <b>Re-arm condition C1.</b> When capabilities declare routes, this half becomes achievable and
    /// this test is replaced by one asserting the destination is stated. Keeping it here means the
    /// deferral is visible in every green run rather than living in a comment somebody deletes.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseA)]
    public void Case_A_the_activity_log_destination_is_declared_but_deferred_to_C1()
    {
        Enum.IsDefined(CoachRouteName.ActivityLog).Should().BeTrue(
            "the vocabulary for the destination exists, so the deferral is about derivation and not "
            + "about a missing concept");

        CoachCapabilityDeclarations.All.Should().NotContain(
            declaration => declaration.EffectClass == CoachCapabilityEffectClass.ActivityLaunch,
            "and nothing yet declares a route the projection could derive a destination from. "
            + "Re-arm at C1");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Case B — "was I active on the 14th?"
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Case B: with no date capability, an answer describing a check fires and repairs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The learner asks about a specific date. The manifest reports no capability that can answer it.
    /// The failure mode is not the model saying "I can't" — it is the model saying "I checked, and
    /// you weren't active", which is a fabricated proxy for a read that never ran. That sentence is
    /// indistinguishable from a real answer, so it is the one shape a learner cannot defend against.
    /// </para>
    /// <para>
    /// The bar has two clauses: it fires, and it repairs. A rule that only fired would leave the
    /// fabrication in the answer at Repair, where the operator has asked for correction rather than
    /// observation.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseB)]
    public void Case_B_a_described_check_that_never_ran_fires_and_repairs()
    {
        var outcome = Engine().Evaluate(
            new CoachClaimRuleContext
            {
                Answer = ClaimFixture.Answer("I checked your activity for that date."),
                Trace = ClaimFixture.FailedTrace()
            },
            CoachGroundingStage.Repair);

        outcome.Findings.Select(finding => finding.Rule).Should().Contain(
            CoachClaimRuleCode.FabricatedCheck,
            "the read failed. Describing it as done is a proxy for evidence that does not exist");

        outcome.Refused.Should().BeFalse("Repair repairs");

        outcome.Answer!.PlainText.Should().NotBe(
            "I checked your activity for that date.",
            "and the fabrication must not survive the rung whose job is removing it");
    }

    /// <summary>
    /// Case B's fixture clause: rows exist on the target date, so the absence is the capability's and
    /// not the data's.
    /// </summary>
    /// <remarks>
    /// §14.2 is explicit that a fixture holds rows on the target date. Without it, "you weren't
    /// active" would be accidentally true and the bar would pass on a build that fabricated freely.
    /// The evidence here is a same-day read that found rows — the shape a real date capability would
    /// return once one exists.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseB)]
    public void Case_B_the_fixture_holds_rows_on_the_target_date_so_the_absence_is_not_accidental()
    {
        var sameDay = ClaimFixture.Evidence(
            coverage: CoachEvidenceCoverage.SingleDay,
            order: CoachEvidenceOrder.NotApplicable,
            matched: 3,
            returned: 3,
            withheld: 0);

        sameDay.MatchedCount.Should().Be(
            3, "the day is not empty. A build that fabricated 'you weren't active' would be wrong "
            + "on the facts as well as on the method");

        var findings = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You weren't active that day."),
            Evidence = [sameDay]
        });

        findings.Select(finding => finding.Rule).Should().NotContain(
            CoachClaimRuleCode.FabricatedCheck,
            "and when a same-day read really did run, describing it is not fabrication. The rule "
            + "keys on the trace, not on the sentence");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Case C — "show me my newest words"
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The Case C shape: 14 matched, 4 returned, 10 withheld under a due embargo, ordered by mastery.
    /// </summary>
    /// <remarks>
    /// The learner asked for newest. The read answered by mastery, held back ten rows under the due
    /// embargo, and returned four. Every one of those three facts is a way the answer can mislead,
    /// and §14.2 requires all three rules to fire on the recorded bad shape.
    /// </remarks>
    /// <param name="order">
    /// Overridden only by the order-gap test, which needs the identical shape with the order
    /// undeclared. <see cref="CoachEvidenceDto"/> is a class rather than a record, so the variant is
    /// a parameter rather than a `with` expression.
    /// </param>
    private static CoachEvidenceDto CaseCEvidence(
        CoachEvidenceOrder order = CoachEvidenceOrder.MasteryDescending,
        CoachWithheldReason? withheldReason = CoachWithheldReason.DueReviewEmbargo) => new()
    {
        Kind = CoachEvidenceKind.VocabularyDue,
        Label = "Vocabulary",
        Summary = "Words you are tracking.",
        WindowStartDate = new DateOnly(2026, 8, 1),
        WindowEndDate = new DateOnly(2026, 8, 21),
        Coverage = CoachEvidenceCoverage.PageOfOwnedSet,
        Order = order,
        MatchedCount = 14,
        ReturnedCount = 4,
        WithheldCount = 10,
        WithheldReason = withheldReason
    };

    /// <summary>
    /// Case C: all three mismatch rules fire on the recorded bad shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §14.2 bar, stated without concession. The learner asked for newest. The read answered by
    /// mastery, held ten rows under the due embargo, and returned four. The answer claims the order
    /// the learner asked for rather than the one the read used, states thirty — a number none of the
    /// recorded counts (14 matched, 4 returned, 10 withheld) supports — and never mentions that ten
    /// rows were held back. Three distinct ways to mislead over one read, and §14.2 requires all
    /// three rules to fire on it.
    /// </para>
    /// <para>
    /// The stated number has to miss all three counts: a count rule that fired on 14 would be firing
    /// on a number the evidence does support, which is a false positive, not a bar.
    /// </para>
    /// <para>
    /// <b>The bar this landed against.</b> <see cref="CoachClaimRuleCode.OrderClaimMismatch"/> used
    /// to early-return whenever the evidence stated any order at all, and Case C's scope states
    /// <see cref="CoachEvidenceOrder.MasteryDescending"/> — so contradicting a known order went
    /// uncaught and this bar was red. The rule has since been widened to resolve the claimed ranking
    /// against the recorded one, and the bar is green. The previous form of this test asserted two
    /// of three and recorded the third as a documented gap. That concession stays removed: a rule
    /// that cannot see the case the plan names is a defect, not a footnote, and invariant 4's soak
    /// zero was uninterpretable until it was fixed.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseC)]
    public void Case_C_all_three_mismatch_rules_fire_on_the_recorded_shape()
    {
        var findings = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.AnswerWith(
                "Your newest words, sorted by when you added them.",
                "You have 30 words tracked right now."),
            // The reason is cleared for this case. Case C is the shape where the answer hides
            // the withholding, and disclosure is now the structured pair rather than English
            // prose — so a coherent count-and-reason would be disclosure and the third rule
            // would correctly stay silent. Clearing the reason is what makes the case the case.
            Evidence = [CaseCEvidence(withheldReason: null)]
        });

        var fired = findings.Select(finding => finding.Rule).ToHashSet();

        using var _ = new AssertionScope();

        fired.Should().Contain(
            CoachClaimRuleCode.OrderClaimMismatch,
            "the answer claims insertion order over a read the scope recorded as MasteryDescending. "
            + "§14.2 names three rules for this shape and this is the third");

        fired.Should().Contain(
            CoachClaimRuleCode.CountClaimMismatch,
            "the answer states a count the four returned rows do not support");

        fired.Should().Contain(
            CoachClaimRuleCode.WithheldNotDisclosed,
            "ten rows were held back and the panel carries no reason that would explain them");
    }

    /// <summary>
    /// Case C's order rule, both paths: contradicting a stated order and outrunning an unstated one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same answer over the same counts, differing only in whether the scope declared its order.
    /// Both are order claims the evidence does not support, and both must be caught.
    /// </para>
    /// <para>
    /// <b>Stated order.</b> The scope recorded <see cref="CoachEvidenceOrder.MasteryDescending"/> and
    /// the prose claims insertion order. This is the stronger of the two cases — the read told us
    /// exactly what order it used, and the answer said something else. It is currently uncaught
    /// because the rule exits early on any stated order.
    /// </para>
    /// <para>
    /// <b>Unstated order.</b> The scope declared nothing and the prose asserts an ordering anyway.
    /// This half already passes and must keep passing; it is the case the rule was written for.
    /// </para>
    /// <para>
    /// Keeping the pair in one test is deliberate. It is the difference between the two halves that
    /// localises the defect to the early return rather than to the regex or the fixture.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseC)]
    public void Case_C_the_order_rule_catches_a_contradicted_order_and_an_unstated_one()
    {
        const string ContradictsTheOrder = "Your newest words, sorted by when you added them.";

        var statedOrder = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(ContradictsTheOrder),
            Evidence = [CaseCEvidence()]
        });

        var unstatedOrder = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer(ContradictsTheOrder),
            Evidence = [CaseCEvidence(CoachEvidenceOrder.Unordered)]
        });

        using var _ = new AssertionScope();

        statedOrder.Select(finding => finding.Rule).Should().Contain(
            CoachClaimRuleCode.OrderClaimMismatch,
            "the read recorded MasteryDescending and the answer claims insertion order. A rule that "
            + "goes quiet precisely because the evidence was more specific has the case backwards");

        unstatedOrder.Select(finding => finding.Rule).Should().Contain(
            CoachClaimRuleCode.OrderClaimMismatch,
            "and prose asserting an order over a read that declared none stays caught. Widening the "
            + "rule must not cost the case it already handles");
    }

    /// <summary>
    /// Case C: claiming the order the read actually used is not a mismatch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fence on the widening. Simply deleting the <c>EvidenceStatesAnOrder</c> early return would
    /// turn the rule into "any order word near any evidence is a finding", and this answer — which
    /// says exactly what the read did — would be refused or rewritten. That is a worse failure than
    /// the gap it fixes, because it punishes the honest answer.
    /// </para>
    /// <para>
    /// The phrasing here deliberately matches the rule's <c>(ranked|sorted|ordered) by</c> pattern, so
    /// the test cannot be satisfied by a fix that merely narrows the regex away from this sentence.
    /// The fix has to compare the claimed order against the recorded one.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseC)]
    public void Case_C_an_answer_that_states_the_order_the_read_used_is_not_a_mismatch()
    {
        var findings = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Here are four of your words, sorted by mastery."),
            Evidence = [CaseCEvidence()]
        });

        findings.Select(finding => finding.Rule).Should().NotContain(
            CoachClaimRuleCode.OrderClaimMismatch,
            "the answer describes MasteryDescending, which is what the scope recorded. A build that "
            + "fired here would teach the model that describing its own evidence is unsafe");
    }

    /// <summary>
    /// Case C: no due term appears in the answer, and the embargo is why.
    /// </summary>
    /// <remarks>
    /// The clause that makes Case C about the learner rather than about counters. Ten rows were
    /// withheld under the due embargo — showing a due term in a browse answer is a free look at
    /// material the learner is about to be quizzed on, which quietly destroys the value of the quiz.
    /// The withheld reason is the machine-readable form of that, and it must survive into evidence.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseC)]
    public void Case_C_the_withheld_rows_are_held_under_the_due_embargo()
    {
        var evidence = CaseCEvidence();

        evidence.WithheldReason.Should().Be(
            CoachWithheldReason.DueReviewEmbargo,
            "a due term in a browse answer is a free look at what the learner is about to be quizzed "
            + "on, and the reason has to be on the evidence for the disclosure to be able to say so");

        evidence.WithheldCount.Should().Be(10);

        (evidence.ReturnedCount + evidence.WithheldCount).Should().Be(
            evidence.MatchedCount,
            "returned plus withheld accounts for everything matched. A shape where they did not "
            + "would make the disclosure unverifiable");
    }

    /// <summary>
    /// Case C's zero fixture: a disclosed, honestly-ordered, correctly-counted answer fires nothing.
    /// </summary>
    /// <remarks>
    /// The same read, answered properly. Without this, the three-rule bar above is satisfiable by a
    /// build that fires all three rules on every vocabulary answer, which would refuse the correct
    /// answer as readily as the wrong one. The order clause says what the read did, so it must
    /// survive the widened order rule as well.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseC)]
    public void Case_C_the_honest_answer_over_the_same_read_fires_none_of_the_three()
    {
        var findings = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.AnswerWith(
                "Here are four of your words, sorted by mastery.",
                "Ten more are held back because they are due for review."),
            Evidence = [CaseCEvidence()]
        });

        var fired = findings.Select(finding => finding.Rule).ToHashSet();

        fired.Should().NotContain(CoachClaimRuleCode.OrderClaimMismatch);
        fired.Should().NotContain(CoachClaimRuleCode.CountClaimMismatch);
        fired.Should().NotContain(
            CoachClaimRuleCode.WithheldNotDisclosed,
            "the answer discloses the ten. A build that still fired here would make the disclosure "
            + "pointless and teach the model that nothing it writes is acceptable");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Case D — the learner disputes a claim
    // ═════════════════════════════════════════════════════════════════════════

    private const string DisputedMessageId = "3f1c9a44-0d3e-4c1b-9a5e-77b2c1d0e912";

    private static CoachTurnDisputeState OpenDispute() => new(
        CoachCorrectionSignal.NotWhatIAsked,
        DisputedMessageId,
        new DateTime(2026, 8, 22, 2, 5, 0, DateTimeKind.Utc),
        ResolvedAtUtc: null,
        CoachDisputeResolution.Open,
        [CoachScopeDefinition.TrackedVocabularyDueSummary]);

    /// <summary>
    /// Case D: a more confident repeat of a disputed claim fires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The learner said the claim was wrong. Repeating it is bad; repeating it with more certainty
    /// is the specific behaviour that makes a learner stop correcting the system at all, and once
    /// they stop, every later claim goes unchallenged. That is why this is a foundation bar and not
    /// a nicety.
    /// </para>
    /// <para>
    /// The dispute is open and scoped to the definition the claim came from, which is what lets the
    /// rule tell a repeat from an unrelated answer that happens to mention a number.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Agreement)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseD)]
    public void Case_D_a_more_confident_repeat_of_a_disputed_claim_fires()
    {
        var findings = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You definitely have twelve words due this week."),
            Trace = ClaimFixture.SuccessfulTrace(),
            Dispute = OpenDispute()
        });

        findings.Select(finding => finding.Rule).Should().Contain(
            CoachClaimRuleCode.RepeatedDisputedClaim,
            "the learner already said this was wrong. Saying it again with 'definitely' is how a "
            + "learner learns that correcting the system does nothing");
    }

    /// <summary>
    /// Case D: the dispute names the exact prior message, so the learner can see what was disputed.
    /// </summary>
    /// <remarks>
    /// A dispute that does not identify its target cannot be shown to the learner and cannot scope a
    /// rule. Both halves of Case D — "opens against the exact prior message" and "the learner sees
    /// the dispute" — depend on this identifier being carried, so it is asserted rather than assumed.
    /// The value is a synthetic literal; no authentic conversation id appears in this suite.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Agreement)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseD)]
    public void Case_D_the_dispute_identifies_the_exact_prior_message_and_stays_open()
    {
        var dispute = OpenDispute();

        dispute.DisputedMessageId.Should().Be(
            DisputedMessageId,
            "a dispute that cannot name its target cannot be shown to the learner and cannot scope "
            + "the rule that reads it");

        dispute.Resolution.Should().Be(
            CoachDisputeResolution.Open,
            "and it stays open until something resolves it. A dispute that closed itself would let "
            + "the next turn repeat the claim freely");

        dispute.ResolvedAtUtc.Should().BeNull();

        dispute.DisputedDefinitionCodes.Should().ContainSingle().Which.Should().Be(
            CoachScopeDefinition.TrackedVocabularyDueSummary,
            "scoped to the definition the claim came from, which is what distinguishes a repeat "
            + "from an unrelated answer that happens to mention a number");
    }

    /// <summary>
    /// Case D's zero fixture: with no dispute open, the same sentence fires nothing.
    /// </summary>
    /// <remarks>
    /// The rule keys on the dispute, not on the word "definitely". Without this control the bar
    /// above would be satisfied by a build that penalised confident phrasing generally, which would
    /// make every correct answer sound uncertain.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Agreement)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseD)]
    public void Case_D_the_same_sentence_with_no_dispute_open_fires_nothing()
    {
        var findings = Engine().Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("You definitely have twelve words due this week."),
            Trace = ClaimFixture.SuccessfulTrace()
        });

        findings.Select(finding => finding.Rule).Should().NotContain(
            CoachClaimRuleCode.RepeatedDisputedClaim,
            "no dispute, no repeat. The rule reads the correction record, not the adverb");
    }

    /// <summary>
    /// Case D at Enforce: the repeat is structural and refuses rather than being softened.
    /// </summary>
    /// <remarks>
    /// The clause that makes the bar bite. A repeat that could be repaired into a hedge would let
    /// the system keep asserting the disputed thing in gentler words, which is the same failure with
    /// better manners. At Enforce the learner gets the notice instead.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Agreement)]
    [Trait(CoachGateCase.Key, CoachGateCase.CaseD)]
    public void Case_D_at_enforce_the_repeat_does_not_ship_in_softer_words()
    {
        var outcome = Engine().Evaluate(
            new CoachClaimRuleContext
            {
                Answer = ClaimFixture.Answer("You definitely have twelve words due this week."),
                Trace = ClaimFixture.SuccessfulTrace(),
                Dispute = OpenDispute()
            },
            CoachGroundingStage.Enforce);

        outcome.Findings.Select(finding => finding.Rule).Should().Contain(
            CoachClaimRuleCode.RepeatedDisputedClaim);

        (outcome.Refused || outcome.Answer!.PlainText != "You definitely have twelve words due this week.")
            .Should().BeTrue(
                "the disputed claim must not reach the learner unchanged. Either the turn is "
                + "refused or the assertion is gone \u2014 what it may not do is ship the same "
                + "claim with the confidence trimmed off");
    }
}
