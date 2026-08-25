using FluentAssertions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.Capabilities;
using SentenceStudio.Api.Tests.Coach.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// The twelve §8.2 foundation invariants do not all read zero the same way. This is the census.
/// </summary>
/// <remarks>
/// <para>
/// <b>The artifact this prevents.</b> A soak report listing twelve zeros in one column invites the
/// reader to believe twelve counters were watched for a fortnight. Seven were. Two are build-time
/// checks dated by the build that ran them. Three cannot be produced by any code path in this
/// build and are evidenced by absence over the current registry. One — invariant 9,
/// <see cref="CoachClaimRuleCode.SideEffectNotDisclosed"/> — is registered and unreachable, and
/// listing it as a measured zero would be the false artifact in its purest form.
/// </para>
/// <para>
/// <b>Ceremony finding F3.</b> This file is the executable form of that finding: the buckets are
/// enumerated, counted, and kept from merging. A future change that promotes an invariant from one
/// bucket to another fails here first, which is the point — the promotion is a fact about the
/// evidence, and the artifact has to change with it.
/// </para>
/// <para>
/// <b>Re-arm conditions.</b> Every absence proof below names the milestone that ends it. An absence
/// proof with no expiry is a claim that the gap is permanent, and none of these are.
/// </para>
/// </remarks>
public sealed class CoachStructuralZeroInvariantTests
{
    /// <summary>Bucket one: a runtime counter over a real denominator. Seven classes.</summary>
    public static readonly IReadOnlyList<CoachClaimRuleCode> SoakMeasured =
        CoachGroundingNonVacuityTests.SoakMeasured;

    /// <summary>
    /// Bucket two: build-time structural checks. §8.2 invariants 11 and 12.
    /// </summary>
    /// <remarks>
    /// Dated by the build that ran them, not by a soak window. Putting a build result in a
    /// soak-window column would claim continuous observation of something checked once at compile
    /// time — true, but true for a different reason than the reader would infer.
    /// </remarks>
    public static readonly IReadOnlyList<string> BuildTimeStructural =
    [
        "invariant-11-registrations-outside-the-legal-matrix",
        "invariant-12-coach-tools-referring-to-application-db-context"
    ];

    /// <summary>
    /// Bucket three: no code path in this build can produce the event. §8.2 invariants 7, 8 and 10.
    /// </summary>
    /// <remarks>
    /// Evidenced by absence over the current registry and declaration set, each with a named re-arm
    /// milestone. These are the invariants where "zero" is a true statement about a capability that
    /// does not exist yet, not a measurement of one that does.
    /// </remarks>
    public static readonly IReadOnlyList<string> StructurallyAbsent =
    [
        "invariant-7-embargoed-term-in-a-quiz-cohort-or-launch-payload",
        "invariant-8-unauthorized-navigation",
        "invariant-10-cross-user-or-cross-circuit-presentation-writes"
    ];

    // ─────────────────────────────────────────────────────────────────────────
    // The census
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seven rule classes cover six invariants; six plus two plus three is eleven; invariant 9 is the
    /// twelfth and is in no bucket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arithmetic is the assertion, and the step that is easy to get wrong is the first one.
    /// Seven measured classes do not cover seven invariants: §16.1 row 5 reads "count-mismatch rate
    /// after Enforce, <em>including withheld not disclosed</em>", so
    /// <see cref="CoachClaimRuleCode.CountClaimMismatch"/> and
    /// <see cref="CoachClaimRuleCode.WithheldNotDisclosed"/> are two codes answering one invariant
    /// and the soak query groups them.
    /// </para>
    /// <para>
    /// Counting classes instead of invariants gives twelve and looks like a complete set, which is
    /// precisely the error that would let invariant 9 be reported as covered. The first draft of this
    /// test made it.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void The_three_buckets_account_for_eleven_of_twelve_with_invariant_nine_inactive()
    {
        SoakMeasured.Should().HaveCount(7, "seven rule classes are measured");
        BuildTimeStructural.Should().HaveCount(2);
        StructurallyAbsent.Should().HaveCount(3);

        SoakMeasured.Should().Contain(CoachClaimRuleCode.CountClaimMismatch);
        SoakMeasured.Should().Contain(CoachClaimRuleCode.WithheldNotDisclosed);

        // Seven codes, six invariants: the count/withheld pair answers §8.2 invariant 5 together.
        const int measuredInvariants = 6;

        (measuredInvariants + BuildTimeStructural.Count + StructurallyAbsent.Count).Should().Be(
            11,
            "eleven of the twelve §8.2 invariants have evidence of some kind. The twelfth is "
            + "invariant 9, which is inactive until C1 and is deliberately in no bucket. A sum of "
            + "twelve here would mean somebody counted the grouped pair twice or promoted "
            + "invariant 9 without making it reachable");
    }

    /// <summary>The three kinds of evidence are distinct, and the artifact must present them apart.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void The_three_evidence_kinds_are_distinct_labels()
    {
        new[]
            {
                CoachGateEvidence.SoakMeasured,
                CoachGateEvidence.BuildTime,
                CoachGateEvidence.StructurallyAbsent,
                CoachGateEvidence.InactiveUntilC1
            }
            .Should().OnlyHaveUniqueItems(
                "a soak-measured zero, a dated build result, an absence proof and an inactive rule "
                + "are four different claims. Collapsing any two of them into one column is how a "
                + "reader comes to believe a counter watched something nothing was watching");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Invariant 9 — inactive until C1, and never a measured zero
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="CoachClaimRuleCode.SideEffectNotDisclosed"/> is registered and cannot fire in this
    /// build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule needs a proposed capability with a declared side effect. The shipped turn path passes
    /// an empty capability list on every turn, so the rule's input is empty by construction and the
    /// counter would read zero for a fortnight without anything having been observed.
    /// </para>
    /// <para>
    /// <b>Re-arm condition C1.</b> When the client capability channel ships and the turn path begins
    /// proposing capabilities, this rule becomes reachable and moves into the soak-measured bucket.
    /// The person who lands C1 updates this test and the runbook's bucket table together.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.InactiveUntilC1)]
    public void Invariant_nine_is_registered_and_unreachable_so_it_is_not_a_measured_zero()
    {
        var engine = new CoachClaimRuleEngine(new StubCapabilityResolver(), new StubCapabilityManifest());

        engine.Rules.Select(rule => rule.Code).Should().Contain(
            CoachClaimRuleCode.SideEffectNotDisclosed,
            "the rule is registered — this is not a claim that it is missing");

        // The shape the shipped turn path actually produces: no proposed capabilities at all.
        var findings = engine.Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Sounds good."),
            ProposedCapabilities = [],
            Trace = ClaimFixture.EmptyTrace()
        });

        findings.Select(finding => finding.Rule).Should().NotContain(
            CoachClaimRuleCode.SideEffectNotDisclosed,
            "with an empty capability list the rule has no input, so its zero is a statement about "
            + "the turn path and not about learner traffic");

        SoakMeasured.Should().NotContain(
            CoachClaimRuleCode.SideEffectNotDisclosed,
            "and it must therefore stay out of the measured bucket. Re-arm at C1");
    }

    /// <summary>
    /// The rule is genuinely capable — it is unreachable, not broken.
    /// </summary>
    /// <remarks>
    /// Given the input the shipped path never supplies, the rule fires. Without this the "inactive"
    /// label would be indistinguishable from a rule that never worked, and C1 would inherit a
    /// silently dead check it believed it was re-arming.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.InactiveUntilC1)]
    public void Invariant_nine_fires_when_given_the_input_the_shipped_path_never_supplies()
    {
        var engine = new CoachClaimRuleEngine(
            new StubCapabilityResolver(),
            new StubCapabilityManifest().Declare(
                "apply_plan_change", CoachCapabilityEffectClass.LearnerData));

        var findings = engine.Scan(new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("Sounds good."),
            ProposedCapabilities = ["apply_plan_change"]
        });

        findings.Select(finding => finding.Rule).Should().Contain(
            CoachClaimRuleCode.SideEffectNotDisclosed,
            "the rule works. It is waiting on a caller, which is exactly what 'inactive until C1' "
            + "is meant to convey and exactly what a dead rule would not satisfy");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Invariant 7 — embargoed term in a quiz cohort or a launch payload
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// No capability in this build launches an activity, so there is no launch payload to leak into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invariant 7 has two halves. The launch-payload half is structurally absent: the capability
    /// stage ladder stops below <c>Launch</c> and nothing declares an <c>ActivityLaunch</c> effect,
    /// so the payload the invariant is about does not exist in any code path.
    /// </para>
    /// <para>
    /// <b>Re-arm condition C3.</b> The first <c>ActivityLaunch</c> declaration makes this reachable.
    /// At that point the absence proof becomes false and must be replaced by an embargo scan over
    /// the real payload type — this test failing is the trigger for that work.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.StructurallyAbsent)]
    public void Invariant_seven_no_declaration_launches_an_activity_so_no_launch_payload_exists()
    {
        CoachCapabilityDeclarations.All.Should().NotBeEmpty(
            "an empty declaration set would satisfy every absence claim below for the wrong reason");

        CoachCapabilityDeclarations.All.Should().NotContain(
            declaration => declaration.EffectClass == CoachCapabilityEffectClass.ActivityLaunch,
            "invariant 7's launch half is absent because nothing launches. Re-arm at C3, when the "
            + "first ActivityLaunch declaration lands and this proof stops being true");
    }

    /// <summary>
    /// Every registered tool result passes the embargo scan for the scope it declares — the whole
    /// registry, not a hand-kept list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction matters for the artifact: this half of invariant 7 is not "nothing can
    /// happen", it is "something scans". Reporting both halves as one absence would understate the
    /// guard on the half that is real.
    /// </para>
    /// <para>
    /// <b>Why this is not a restatement of <c>CoachToolRedactionTests</c>.</b> That suite scans a
    /// hand-maintained array of result types. A tool registered without being added to that array is
    /// unscanned and nothing says so. This drives the population from the registry itself and pins
    /// each type to the scope its registration declares, so registering a tool is enough to put it
    /// under the scan.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void Invariant_seven_the_result_half_scans_the_whole_registry_not_a_hand_list()
    {
        var registry = CapabilityFixtures.FrozenRegistry();

        registry.All.Should().NotBeEmpty("the scan below must have a population");

        var scanner = new CoachEmbargoScanner();

        foreach (var group in registry.All.GroupBy(registration => registration.EmbargoScope))
        {
            var result = scanner.ScanTypes(
                [.. group.Select(registration => registration.ResultType).Distinct()],
                group.Key);

            result.IsValid.Should().BeTrue(
                "every registered result type must pass the embargo scan for the scope it declares. "
                + "Scope {0}: {1}",
                group.Key,
                string.Join("; ", result.Violations.Select(violation => violation.Message)));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Invariant 8 — unauthorized navigation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing in this build can navigate, so navigation cannot be unauthorized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two facts together: no capability declares a route, and the limitation projection never
    /// states a destination. The second is the one that would leak first — a limitation naming a
    /// screen is one client change away from being a navigation, and W7 refused to state a
    /// destination it could not derive precisely so this invariant could stay structural.
    /// </para>
    /// <para>
    /// <b>Re-arm condition C1.</b> When a capability declares a route and the projection begins
    /// naming destinations, navigation becomes possible and this absence proof must be replaced by
    /// an authorization test — explicit learner imperative counts as authorization, per §8.2.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.StructurallyAbsent)]
    public void Invariant_eight_no_capability_declares_a_route_and_no_limitation_names_one()
    {
        var resolver = new StubCapabilityResolver().Declare(
            CoachCapabilityDeclarations.ThemeMetadataCapabilityName,
            CoachCapabilityAvailability.PresentOnAnotherSurface);

        var outcome = new CoachClaimRuleEngine(resolver, new StubCapabilityManifest()).Evaluate(
            new CoachClaimRuleContext
            {
                Answer = ClaimFixture.Answer("I'll switch you to the light theme now."),
                ProposedCapabilities = [CoachCapabilityDeclarations.ThemeMetadataCapabilityName],
                Stage = CoachCapabilityStage.Presentation,
                Trace = ClaimFixture.EmptyTrace()
            },
            CoachGroundingStage.Enforce);

        outcome.Limitation.Should().NotBeNull(
            "the redirect path must be exercised, or this proves only that nothing ran");

        outcome.Limitation!.Destination.Should().BeNull(
            "a limitation that names a screen is one client change away from being a navigation. "
            + "Re-arm at C1, when a capability declares a route");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Invariant 10 — cross-user or cross-circuit presentation writes
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every presentation-state declaration is device-scoped and none resolves usable, so there is no
    /// write to cross a circuit with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent facts, and either alone would be weaker than it looks. Device scope means a
    /// write could not address another circuit even if one existed; the ceiling means no write
    /// exists. Asserting only the ceiling would leave the invariant resting on a value C1 is
    /// expected to change.
    /// </para>
    /// <para>
    /// <b>Re-arm condition C1.</b> When the theme capability's ceiling lifts, the ceiling half of
    /// this proof expires and a real cross-circuit isolation test is required. The scope half should
    /// survive; if it does not, the change is introducing a session- or account-scoped presentation
    /// write and needs its own review.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.StructurallyAbsent)]
    public void Invariant_ten_presentation_state_is_device_scoped_and_not_yet_usable()
    {
        var presentation = CoachCapabilityDeclarations.All
            .Where(declaration =>
                declaration.EffectClass == CoachCapabilityEffectClass.PresentationState)
            .ToList();

        presentation.Should().NotBeEmpty(
            "the theme capability is the only presentation-state declaration and it must be here, "
            + "or this test is asserting over an empty set");

        presentation.Should().OnlyContain(
            declaration => declaration.Scope == CoachCapabilityScope.Device,
            "a device-scoped write cannot address another learner's circuit even in principle");

        presentation.Should().OnlyContain(
            declaration =>
                declaration.MaxAvailability != CoachCapabilityAvailability.Present,
            "and none of them resolves usable in this build, so there is no write at all. Re-arm at "
            + "C1: when the ceiling lifts, a real cross-circuit isolation test replaces this half");
    }

    /// <summary>
    /// The shipped stage ladder is below the rung any presentation write would need.
    /// </summary>
    /// <remarks>
    /// The configuration half of invariant 10. A declaration capped at the type level is one edit
    /// from being uncapped; a stage the operator has not promoted is a second, independent lock, and
    /// the artifact should be able to say both were closed.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.StructurallyAbsent)]
    public void Invariant_ten_the_default_stage_does_not_reach_the_presentation_rung()
    {
        var manifest = CapabilityFixtures.ManifestWith(CoachCapabilityDeclarations.ThemeMetadata);
        var resolver = new CoachCapabilityResolver(manifest);

        foreach (var stage in new[] { CoachCapabilityStage.Off, CoachCapabilityStage.Read })
        {
            resolver.Resolve(
                    CoachCapabilityDeclarations.ThemeMetadataCapabilityName,
                    stage,
                    CapabilityFixtures.Handshake(codes: [CoachClientCapabilityCode.ThemeMetadata]))
                .Should().NotBe(
                    CoachCapabilityAvailability.Present,
                    "at {0} the operator has not promoted to the rung a presentation write needs, "
                    + "so the stage is a second lock independent of the declaration ceiling",
                    stage);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The buckets must not merge
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A build-time invariant has no runtime counter, and this is deliberate.
    /// </summary>
    /// <remarks>
    /// Invariants 11 and 12 are checked by a validator and a source scan. Adding a permanently-zero
    /// counter for either would put a number in the soak column that no traffic could ever move, and
    /// a reader would take it for a measurement. The metrics file says so in prose; this says so in
    /// a way that fails if somebody adds one.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void No_counter_exists_for_a_structural_invariant()
    {
        var counters = typeof(CoachGroundingMetrics)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        counters.Should().NotBeEmpty("the counter names must be discoverable for this to mean anything");

        foreach (var forbidden in new[]
                 {
                     "legal_matrix", "matrix_violation", "db_context", "tool_boundary",
                     "navigation", "embargo", "cross_circuit", "launch_payload", "side_effect"
                 })
        {
            counters.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "a counter named for {0} would be permanently zero and would read in the artifact "
                + "as a measurement of something no traffic can move",
                forbidden);
        }
    }

    /// <summary>
    /// Every soak-measured class is a real rule the engine registers.
    /// </summary>
    /// <remarks>
    /// The reverse direction of the previous test. A measured bucket listing a code no rule produces
    /// would put a permanently-zero series into the measured column, which is the same false comfort
    /// arriving from the other side.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void Every_measured_class_is_backed_by_a_registered_rule()
    {
        var registered = new CoachClaimRuleEngine(
                new StubCapabilityResolver(), new StubCapabilityManifest())
            .Rules
            .Select(rule => rule.Code)
            .ToHashSet();

        foreach (var code in SoakMeasured)
        {
            registered.Should().Contain(
                code,
                "{0} sits in the measured bucket, so a rule must exist that can produce it. A "
                + "measured class with no rule is a zero nothing could have moved",
                code);
        }
    }

    /// <summary>
    /// Each absence proof names the milestone that ends it.
    /// </summary>
    /// <remarks>
    /// The re-arm conditions are part of the artifact, not commentary. A reader who cannot see when
    /// an absence proof expires will read it as permanent, and the three in bucket three all expire.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.StructurallyAbsent)]
    public void Every_structurally_absent_invariant_has_a_named_re_arm_milestone()
    {
        var reArm = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["invariant-7-embargoed-term-in-a-quiz-cohort-or-launch-payload"] = "C3",
            ["invariant-8-unauthorized-navigation"] = "C1",
            ["invariant-10-cross-user-or-cross-circuit-presentation-writes"] = "C1"
        };

        reArm.Keys.Should().BeEquivalentTo(
            StructurallyAbsent,
            "every absence proof carries an expiry, and every expiry belongs to a proof");

        reArm.Values.Should().OnlyContain(
            milestone => milestone == "C1" || milestone == "C3",
            "the two milestones that re-arm these are C1, which ships the client capability channel, "
            + "and C3, which ships activity launch");
    }
}
