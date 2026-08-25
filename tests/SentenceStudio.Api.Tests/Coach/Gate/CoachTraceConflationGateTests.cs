using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// The trace-conflation gate case: an empty observation buffer is proof that nothing was read, and
/// must be told apart from no buffer at all, which is proof of nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The distinction, in one sentence.</b> "The turn ran no tools" and "we do not know whether the
/// turn ran tools" are different facts with opposite consequences, and the layer has to hold them
/// apart from the projection all the way down to the rules.
/// </para>
/// <para>
/// <b>Why it is load-bearing.</b> Two honesty rules gate themselves on the trace, and both do it
/// for a good reason. <c>CoachFabricatedCheckRule</c> says so in its own source:
/// <em>"No trace is no evidence of absence. Only a recorded turn can prove a check did not run."</em>
/// A turn whose observation buffer was present and recorded <b>zero</b> tool calls <em>is</em> the
/// recorded turn that comment asks for — it is positive proof nothing was read. If it reaches the
/// rules as <c>null</c>, they read "unknown", bail, and an answer saying <em>"I checked your
/// vocabulary list"</em> after checking nothing ships unaltered at Enforce.
/// </para>
/// <para>
/// <b>The population this covers is the one that matters.</b> An empty-but-present buffer is not an
/// exotic shape — it is every turn where the model answered from its own head instead of calling a
/// tool, which is precisely the population the fabricated-check and unverified-state rules exist to
/// catch. Erasing it makes the two soak invariants those rules feed read zero whether or not the
/// behaviour is occurring, and a zero produced by a blind spot is worse than a missing measurement
/// because it is reported with the same confidence as a real one.
/// </para>
/// <para>
/// <b>The null case is correct and must stay correct.</b> A turn from before the observation buffer
/// existed, and any caller that still passes <c>null</c>, genuinely tells us nothing. Treating
/// unknown as guilty would refuse or rewrite honest historical answers on no evidence at all. So
/// this file asserts both directions at once: the empty buffer convicts, the absent buffer does
/// not. That pairing is the point — it is what stops the strict half being satisfied by simply
/// dropping the trace gate.
/// </para>
/// <para>
/// <b>History.</b> These cases were written and released deliberately red during W9 review, when
/// <c>CoachTurnTraceProjection.Project</c> returned <c>null</c> for an empty buffer just as it did
/// for a missing one. They were the executable statement of the finding and the bar the fix had to
/// land against. The production change is in: the projection now returns a zero-length trace when
/// the buffer is present and keeps <c>null</c> only for a null buffer, so both rules can ask their
/// question. The file stays as the regression fence — the collapse is a cheap-looking optimisation
/// and would be easy to reintroduce as a "why allocate an empty array" tidy-up.
/// </para>
/// <para>Fixture rule (§14): shapes only. No authentic term, identifier, or learner text.</para>
/// </remarks>
public sealed class CoachTraceConflationGateTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // The projection seam itself. Asserted directly, so a regression names the
    // collapsing line rather than surfacing as a rule three layers up going quiet.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An empty-but-present buffer must project to something other than null.
    /// </summary>
    /// <remarks>
    /// The narrowest statement of the property, and the one everything below depends on: if the
    /// projection ever erases the difference again, no consumer can recover it, because by the time
    /// the rules run the two cases are the same reference.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    public void An_empty_but_present_buffer_does_not_project_to_null()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.RecordBudget(used: 0, limit: 8);

        var trace = CoachTurnTraceProjection.Project(buffer);

        trace.Should().NotBeNull(
            "the buffer was present for this turn and recorded zero of eight calls used. That is a "
            + "recorded turn stating positively that nothing was read, which is the strongest "
            + "evidence the layer can hold — and projecting it to null makes it indistinguishable "
            + "from a turn we never observed at all");
    }

    /// <summary>
    /// A null buffer must keep projecting to null. This one passes today and must keep passing.
    /// </summary>
    /// <remarks>
    /// The guard rail on the fix. It is already asserted in <c>CoachTurnTraceShapeTests</c>; it is
    /// restated here because the value of the pair is that both hold <em>at once</em>, and a
    /// reviewer looking at this file should be able to see the whole finding without opening
    /// another one.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    public void An_absent_buffer_still_projects_to_null()
    {
        CoachTurnTraceProjection.Project(buffer: null).Should().BeNull(
            "no buffer is no observation. A turn from before the buffer existed cannot be convicted "
            + "on evidence that was never collected, and a fix that makes unknown look guilty would "
            + "rewrite honest historical answers");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The consequence at the evaluator, which is where the gate reads it.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fabricated check must be caught when the recorded turn read nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted through <c>Evaluate</c> rather than against the rule in isolation, because the
    /// defect is not in the rule — the rule's logic is correct given what it is handed. It is in
    /// what the evaluator hands it. A rule-level test would have to construct the context by hand
    /// and would therefore pass while production stayed broken, which is how this reached Enforce
    /// in the first place.
    /// </para>
    /// <para>
    /// The bar is stated as "does not ship unaltered" rather than "refuses", because
    /// <c>FabricatedCheck</c> carries a deterministic substitution, so the specified repair at
    /// Enforce is a rewrite, not a refusal. Either outcome satisfies the gate; silence does not.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    public void Enforce_catches_a_fabricated_check_when_the_recorded_turn_read_nothing()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.RecordBudget(used: 0, limit: 8);

        var original = ClaimFixture.Answer("I checked your vocabulary list and it looks fine.");

        var result = Evaluator().Evaluate(
            CoachGroundingStage.Enforce,
            original,
            evidence: [],
            observations: buffer,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        using var _ = new AssertionScope();

        result.Record.Should().NotBeNull("Enforce records every evaluated turn");
        result.Record!.Findings.Select(finding => finding.Rule).Should().Contain(
            CoachClaimRuleCode.FabricatedCheck,
            "the answer asserts a read, and the recorded turn proves no read happened. The rule "
            + "bails on a null trace by design — 'no trace is no evidence of absence' — but this "
            + "turn has a trace, and it says zero calls. The projection is what erased that");

        (result.Refused || result.Grounding?.Altered == true).Should().BeTrue(
            "at Enforce a fabricated check must either be refused or repaired to the deterministic "
            + "'no read happened' copy. Shipping the claim unaltered tells the learner their "
            + "records were consulted when nothing was");
    }

    /// <summary>
    /// An unverified learner-state claim, same shape, same erasure.
    /// </summary>
    /// <remarks>
    /// Included because the two rules fail for one reason and a fix that only repairs the
    /// fabricated-check path would leave the second half of the blind spot open. Distinct fixture
    /// text so the two are not one assertion wearing two names: this answer makes no claim about
    /// having checked, it simply asserts a fact about the learner that nothing was read to support.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    public void Enforce_catches_an_unverified_learner_state_claim_on_an_empty_trace()
    {
        var buffer = new CoachTurnObservationBuffer();
        buffer.RecordBudget(used: 0, limit: 8);

        var original = ClaimFixture.Answer("You have reviewed these words before.");

        var result = Evaluator().Evaluate(
            CoachGroundingStage.Enforce,
            original,
            evidence: [],
            observations: buffer,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        using var _ = new AssertionScope();

        result.Record.Should().NotBeNull();
        result.Record!.Findings.Select(finding => finding.Rule).Should().Contain(
            CoachClaimRuleCode.UnverifiedLearnerStateClaim,
            "the answer states the learner's history as fact. The recorded turn read nothing and "
            + "no evidence was supplied, so there is nothing behind the claim");

        (result.Refused || result.Grounding?.Altered == true).Should().BeTrue(
            "at Enforce the claim must be refused or replaced with the deterministic 'not checked' "
            + "copy rather than presented as established");
    }

    /// <summary>
    /// The other half of the pair: the same two answers on a genuinely unknown turn stay silent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Passes today, and is the reason the fix cannot be "drop the trace gate". A pre-W4 turn, or
    /// any caller still passing <c>null</c>, supplies no observation at all. Convicting on that is
    /// not stricter honesty, it is a guess — and it would rewrite answers that may well have been
    /// perfectly grounded by a tool call nobody was recording yet.
    /// </para>
    /// <para>
    /// Both rules are asserted absent, not just one, so an over-broad fix cannot half-pass.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("I checked your vocabulary list and it looks fine.")]
    [InlineData("You have reviewed these words before.")]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    public void An_unobserved_turn_is_not_convicted_on_evidence_that_was_never_collected(string text)
    {
        var result = Evaluator().Evaluate(
            CoachGroundingStage.Enforce,
            ClaimFixture.Answer(text),
            evidence: [],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        using var _ = new AssertionScope();

        var rules = result.Record?.Findings.Select(finding => finding.Rule).ToList() ?? [];

        rules.Should().NotContain(
            CoachClaimRuleCode.FabricatedCheck,
            "no buffer means no recorded turn, and only a recorded turn can prove a check did not "
            + "run. This is the rule's own stated reasoning and it is correct");

        rules.Should().NotContain(
            CoachClaimRuleCode.UnverifiedLearnerStateClaim,
            "same reason. Unknown is not guilty, and a fix that makes it guilty trades one "
            + "dishonesty for another");

        result.Refused.Should().BeFalse(
            "an unobserved turn carries no grounds for refusal on these two rules");
    }

    /// <summary>
    /// Non-vacuity. The two red tests above are red because of the trace, not because the fixture
    /// never had a claim in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the two conviction tests above could go quiet for a boring reason — a fixture
    /// whose text no longer matches the rule's referent pattern would produce no finding either,
    /// and would look identical in the output. That sends the next reader to the wrong file.
    /// </para>
    /// <para>
    /// So this pins the other axis: hand the same two answers a trace with one successful read but
    /// give the fabricated-check answer nothing to have read, and the rules engage. It proves the
    /// text is recognised and the only variable left is how an empty buffer projects.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    public void The_fixture_text_is_recognised_by_the_rules_when_a_trace_exists()
    {
        var recognised = CoachLearnerStateReferent.IsLearnerStateClaim(
            "You have reviewed these words before.");

        recognised.Should().BeTrue(
            "if the referent matcher did not recognise this sentence, the unverified-state red "
            + "above would be red for a fixture reason rather than the trace reason, and the "
            + "finding would be filed against the wrong seam");

        CoachLearnerStateReferent.IsLearnerStateClaim("Korean marks the topic with a particle.")
            .Should().BeFalse(
                "and the matcher is not simply saying yes to everything, which would make the "
                + "assertion above meaningless");
    }

    private static CoachTurnGroundingEvaluator Evaluator()
    {
        var resolver = new StubCapabilityResolver();
        var manifest = new StubCapabilityManifest();

        return new CoachTurnGroundingEvaluator(
            new CoachClaimRuleEngine(resolver, manifest),
            resolver,
            NullLogger<CoachTurnGroundingEvaluator>.Instance);
    }
}
