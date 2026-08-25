using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// The gate's anti-vacuity layer: the soak's zeros are only evidence if something was counting.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this file exists to make impossible.</b> A soak artifact reading twelve zeros is
/// produced identically by a host running the grounding layer perfectly and by a host where the
/// layer never executed — a mis-set stage, a metric nobody registered, an exporter that dropped the
/// meter. Every one of those reads as a clean gate. So the artifact needs three things beside the
/// numerators: a strictly positive denominator, a canary proving delivery, and a demonstration that
/// each measured class <em>can</em> be driven off zero.
/// </para>
/// <para>
/// <b>Scope, and what this does not prove.</b> These are local, synthetic, and prove gate condition
/// (a) — that the instrument is real and non-vacuous. They cannot prove condition (b), which is the
/// same instrument reading zero across a Captain-named production window with real traffic behind
/// the denominator. See <c>docs/sam-foundation-gate-soak-runbook.md</c>.
/// </para>
/// <para>
/// <b>Relationship to R2's own suite.</b> <c>CoachGroundingProjectionAndMetricsTests</c> owns the
/// projection contract and the per-counter behaviour. This file owns the gate's reading of it: the
/// denominator rule stated as the gate states it, one positive <em>and</em> one zero fixture for
/// each of the seven soak-measured classes, and the canary's single delivery.
/// </para>
/// </remarks>
[Collection(GlobalTelemetryListenerCollection.Name)]
public sealed class CoachGroundingNonVacuityTests
{
    /// <summary>
    /// The seven rule classes the soak reads as measured zeros. Ceremony finding F3, bucket one.
    /// </summary>
    /// <remarks>
    /// <c>CountClaimMismatch</c> and <c>WithheldNotDisclosed</c> are two codes and one §16.1 row —
    /// row 5 reads "count-mismatch rate after Enforce, including withheld not disclosed". They are
    /// listed separately here because the counter tags them separately; the KQL groups them, and
    /// <see cref="The_two_codes_the_query_groups_are_both_present_and_distinct"/> keeps the two
    /// facts from drifting apart.
    /// </remarks>
    public static readonly IReadOnlyList<CoachClaimRuleCode> SoakMeasured =
    [
        CoachClaimRuleCode.NegativeClaimWithoutCoverage,
        CoachClaimRuleCode.FabricatedCheck,
        CoachClaimRuleCode.FalseLimitation,
        CoachClaimRuleCode.OrderClaimMismatch,
        CoachClaimRuleCode.CountClaimMismatch,
        CoachClaimRuleCode.WithheldNotDisclosed,
        CoachClaimRuleCode.RepeatedDisputedClaim
    ];

    public static TheoryData<CoachClaimRuleCode> SoakMeasuredCases()
    {
        var data = new TheoryData<CoachClaimRuleCode>();

        foreach (var code in SoakMeasured)
        {
            data.Add(code);
        }

        return data;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The denominator
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One evaluated turn produces exactly one denominator point, and the point is strictly positive.
    /// </summary>
    /// <remarks>
    /// "Exactly one" and "positive" are separate failures. A counter added twice inflates every rate
    /// the soak reads; a counter added zero times voids the artifact. Both are asserted because a
    /// test for one would pass while the other was broken.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void One_evaluated_turn_emits_exactly_one_strictly_positive_denominator_point()
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(Summary(CoachGroundingStage.Enforce));

        probe.Count(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            1, "the denominator is one point per evaluated turn, not one per finding");

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().BePositive(
            "a soak window whose denominator reads zero is a window where nothing ran, and its "
            + "numerator zeros prove nothing at all");
    }

    /// <summary>Ten turns produce ten points. The denominator scales with traffic, not with findings.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void The_denominator_counts_turns_and_not_findings()
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        for (var turn = 0; turn < 10; turn++)
        {
            metrics.RecordTurn(Summary(
                CoachGroundingStage.Enforce,
                findings:
                [
                    new CoachClaimFinding(CoachClaimRuleCode.FabricatedCheck, CoachClaimRepairAction.ObservedOnly),
                    new CoachClaimFinding(CoachClaimRuleCode.CountClaimMismatch, CoachClaimRepairAction.Substituted, 0, 0)
                ]));
        }

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            10, "twenty findings across ten turns is still ten turns. A denominator that tracked "
            + "findings would make a noisy window look like a busy one and flatter every rate");
    }

    /// <summary>
    /// Off emits nothing at all — not a zero-valued point, nothing.
    /// </summary>
    /// <remarks>
    /// This is the rung the rollback ladder ends at, and the reason the runbook insists a soak
    /// window records the configured stage. An Off window is not a clean window; it is an unmeasured
    /// one, and it is indistinguishable from a clean one by numerators alone.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void Off_evaluates_nothing_so_the_denominator_stays_at_zero()
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        var engine = new CoachClaimRuleEngine(new StubCapabilityResolver(), new StubCapabilityManifest());
        var evaluator = new CoachTurnGroundingEvaluator(
            engine,
            new StubCapabilityResolver(),
            NullLogger.Instance,
            router: null,
            findings: null,
            metrics: metrics);

        var result = evaluator.Evaluate(
            CoachGroundingStage.Off,
            ClaimFixture.Answer("You have been practising verbs a lot lately."),
            evidence: [],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        result.Record.Should().BeNull("Off must be indistinguishable from a build with no layer");

        probe.Count(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            0,
            "Off did not evaluate the turn, so counting it would put un-judged traffic into the "
            + "denominator every rate is read over");
    }

    /// <summary>An evaluated turn on a promoted rung does reach the denominator.</summary>
    /// <remarks>
    /// The control for the test above. Without it, a wiring mistake that stopped the evaluator
    /// touching the metrics at all would make the Off assertion pass for the wrong reason.
    /// </remarks>
    [Theory]
    [InlineData(CoachGroundingStage.Observe)]
    [InlineData(CoachGroundingStage.Repair)]
    [InlineData(CoachGroundingStage.Enforce)]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void Every_promoted_rung_reaches_the_denominator_through_the_real_evaluator(
        CoachGroundingStage stage)
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        var engine = new CoachClaimRuleEngine(new StubCapabilityResolver(), new StubCapabilityManifest());
        var evaluator = new CoachTurnGroundingEvaluator(
            engine,
            new StubCapabilityResolver(),
            NullLogger.Instance,
            router: null,
            findings: null,
            metrics: metrics);

        evaluator.Evaluate(
            stage,
            ClaimFixture.Answer("Here is a plain sentence."),
            evidence: [],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        probe.Count(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            1,
            "{0} is a promoted rung, so the turn was judged and belongs in the denominator even "
            + "though it produced no finding. Counting only guilty turns would make the rate "
            + "one over one on every window",
            stage);

        probe.TagsFor(CoachGroundingMetrics.TurnsEvaluatedName)[0][CoachGroundingTags.Stage]
            .Should().Be(stage.ToString(), "the soak query splits the denominator by rung");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-class positive and zero fixtures
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each soak-measured class has a fixture that drives its counter positive.
    /// </summary>
    /// <remarks>
    /// The positive half of the pair. A class with only a zero fixture is a class nobody has shown
    /// can fire, and a permanently unfirable counter reading zero is the registered-but-unreachable
    /// defect the W6 and W8 reviews each caught — reappearing as gate evidence.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SoakMeasuredCases))]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void Each_soak_measured_class_has_a_fixture_that_drives_it_positive(
        CoachClaimRuleCode code)
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(Summary(
            CoachGroundingStage.Enforce,
            findings: [new CoachClaimFinding(code, CoachClaimRepairAction.ObservedOnly)]));

        var points = probe.TagsFor(CoachGroundingMetrics.FindingsName)
            .Where(tags => Equals(tags[CoachGroundingTags.RuleCode], code.ToString()))
            .ToList();

        points.Should().ContainSingle(
            "{0} must be able to reach the findings counter under its own tag. A class the "
            + "instrument cannot express reads zero forever and means nothing",
            code);

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().BePositive(
            "even the positive fixture carries a denominator; a numerator with no denominator is "
            + "a count, not a rate");
    }

    /// <summary>
    /// Each soak-measured class has a fixture that leaves its counter at zero.
    /// </summary>
    /// <remarks>
    /// The zero half. Without it, a counter that fired on every turn regardless of the finding would
    /// pass the positive test above and make every soak window look catastrophic — or, with an
    /// inverted tag, look clean.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SoakMeasuredCases))]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void Each_soak_measured_class_has_a_fixture_that_leaves_it_at_zero(
        CoachClaimRuleCode code)
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        // A clean turn: judged, counted in the denominator, no finding of any class.
        metrics.RecordTurn(Summary(CoachGroundingStage.Enforce, findings: []));

        probe.TagsFor(CoachGroundingMetrics.FindingsName)
            .Where(tags => Equals(tags[CoachGroundingTags.RuleCode], code.ToString()))
            .Should().BeEmpty(
                "{0} must read zero on a clean turn. This is the shape the soak expects to see for "
                + "the whole window, and it has to be reachable or the expected result is unreachable",
                code);

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            1,
            "the zero is only meaningful over a positive denominator. A window of zero findings and "
            + "zero turns is the artifact the gate must refuse");
    }

    /// <summary>
    /// The two codes §16.1 row 5 groups are both emitted, and they are distinguishable.
    /// </summary>
    /// <remarks>
    /// The KQL sums them into one row because the plan reads them as one invariant — "count-mismatch
    /// rate after Enforce, including withheld not disclosed". That grouping is only safe if both
    /// codes actually reach the counter under their own tags, so a future reader can decompose the
    /// row. A grouped row over a code that never fires is a row that half-measures.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void The_two_codes_the_query_groups_are_both_present_and_distinct()
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(Summary(
            CoachGroundingStage.Enforce,
            findings:
            [
                new CoachClaimFinding(CoachClaimRuleCode.CountClaimMismatch, CoachClaimRepairAction.Substituted, 0, 0),
                new CoachClaimFinding(CoachClaimRuleCode.WithheldNotDisclosed, CoachClaimRepairAction.ObservedOnly)
            ]));

        var codes = probe.TagsFor(CoachGroundingMetrics.FindingsName)
            .Select(tags => tags[CoachGroundingTags.RuleCode] as string)
            .ToList();

        codes.Should().BeEquivalentTo(
            [
                CoachClaimRuleCode.CountClaimMismatch.ToString(),
                CoachClaimRuleCode.WithheldNotDisclosed.ToString()
            ],
            "the query may sum them into one §16.1 row, but the instrument must keep them apart so "
            + "an operator reading a non-zero can tell an overstated count from an undisclosed "
            + "withholding — two different defects with two different fixes");
    }

    /// <summary>The gate's list of measured classes matches the plan's count exactly.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void The_measured_bucket_holds_exactly_seven_classes()
    {
        SoakMeasured.Should().HaveCount(
            7, "ceremony finding F3 puts seven of the twelve foundation invariants on a runtime "
            + "counter. A list that drifted would either over-claim measurement or silently drop a "
            + "class from the soak artifact");

        SoakMeasured.Should().OnlyHaveUniqueItems();

        SoakMeasured.Should().NotContain(
            CoachClaimRuleCode.SideEffectNotDisclosed,
            "invariant 9 is inactive until C1 — ProposedCapabilities is empty on every shipped turn, "
            + "so nothing can fire it. Reporting it as a measured zero would be a false artifact");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The canary
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The canary is invoked exactly once, and a listener proves the point arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gate's proof that the measurement pipeline exists at all. In the soak it runs
    /// against App Insights: one deliberate ping at the start of the window, and a window where it
    /// did not land is a window whose zeros are unreadable. Here it runs against a
    /// <see cref="MeterListener"/>, which proves the same link one hop short of the exporter.
    /// </para>
    /// <para>
    /// Exactly once matters. A canary fired twice would let a duplicate-delivery bug hide, and the
    /// whole point of this counter is that its expected value is known precisely.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void The_canary_is_emitted_once_and_the_listener_receives_it()
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        metrics.EmitCanary();

        probe.Count(CoachGroundingMetrics.CanaryName).Should().Be(
            1, "one call, one point. The soak's expected value for this counter is exactly one");

        probe.Total(CoachGroundingMetrics.CanaryName).Should().Be(
            1, "delivered, and with the value the harness will look for");
    }

    /// <summary>The canary touches no counter an operator reads for real defects.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void The_canary_leaves_every_real_counter_untouched()
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        metrics.EmitCanary();

        foreach (var name in new[]
                 {
                     CoachGroundingMetrics.TurnsEvaluatedName,
                     CoachGroundingMetrics.FindingsName,
                     CoachGroundingMetrics.TurnsRefusedName,
                     CoachGroundingMetrics.TurnsAlteredName,
                     CoachGroundingMetrics.TurnsSuppressedName
                 })
        {
            probe.Count(name).Should().Be(
                0,
                "{0} must stay clean. Proving delivery by firing a real defect counter would put a "
                + "fabricated finding into the series the gate then reads as evidence",
                name);
        }
    }

    /// <summary>
    /// No production source calls <see cref="CoachGroundingMetrics.EmitCanary"/>.
    /// </summary>
    /// <remarks>
    /// The canary's meaning depends on it being deliberate. One production caller and it stops being
    /// a probe and becomes background noise with an unknown expected value, at which point the soak
    /// can no longer tell "the pipeline works" from "something fired it".
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.StructurallyAbsent)]
    public void No_production_source_file_calls_the_canary()
    {
        var apiRoot = LocateApiSourceRoot();

        var callers = Directory
            .EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.EndsWith("CoachGroundingMetrics.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("EmitCanary(", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .ToList();

        callers.Should().BeEmpty(
            "the canary is test-only by contract. Production callers found: {0}",
            string.Join(", ", callers));
    }

    /// <summary>The scan above is non-vacuous: it is looking at a real, populated source tree.</summary>
    /// <remarks>
    /// A scan whose root resolved to an empty directory would report zero callers and read exactly
    /// like a clean result. This is the control that separates the two.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.StructurallyAbsent)]
    public void The_canary_source_scan_is_reading_a_populated_tree()
    {
        var apiRoot = LocateApiSourceRoot();

        Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Count(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Should().BeGreaterThan(
                100,
                "a scan over an empty or mis-rooted tree reports zero callers and passes. This "
                + "assertion is the difference between 'nobody calls it' and 'nothing was read'");

        File.ReadAllText(Path.Combine(
                apiRoot, "Coach", "Validation", "Claims", "CoachGroundingMetrics.cs"))
            .Should().Contain(
                "EmitCanary",
                "and the one file the scan deliberately excludes must be the one that declares it");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static CoachGroundingTurnSummary Summary(
        CoachGroundingStage stage,
        IReadOnlyList<CoachClaimFinding>? findings = null,
        bool refused = false,
        bool altered = false,
        bool suppressed = false) =>
        CoachGroundingTurnProjection.Project(new CoachClaimTurnRecord(
            stage,
            findings ?? [],
            refused,
            altered,
            CoachShadowRouteLabel.LearnerState,
            null,
            suppressed))!;

    /// <summary>Walks up from the test binary to the repository's API source root.</summary>
    private static string LocateApiSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SentenceStudio.Api");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "src/SentenceStudio.Api was not found above the test binary. The source scans in this "
            + "file cannot report a clean result they did not actually establish.");
    }

    /// <summary>
    /// A local <see cref="MeterListener"/> over the coach meter's grounding instruments.
    /// </summary>
    /// <remarks>
    /// Deliberately a private copy rather than a shared helper. A listener shared across suites is a
    /// process-global that collects another class's measurements when the runner parallelises, and
    /// the class-level <see cref="GlobalTelemetryListenerCollection"/> membership is the other half
    /// of that guard.
    /// </remarks>
    private sealed class MeterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Name, long Value, Dictionary<string, object?> Tags)> _points = [];
        private readonly object _gate = new();

        internal MeterProbe()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CoachTelemetry.MeterName
                    && instrument.Name.StartsWith("coach.grounding.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var tag in tags)
                {
                    copy[tag.Key] = tag.Value;
                }

                lock (_gate)
                {
                    _points.Add((instrument.Name, value, copy));
                }
            });

            _listener.Start();
        }

        internal long Total(string name)
        {
            lock (_gate)
            {
                return _points.Where(point => point.Name == name).Sum(point => point.Value);
            }
        }

        internal int Count(string name)
        {
            lock (_gate)
            {
                return _points.Count(point => point.Name == name);
            }
        }

        internal IReadOnlyList<Dictionary<string, object?>> TagsFor(string name)
        {
            lock (_gate)
            {
                return [.. _points.Where(point => point.Name == name).Select(point => point.Tags)];
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
