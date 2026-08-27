using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// The grounding ladder on the real turn path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are turn tests and not engine tests.</b> <c>CoachClaimRuleEngine</c> already had a
/// full suite and every one of those tests passed while the engine was unreachable: it was
/// registered in DI and nothing resolved it, so nine rules ran on nothing. A rule test constructs
/// its own context and therefore cannot notice a missing caller. Every test in this file drives
/// <c>CoachSessionService.SubmitTurnAsync</c> and asserts on the response a learner would receive,
/// so deleting the call site fails them.
/// </para>
/// <para>
/// The one exception is the router-equivalence pair and the manifest-reachability case at the
/// bottom, which construct the evaluator directly. Those are about a wiring property — that a
/// component is absent, or that a value arrives — which a full turn cannot isolate.
/// </para>
/// </remarks>
public sealed class CoachTurnGroundingPathTests
{
    private const string Question = "Am I doing well with these words?";

    /// <summary>A sentence about the learner that no read supports. Pronoun plus state verb.</summary>
    private const string UnverifiedClaim = "You have reviewed these words plenty of times already.";

    /// <summary>Instructional text no rule touches. The control in every substitution assertion.</summary>
    private const string InstructionalText = "The verb ending changes with the politeness level.";

    // ------------------------------------------------------------------ Off

    [Fact]
    public async Task Off_is_a_total_bypass()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Off);
        harness.SeedFailedRead();

        var response = await AskAsync(harness, TwoSpanAnswer());

        response.Answer.Should().NotBeNull();
        SpanText(response.Answer!, 0).Should().Be(UnverifiedClaim);
        response.Status.Should().Be(CoachTurnStatus.Completed);

        // Not "scanned and discarded". Never called, so a host at Off is indistinguishable from
        // the build that had no ladder — which is what makes promoting to Observe measurable.
        harness.ClaimFindings.Record.Should().BeNull();
    }

    [Fact]
    public void The_evaluator_bypasses_at_Off_on_its_own()
    {
        // The service checks the stage before calling, so the evaluator's own Off guard is never
        // exercised by a turn — which means a turn test cannot notice if it is deleted. Both
        // guards are load-bearing: the service's keeps the call out of the hot path, the
        // evaluator's keeps any future caller from scanning at Off. This covers the second one.
        using var harness = new CoachApplicationHarness();

        var result = harness.Grounding.Evaluate(
            CoachGroundingStage.Off,
            Compose(UnverifiedClaim, "I checked your practice history."),
            evidence: [],
            observations: null,
            proposedCapabilities: ["absent_capability"],
            capabilityStage: CoachCapabilityStage.Off,
            handshake: null);

        result.Record.Should().BeNull("Off records nothing, however much there was to find");
        result.Refused.Should().BeFalse();
        result.Answer!.Blocks[0].Spans[0].Text.Should().Be(UnverifiedClaim);
        harness.ClaimFindings.Record.Should().BeNull("nothing reached the buffer either");
    }

    // -------------------------------------------------------------- Observe

    [Fact]
    public async Task Observe_finds_and_leaves_the_answer_byte_identical()
    {
        using var offHarness = new CoachApplicationHarness();
        offHarness.SetGroundingStage(CoachGroundingStage.Off);
        offHarness.SeedFailedRead();
        var untouched = await AskAsync(offHarness, TwoSpanAnswer());

        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Observe);
        harness.SeedFailedRead();

        var response = await AskAsync(harness, TwoSpanAnswer());

        harness.ClaimFindings.Record.Should().NotBeNull();
        harness.ClaimFindings.Record!.Stage.Should().Be(CoachGroundingStage.Observe);
        harness.ClaimFindings.Record.Findings.Should()
            .Contain(finding => finding.Rule == CoachClaimRuleCode.UnverifiedLearnerStateClaim);

        harness.ClaimFindings.Record.Findings.Should()
            .OnlyContain(finding => finding.Action == CoachClaimRepairAction.ObservedOnly,
                "Observe records and never alters, however severe the finding");

        // Byte-identical against a real Off turn rather than against the literal, so a change to
        // the projection cannot make both sides wrong in the same direction.
        response.Answer!.PlainText.Should().Be(untouched.Answer!.PlainText);
        SpanText(response.Answer, 0).Should().Be(SpanText(untouched.Answer, 0));
        SpanText(response.Answer, 1).Should().Be(SpanText(untouched.Answer, 1));
        response.Status.Should().Be(CoachTurnStatus.Completed);
        harness.ClaimFindings.Record.AnswerAltered.Should().BeFalse();
    }

    // --------------------------------------------------------------- Repair

    [Fact]
    public async Task Repair_substitutes_the_offending_span_and_leaves_the_rest()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Repair);
        harness.SeedFailedRead();

        var response = await AskAsync(harness, TwoSpanAnswer());

        response.Answer.Should().NotBeNull();
        SpanText(response.Answer!, 0).Should().Be(CoachDeterministicCopy.UncheckedLearnerState);
        SpanText(response.Answer!, 1).Should().Be(
            InstructionalText, "a rule that fired on one span must not rewrite another");

        response.Answer!.PlainText.Should().Contain(CoachDeterministicCopy.UncheckedLearnerState);
        response.Answer.PlainText.Should().NotContain(UnverifiedClaim,
            "the plain-text projection is read directly by surfaces that never see the blocks");

        response.Status.Should().Be(CoachTurnStatus.Completed);
        harness.ClaimFindings.Record!.AnswerAltered.Should().BeTrue();
        harness.ClaimFindings.Record.Refused.Should().BeFalse();
    }

    [Fact]
    public async Task Repair_ships_an_unrepairable_finding_rather_than_refusing()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Repair);
        // Undisclosed on purpose: a count the evidence panel cannot explain. A withheld count
        // that carries its reason is disclosure in every language and no longer fires the rule.
        harness.SeedWithheldVocabularyRead(
            reason: SentenceStudio.Api.Coach.Tools.CoachScopeWithheldReason.None);

        var response = await AskAsync(harness, SilentAboutWithholdingAnswer());

        response.Status.Should().Be(CoachTurnStatus.Completed);
        response.Answer.Should().NotBeNull();

        harness.ClaimFindings.Record!.Findings.Should()
            .Contain(finding => finding.Rule == CoachClaimRuleCode.WithheldNotDisclosed);
        harness.ClaimFindings.Record.Refused.Should().BeFalse(
            "Repair records what it cannot fix and still delivers the answer");
    }

    // -------------------------------------------------------------- Enforce

    [Fact]
    public async Task Enforce_refuses_only_what_substitution_cannot_make_honest()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Enforce);
        // Undisclosed on purpose: a count the evidence panel cannot explain. A withheld count
        // that carries its reason is disclosure in every language and no longer fires the rule.
        harness.SeedWithheldVocabularyRead(
            reason: SentenceStudio.Api.Coach.Tools.CoachScopeWithheldReason.None);

        var response = await AskAsync(harness, SilentAboutWithholdingAnswer());

        response.Answer.Should().BeNull();
        response.Status.Should().Be(CoachTurnStatus.Rejected);
        response.StopReason.Should().Be(CoachStopReason.ValidationFailed);

        harness.ClaimFindings.Record!.Refused.Should().BeTrue();
        harness.ClaimFindings.Record.Findings.Should()
            .Contain(finding => finding.Action == CoachClaimRepairAction.Refused);

        harness.Db.CoachPlanRevisions.Should().BeEmpty("a refused answer writes nothing");
    }

    [Fact]
    public async Task Enforce_still_substitutes_what_it_can_rather_than_refusing_the_whole_turn()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Enforce);
        harness.SeedFailedRead();

        var response = await AskAsync(harness, TwoSpanAnswer());

        // Substitution first, refusal last. A repairable finding at Enforce is repaired, not
        // blocked — a grounding layer that refuses freely trains everyone to turn it off.
        response.Status.Should().Be(CoachTurnStatus.Completed);
        SpanText(response.Answer!, 0).Should().Be(CoachDeterministicCopy.UncheckedLearnerState);
        harness.ClaimFindings.Record!.Refused.Should().BeFalse();
    }

    // ------------------------------------------- the three inputs reach the rules

    [Fact]
    public async Task The_trace_reaches_the_rules()
    {
        // FabricatedCheck fires when a trace exists and shows no successful read.
        using var withTrace = new CoachApplicationHarness();
        withTrace.SetGroundingStage(CoachGroundingStage.Observe);
        withTrace.SeedFailedRead();
        await AskAsync(withTrace, ClaimsToHaveCheckedAnswer());

        withTrace.ClaimFindings.Record!.Findings.Should()
            .Contain(finding => finding.Rule == CoachClaimRuleCode.FabricatedCheck);

        // A turn that read nothing at all. This arm previously asserted the OPPOSITE — that no
        // finding was produced — and was labelled "no trace". It never was one: the harness wires
        // the real observation buffer, so this turn is observed and simply idle, and the projection
        // was collapsing it to null. That collapse is the trace-conflation defect, and this arm is
        // where it was visible in the application path the whole time.
        //
        // The genuine unobserved contrast cannot be expressed here — the harness always supplies a
        // buffer — so it lives where a null one can actually be passed:
        // CoachTraceConflationGateTests.An_unobserved_turn_is_not_convicted_on_evidence_that_was_
        // never_collected, and Project(null) in CoachTurnTraceShapeTests.
        using var readNothing = new CoachApplicationHarness();
        readNothing.SetGroundingStage(CoachGroundingStage.Observe);
        await AskAsync(readNothing, ClaimsToHaveCheckedAnswer());

        readNothing.ClaimFindings.Record!.Findings.Should()
            .Contain(
                finding => finding.Rule == CoachClaimRuleCode.FabricatedCheck,
                "the answer claims a read and the recorded turn made none, which is the strongest "
                + "case the rule has rather than one it should be blind to");
    }

    [Fact]
    public async Task The_evidence_reaches_the_rules()
    {
        // The seeded read withheld 4 of 14 and returned 10. "twelve" is supported by nothing the
        // server computed, and the rule can only know that from the evidence the turn built.
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Observe);
        harness.SeedWithheldVocabularyRead(matched: 14, returned: 10, withheld: 4);

        await AskAsync(harness, CountAnswer("You are tracking 12 words right now."));

        harness.ClaimFindings.Record!.Findings.Should()
            .Contain(finding => finding.Rule == CoachClaimRuleCode.CountClaimMismatch);

        using var supported = new CoachApplicationHarness();
        supported.SetGroundingStage(CoachGroundingStage.Observe);
        supported.SeedWithheldVocabularyRead(matched: 14, returned: 10, withheld: 4);

        await AskAsync(supported, CountAnswer("You are tracking 14 words right now."));

        supported.ClaimFindings.Record!.Findings.Should()
            .NotContain(finding => finding.Rule == CoachClaimRuleCode.CountClaimMismatch,
                "a number the evidence supports is not a mismatch");
    }

    [Fact]
    public void The_manifest_reaches_the_rules()
    {
        // A full turn cannot isolate this: the intent has no capability-proposal member yet, so a
        // real turn always proposes nothing. What this proves is that the evaluator hands the
        // resolver and the proposal list to the rules, so the day the intent carries one, the
        // capability rules see it.
        using var harness = new CoachApplicationHarness();

        var result = harness.Grounding.Evaluate(
            CoachGroundingStage.Observe,
            Compose(UnverifiedClaim),
            evidence: [],
            observations: null,
            proposedCapabilities: ["capability_this_build_does_not_declare"],
            capabilityStage: CoachCapabilityStage.Off,
            handshake: null);

        result.Record!.Findings.Should()
            .Contain(finding => finding.Rule == CoachClaimRuleCode.CapabilityAbsent);
    }

    // ------------------------------------------------------------ the router

    [Fact]
    public void Every_rule_fires_identically_with_the_router_off()
    {
        var manifest = ShippedManifest();
        var resolver = new CoachCapabilityResolver(manifest);
        var engine = new CoachClaimRuleEngine(resolver, manifest);

        var withRouter = new CoachTurnGroundingEvaluator(
            engine, resolver, NullLogger.Instance, new CoachShadowClaimRouter(), new CoachClaimFindingBuffer());

        var withoutRouter = new CoachTurnGroundingEvaluator(
            engine, resolver, NullLogger.Instance, router: null, findings: new CoachClaimFindingBuffer());

        var cases = 0;
        foreach (var stage in new[]
                 {
                     CoachGroundingStage.Observe, CoachGroundingStage.Repair, CoachGroundingStage.Enforce
                 })
        {
            var routed = withRouter.Evaluate(
                stage, Compose(UnverifiedClaim), [], null, ["absent_capability"],
                CoachCapabilityStage.Off, null);

            var bare = withoutRouter.Evaluate(
                stage, Compose(UnverifiedClaim), [], null, ["absent_capability"],
                CoachCapabilityStage.Off, null);

            routed.Record!.Findings.Select(f => (f.Rule, f.Action))
                .Should().BeEquivalentTo(bare.Record!.Findings.Select(f => (f.Rule, f.Action)));
            routed.Refused.Should().Be(bare.Refused);
            cases++;
        }

        cases.Should().Be(3, "the equivalence must hold on every rung that runs a rule");
    }

    [Fact]
    public void The_router_label_is_recorded_and_never_consulted()
    {
        // B5 as a compile-time fact: there is no member on the context a label could occupy. This
        // asserts the observable half — the label is present in the record, so it is produced, and
        // the equivalence test above proves it changed nothing.
        typeof(CoachClaimRuleContext).GetProperties()
            .Should().NotContain(property => property.PropertyType == typeof(CoachShadowRouteLabel));

        using var harness = new CoachApplicationHarness();

        var result = harness.Grounding.Evaluate(
            CoachGroundingStage.Observe, Compose(UnverifiedClaim), [], null, [],
            CoachCapabilityStage.Off, null);

        result.Record!.ShadowLabel.Should().Be(CoachShadowRouteLabel.LearnerState);
    }

    // ---------------------------------------------------------------- safety

    [Fact]
    public async Task Nothing_recorded_carries_learner_or_model_text()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Repair);
        harness.SeedWithheldVocabularyRead();

        await AskAsync(harness, TwoSpanAnswer());

        var record = harness.ClaimFindings.Record;
        record.Should().NotBeNull();

        // Every member of a finding is a closed code or a bounded number. Asserted structurally so
        // a text member added later fails here rather than in a log review.
        typeof(CoachClaimFinding).GetProperties()
            .Should().OnlyContain(property =>
                property.PropertyType == typeof(CoachClaimRuleCode)
                || property.PropertyType == typeof(CoachClaimRepairAction)
                || property.PropertyType == typeof(int?));
    }

    [Fact]
    public async Task A_clean_answer_is_untouched_at_every_rung()
    {
        var cases = 0;
        foreach (var stage in Enum.GetValues<CoachGroundingStage>())
        {
            using var harness = new CoachApplicationHarness();
            harness.SetGroundingStage(stage);
            harness.SeedPracticeBalanceRead();

            var response = await AskAsync(harness, InstructionalOnlyAnswer());

            response.Status.Should().Be(CoachTurnStatus.Completed, "stage {0} found nothing", stage);
            SpanText(response.Answer!, 0).Should().Be(InstructionalText);
            cases++;
        }

        cases.Should().Be(4, "all four rungs must leave an honest answer alone");
    }

    // ----------------------------------------------------------- both arms

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task The_ladder_runs_whichever_arm_served_the_turn(CoachImplementation arm)
    {
        using var harness = new CoachApplicationHarness();
        harness.Options.CurrentValue.Implementation = arm;
        harness.SetGroundingStage(CoachGroundingStage.Repair);
        harness.SeedFailedRead();

        var response = await AskAsync(harness, TwoSpanAnswer());

        SpanText(response.Answer!, 0).Should().Be(CoachDeterministicCopy.UncheckedLearnerState,
            "the ladder sits downstream of the arm, so neither route can skip it");
        harness.ClaimFindings.Record!.AnswerAltered.Should().BeTrue();
    }

    [Fact]
    public void There_is_exactly_one_grounding_call_site()
    {
        // Both answer-producing reducers reach the ladder because both call BuildAnswerAsync, and
        // BuildAnswerAsync calls it once. A second call site would mean an answer could be scanned
        // twice — substituting a substitution — or that one branch had quietly acquired its own
        // policy.
        var service = File.ReadAllText(Path.Combine(
            ApiSourceRoot(), "Coach", "Application", "CoachSessionService.cs"));

        CountOutsideComments(service, "ApplyGroundingAsync(").Should().Be(
            2, "one declaration and exactly one invocation");

        CountOutsideComments(service, "await BuildAnswerAsync(").Should().Be(
            2, "the two answer-producing reducers, both of which therefore reach the ladder");
    }

    [Fact]
    public void RecordBudget_is_called_exactly_once_in_each_arm()
    {
        // W4 Amendment A1 authorises one call per arm at the turn boundary. Twice in one arm would
        // double-count a budget the trace reports as a fact, and the second value would win
        // silently.
        var arms = new[] { "BaselineLearningCoach.cs", "HarnessLearningCoach.cs" };

        var counted = 0;
        foreach (var arm in arms)
        {
            var path = Path.Combine(ApiSourceRoot(), "Coach", "Agents", arm);
            File.Exists(path).Should().BeTrue($"{arm} must be where the guard thinks it is");

            CountOutsideComments(File.ReadAllText(path), "RecordBudget(").Should().Be(
                1, $"{arm} records the budget once, at the turn boundary");
            counted++;
        }

        counted.Should().Be(2, "there are exactly two arms and both were examined");
    }

    /// <summary>Occurrences of <paramref name="token"/> with line comments stripped first.</summary>
    private static int CountOutsideComments(string source, string token)
    {
        var code = string.Join('\n', source.Split('\n').Select(line =>
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment >= 0 ? line[..comment] : line;
        }));

        var count = 0;
        var index = 0;
        while ((index = code.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string ApiSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        return Path.Combine(directory!.FullName, "src", "SentenceStudio.Api");
    }

    // ------------------------------------------------------------------- DI

    [Fact]
    public void The_evaluator_is_registered_beside_the_engine()
    {
        // The engine was registered and never resolved. The evaluator is registered by the same
        // extension method, so the two cannot be wired independently — and the session service
        // takes it as an optional constructor parameter, which DI fills only when it is registered.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddCoachReadOnlyTools();

        services.Should().Contain(d => d.ServiceType == typeof(CoachClaimRuleEngine));
        services.Should().Contain(d => d.ServiceType == typeof(CoachTurnGroundingEvaluator),
            "an engine with no registered caller is the defect this workstream was rejected for");
        services.Should().Contain(d => d.ServiceType == typeof(ICoachClaimFindingBuffer));
    }

    [Fact]
    public void The_session_service_accepts_the_evaluator_so_the_container_can_supply_it()
    {
        // The parameter is optional so every existing construction keeps compiling. Optional means
        // DI supplies it when the type is registered and passes null when it is not — so the
        // assertion that matters is that the parameter exists and is the evaluator's type.
        var parameter = typeof(CoachSessionService).GetConstructors().Single()
            .GetParameters()
            .SingleOrDefault(p => p.ParameterType == typeof(CoachTurnGroundingEvaluator));

        parameter.Should().NotBeNull(
            "without this parameter the registration above is decoration");
        parameter!.IsOptional.Should().BeTrue(
            "a required parameter would break every host that does not register the tools");
    }

    // ------------------------------------------------- W7 limitation projection

    [Fact]
    public void A_capability_the_app_offers_elsewhere_becomes_a_typed_limitation()
    {
        // Plan B11: a limitation names a real screen, and the counts live in the DTO rather than
        // in the copy. This is the projection that produces it. It is asserted against a resolver
        // that reports PresentOnAnotherSurface because no shipped capability does yet.
        var limitation = CoachClaimLimitationProjection.Project(
            [new CoachClaimFinding(CoachClaimRuleCode.CapabilityAbsent, CoachClaimRepairAction.None)],
            ["some_capability"],
            new StubResolver(CoachCapabilityAvailability.PresentOnAnotherSurface),
            CoachCapabilityStage.Off,
            handshake: null);

        limitation.Should().NotBeNull();
        limitation!.Code.Should().Be(CoachLimitationCode.AvailableOnAnotherSurface);

        // Null, and honestly so: no capability descriptor declares a route, so this build cannot
        // say which screen. Naming a plausible one would be the fluent invention the whole
        // grounding layer exists to stop.
        limitation.Destination.Should().BeNull();
    }

    [Theory]
    [InlineData(CoachCapabilityAvailability.AbsentByDesign, CoachLimitationCode.RefusedByDesign)]
    [InlineData(CoachCapabilityAvailability.AbsentUnimplemented, CoachLimitationCode.NotBuilt)]
    [InlineData(CoachCapabilityAvailability.PresentOnAnotherSurface,
        CoachLimitationCode.AvailableOnAnotherSurface)]
    public void Each_absent_availability_states_the_boundary_it_actually_is(
        CoachCapabilityAvailability availability,
        CoachLimitationCode expected)
    {
        CoachClaimLimitationProjection.Project(
                [new CoachClaimFinding(CoachClaimRuleCode.CapabilityAbsent, CoachClaimRepairAction.None)],
                ["some_capability"],
                new StubResolver(availability),
                CoachCapabilityStage.Off,
                handshake: null)!
            .Code.Should().Be(expected);
    }

    [Theory]
    [InlineData(CoachCapabilityAvailability.Unknown)]
    [InlineData(CoachCapabilityAvailability.Present)]
    public void An_availability_that_states_no_boundary_produces_no_limitation(
        CoachCapabilityAvailability availability)
    {
        // Unknown is undeterminable rather than absent, and Present contradicts the finding.
        // Neither is a boundary this build can state, so it states none rather than the nearest one.
        CoachClaimLimitationProjection.Project(
            [new CoachClaimFinding(CoachClaimRuleCode.CapabilityAbsent, CoachClaimRepairAction.None)],
            ["some_capability"],
            new StubResolver(availability),
            CoachCapabilityStage.Off,
            handshake: null).Should().BeNull();
    }

    [Fact]
    public void A_finding_that_is_not_about_a_capability_produces_no_limitation()
    {
        CoachClaimLimitationProjection.Project(
            [new CoachClaimFinding(CoachClaimRuleCode.CountClaimMismatch, CoachClaimRepairAction.Substituted)],
            ["some_capability"],
            new StubResolver(CoachCapabilityAvailability.PresentOnAnotherSurface),
            CoachCapabilityStage.Off,
            handshake: null).Should().BeNull();
    }

    [Fact]
    public void The_judged_context_carries_the_same_typed_limitation_the_record_does()
    {
        // The dispute exit resolves on a typed limitation rather than on prose, and it reads that
        // limitation off the judged context the evaluator hands back. Two things have to hold for
        // the live path to work, and neither is visible from a rule test that builds its own
        // context: the projection has to reach the context at all, and it has to be the same
        // projection the record was built from. Running it twice would let the exit clear a dispute
        // the record says was never bounded.
        var manifest = ShippedManifest();
        var resolver = new StubResolver(CoachCapabilityAvailability.AbsentUnimplemented);
        var engine = new CoachClaimRuleEngine(resolver, manifest);

        var evaluator = new CoachTurnGroundingEvaluator(
            engine, resolver, NullLogger.Instance, router: null, findings: new CoachClaimFindingBuffer());

        var result = evaluator.Evaluate(
            CoachGroundingStage.Observe,
            Compose(UnverifiedClaim),
            evidence: [],
            observations: null,
            proposedCapabilities: ["absent_capability"],
            capabilityStage: CoachCapabilityStage.Off,
            handshake: null);

        result.Record!.Limitation.Should().NotBeNull("the capability is absent, so the turn is bounded");

        result.Context.Should().NotBeNull("Observe judges the turn, so there is a context to resolve against");
        result.Context!.Limitation.Should().BeSameAs(
            result.Record.Limitation,
            "the exit and the record must be answering from one projection, not two that could drift");
    }

    [Fact]
    public void A_turn_that_states_no_boundary_leaves_the_judged_context_unbounded()
    {
        // Null is not a limitation, and nothing infers one from absence. Without this, every turn
        // during an open dispute would arrive carrying something, and the exit would have to guess
        // which somethings meant "I can't".
        using var harness = new CoachApplicationHarness();

        var result = harness.Grounding.Evaluate(
            CoachGroundingStage.Observe, Compose(UnverifiedClaim), [], null, [],
            CoachCapabilityStage.Off, null);

        result.Record!.Limitation.Should().BeNull();
        result.Context!.Limitation.Should().BeNull(
            "an answer that declared no boundary must not be able to clear a dispute by arriving");
    }

    private sealed class StubResolver(CoachCapabilityAvailability availability) : ICoachCapabilityResolver
    {
        public CoachCapabilityAvailability Resolve(
            string name,
            CoachCapabilityStage currentStage,
            CoachClientCapabilityHandshake? handshake) => availability;
    }

    // --------------------------------------------------------------- helpers

    private static CoachCapabilityManifest ShippedManifest() =>
        new(SentenceStudio.Api.Coach.Tools.CoachToolServiceCollectionExtensions.BuildValidatedRegistry(
            new SentenceStudio.Api.Coach.Runtime.CoachOptions()));

    private static string SpanText(CoachAnswerDto answer, int index) =>
        answer.Blocks.SelectMany(block => block.Spans).ElementAt(index).Text;

    private static CoachAnswerDto Compose(params string[] spans) => new()
    {
        Topic = CoachAnswerTopic.Vocabulary,
        Blocks =
        [
            new CoachAnswerBlockDto
            {
                Kind = CoachAnswerBlockKind.Answer,
                Spans = [.. spans.Select(text => new CoachAnswerSpanDto
                {
                    Text = text,
                    Language = CoachLanguageRole.Display,
                    LanguageTag = "en"
                })]
            }
        ],
        PlainText = string.Join(" ", spans),
        TargetLanguageTag = "ko",
        DisplayLanguageTag = "en"
    };

    private static async Task<CoachTurnResponse> AskAsync(
        CoachApplicationHarness harness,
        CoachPedagogicalAnswerIntent answer)
    {
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                PedagogicalAnswer = answer,
                CoachMessage = string.Empty
            }
        };

        var result = await harness.Service.SubmitTurnAsync(
            sessionId,
            new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = Question },
            CoachTurnExecutionContext.Default);

        result.IsOk.Should().BeTrue();
        return result.Value!;
    }

    private static CoachPedagogicalAnswerIntent TwoSpanAnswer() =>
        Answer(UnverifiedClaim, InstructionalText);

    private static CoachPedagogicalAnswerIntent InstructionalOnlyAnswer() =>
        Answer(InstructionalText);

    private static CoachPedagogicalAnswerIntent ClaimsToHaveCheckedAnswer() =>
        Answer("I checked your practice history and it looks steady.");

    private static CoachPedagogicalAnswerIntent SilentAboutWithholdingAnswer() =>
        Answer(InstructionalText);

    private static CoachPedagogicalAnswerIntent CountAnswer(string sentence) => Answer(sentence);

    private static CoachPedagogicalAnswerIntent Answer(params string[] spans) => new()
    {
        Topic = CoachAnswerTopic.Vocabulary,
        Blocks =
        [
            new CoachAnswerBlockIntent
            {
                Kind = CoachAnswerBlockKind.Answer,
                Spans = [.. spans.Select(text => new CoachAnswerSpanIntent
                {
                    Text = text,
                    Language = CoachLanguageRole.Display
                })]
            }
        ]
    };
}
