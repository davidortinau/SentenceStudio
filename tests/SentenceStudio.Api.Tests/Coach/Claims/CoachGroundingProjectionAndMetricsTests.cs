using System.Diagnostics.Metrics;
using System.Reflection;
using FluentAssertions;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// R2: the judged turn becomes a durable summary, and the summary becomes measurable.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate condition these serve.</b> The soak reads foundation invariants and requires zeros.
/// Zeros only mean something if something was counting, so the denominator here is load-bearing in
/// a way no numerator is: a window where <c>turns_evaluated</c> reads zero would produce the same
/// artifact as a host with the grounding layer deleted.
/// </para>
/// <para>
/// Every counter therefore has a fixture that drives it positive and a fixture that leaves it at
/// zero. A counter with only the first proves it can fire; a counter with only the second proves
/// nothing at all.
/// </para>
/// </remarks>
// Added by W9/J2. This class reads process-wide meter measurements; without joining the global
// telemetry collection it runs in parallel with the Coach/Gate metric suites and each observes the
// others' counter increments. Attribute only — no assertion in this file is changed.
[Collection(GlobalTelemetryListenerCollection.Name)]
public sealed class CoachGroundingProjectionAndMetricsTests
{
    private static CoachClaimTurnRecord Record(
        CoachGroundingStage stage = CoachGroundingStage.Enforce,
        bool refused = false,
        bool altered = false,
        bool suppressed = false,
        IReadOnlyList<CoachClaimFinding>? findings = null,
        CoachLimitationDto? limitation = null,
        CoachShadowRouteLabel label = CoachShadowRouteLabel.LearnerState) =>
        new(stage,
            findings ??
            [
                new CoachClaimFinding(CoachClaimRuleCode.FabricatedCheck, CoachClaimRepairAction.ObservedOnly),
                new CoachClaimFinding(CoachClaimRuleCode.UnverifiedLearnerStateClaim, CoachClaimRepairAction.Substituted, 0, 0),
                new CoachClaimFinding(CoachClaimRuleCode.UnverifiedLearnerStateClaim, CoachClaimRepairAction.Substituted, 0, 1)
            ],
            refused,
            altered,
            label,
            limitation,
            suppressed);

    // ── Projection is total ──────────────────────────────────────────────────

    [Fact]
    public void The_projection_fills_every_member()
    {
        var record = Record(
            stage: CoachGroundingStage.Enforce,
            refused: true,
            altered: false,
            suppressed: true,
            limitation: new CoachLimitationDto { Code = CoachLimitationCode.ExceedsSafeChangeScope });

        var summary = CoachGroundingTurnProjection.Project(record);

        summary.Should().NotBeNull();
        summary!.RequestedStage.Should().Be(CoachGroundingStage.Enforce);
        summary.SubstitutionAllowed.Should().BeFalse("suppression is the inverse of permission");
        summary.Refused.Should().BeTrue();
        summary.Altered.Should().BeFalse();
        summary.RepairSuppressedForLanguage.Should().BeTrue();
        summary.FindingCount.Should().Be(3);
        summary.LimitationCode.Should().Be(CoachLimitationCode.ExceedsSafeChangeScope);
        summary.ShadowLabel.Should().Be(CoachShadowRouteLabel.LearnerState);
        summary.IsWellFormed().Should().BeTrue("a projection must produce a summary the reader accepts");
    }

    /// <summary>
    /// No member is left at a default the caller has to remember to set.
    /// </summary>
    /// <remarks>
    /// Checked by driving every member away from its default and requiring the projection to carry
    /// each one through. A projection that dropped one would produce a report column that cannot
    /// tell "the turn did not refuse" from "nobody said".
    /// </remarks>
    [Fact]
    public void No_member_is_silently_defaulted()
    {
        var summary = CoachGroundingTurnProjection.Project(Record(
            stage: CoachGroundingStage.Repair,
            refused: true,
            altered: true,
            suppressed: true,
            limitation: new CoachLimitationDto { Code = CoachLimitationCode.NotBuilt },
            label: CoachShadowRouteLabel.CapabilityProposal))!;

        var defaults = CoachGroundingTurnProjection.Project(new CoachClaimTurnRecord(
            CoachGroundingStage.Off, [], false, false, CoachShadowRouteLabel.Unknown, null))!;

        foreach (var property in typeof(CoachGroundingTurnSummary)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.Name != nameof(CoachGroundingTurnSummary.RuleCounts)))
        {
            property.GetValue(summary).Should().NotBe(
                property.GetValue(defaults),
                "{0} reads the same for a fully-populated record and an empty one, which means the "
                + "projection is not carrying it",
                property.Name);
        }
    }

    [Fact]
    public void Rule_counts_are_unique_and_sorted()
    {
        var summary = CoachGroundingTurnProjection.Project(Record())!;

        summary.RuleCounts.Select(entry => entry.Rule).Should().OnlyHaveUniqueItems();
        summary.RuleCounts.Select(entry => (int)entry.Rule).Should().BeInAscendingOrder(
            "an unordered list makes two identical turns serialize to different bytes, which turns "
            + "a payload comparison into a coin flip");

        summary.RuleCounts.Should().BeEquivalentTo(
            [
                new CoachGroundingRuleCount(CoachClaimRuleCode.UnverifiedLearnerStateClaim, 2),
                new CoachGroundingRuleCount(CoachClaimRuleCode.FabricatedCheck, 1)
            ],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void An_absent_record_projects_to_null()
    {
        CoachGroundingTurnProjection.Project(null).Should().BeNull(
            "an Off deployment produces no record, and an all-zero summary would read as 'the layer "
            + "looked and found nothing'");
    }

    [Fact]
    public void A_clean_turn_projects_a_well_formed_empty_summary()
    {
        var summary = CoachGroundingTurnProjection.Project(new CoachClaimTurnRecord(
            CoachGroundingStage.Observe, [], false, false, CoachShadowRouteLabel.Instructional, null))!;

        summary.FindingCount.Should().Be(0);
        summary.RuleCounts.Should().BeEmpty();
        summary.LimitationCode.Should().BeNull();
        summary.IsWellFormed().Should().BeTrue();
    }

    /// <summary>The projection drops the pointer. Structural, not sampled.</summary>
    [Fact]
    public void The_projection_drops_block_and_span_indices()
    {
        var summary = CoachGroundingTurnProjection.Project(Record())!;

        typeof(CoachGroundingTurnSummary)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().NotContain(
                name => name.Contains("Index", StringComparison.OrdinalIgnoreCase),
                "findings carry indices and the stored answer sits in the same payload, so this is "
                + "the boundary where the pointer is dropped");

        System.Text.Json.JsonSerializer.Serialize(summary).Should().NotContain("blockIndex");
    }

    // ── Metrics ──────────────────────────────────────────────────────────────

    /// <summary>Collects grounding measurements from the real meter.</summary>
    private sealed class Probe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Name, long Value, Dictionary<string, object?> Tags)> _points = [];
        private readonly object _gate = new();

        internal Probe()
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

        internal IReadOnlyList<string> AllTagKeys()
        {
            lock (_gate)
            {
                return [.. _points.SelectMany(point => point.Tags.Keys).Distinct()];
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>The F2 positive control: exactly one denominator point per evaluated turn.</summary>
    [Fact]
    public void One_denominator_point_is_emitted_per_turn()
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(CoachGroundingTurnProjection.Project(Record())!);

        probe.Count(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            1, "one point per turn, or the denominator stops being a turn count");
        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(1);
    }

    [Fact]
    public void Three_turns_produce_three_denominator_points()
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        for (var turn = 0; turn < 3; turn++)
        {
            metrics.RecordTurn(CoachGroundingTurnProjection.Project(Record())!);
        }

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            3, "a soak window's denominator must be strictly positive and must count turns");
    }

    /// <summary>A clean turn still counts. The denominator is not a finding counter.</summary>
    [Fact]
    public void A_clean_turn_still_increments_the_denominator()
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(CoachGroundingTurnProjection.Project(new CoachClaimTurnRecord(
            CoachGroundingStage.Observe, [], false, false, CoachShadowRouteLabel.Unknown, null))!);

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            1,
            "if only defective turns counted, a healthy window would read zero over zero and the "
            + "artifact would be indistinguishable from a dead pipeline");

        probe.Count(CoachGroundingMetrics.FindingsName).Should().Be(0);
    }

    /// <summary>
    /// The seven soak-measured classes are all rule codes, and each drives the findings counter.
    /// </summary>
    [Theory]
    [InlineData(CoachClaimRuleCode.NegativeClaimWithoutCoverage)]
    [InlineData(CoachClaimRuleCode.FabricatedCheck)]
    [InlineData(CoachClaimRuleCode.FalseLimitation)]
    [InlineData(CoachClaimRuleCode.OrderClaimMismatch)]
    [InlineData(CoachClaimRuleCode.CountClaimMismatch)]
    [InlineData(CoachClaimRuleCode.WithheldNotDisclosed)]
    [InlineData(CoachClaimRuleCode.RepeatedDisputedClaim)]
    public void Every_soak_measured_class_is_countable(CoachClaimRuleCode rule)
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(CoachGroundingTurnProjection.Project(Record(
            findings: [new CoachClaimFinding(rule, CoachClaimRepairAction.ObservedOnly)]))!);

        probe.Total(CoachGroundingMetrics.FindingsName).Should().Be(
            1, "{0} is one of the seven classes the soak reads, so it must be observable", rule);

        probe.TagsFor(CoachGroundingMetrics.FindingsName).Should().ContainSingle()
            .Which[CoachGroundingTags.RuleCode].Should().Be(rule.ToString());
    }

    /// <summary>Each flag counter has a positive fixture and a zero fixture.</summary>
    [Theory]
    [InlineData(CoachGroundingMetrics.TurnsRefusedName, true, false, false)]
    [InlineData(CoachGroundingMetrics.TurnsAlteredName, false, true, false)]
    [InlineData(CoachGroundingMetrics.TurnsSuppressedName, false, false, true)]
    public void Each_flag_counter_fires_only_for_its_own_flag(
        string counter, bool refused, bool altered, bool suppressed)
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(CoachGroundingTurnProjection.Project(
            Record(refused: refused, altered: altered, suppressed: suppressed))!);

        probe.Total(counter).Should().Be(1, "{0} must fire for its own flag", counter);

        foreach (var other in new[]
                 {
                     CoachGroundingMetrics.TurnsRefusedName,
                     CoachGroundingMetrics.TurnsAlteredName,
                     CoachGroundingMetrics.TurnsSuppressedName
                 }.Where(name => name != counter))
        {
            probe.Total(other).Should().Be(
                0, "{0} must stay at zero when its flag is false", other);
        }
    }

    /// <summary>Refusal and suppression together, which is the Korean Enforce shape.</summary>
    [Fact]
    public void A_korean_enforce_refusal_counts_both_refused_and_suppressed()
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(CoachGroundingTurnProjection.Project(
            Record(refused: true, suppressed: true))!);

        probe.Total(CoachGroundingMetrics.TurnsRefusedName).Should().Be(1);
        probe.Total(CoachGroundingMetrics.TurnsSuppressedName).Should().Be(1);

        probe.TagsFor(CoachGroundingMetrics.TurnsRefusedName).Should().ContainSingle()
            .Which[CoachGroundingTags.SubstitutionSuppressed].Should().Be(
                true,
                "an operator reading a refusal spike must be able to tell the language-suppressed "
                + "ones apart without opening a row");
    }

    // ── Tag hygiene ──────────────────────────────────────────────────────────

    /// <summary>No tag outside the closed set ever reaches the meter.</summary>
    [Fact]
    public void Only_closed_tag_names_are_emitted()
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(CoachGroundingTurnProjection.Project(
            Record(refused: true, altered: true, suppressed: true))!);

        var emitted = probe.AllTagKeys();

        emitted.Should().NotBeEmpty("a tag scan over no points proves nothing");
        emitted.Should().BeSubsetOf(
            CoachGroundingTags.All,
            "an unbounded dimension is both a privacy leak and a billing incident, and the two "
            + "arrive together");
    }

    [Fact]
    public void No_tag_value_carries_learner_cardinality()
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(CoachGroundingTurnProjection.Project(Record())!);

        foreach (var tags in probe.TagsFor(CoachGroundingMetrics.FindingsName))
        {
            foreach (var (key, value) in tags)
            {
                if (value is not string text)
                {
                    value.Should().BeOfType<bool>("only closed codes and booleans are permitted");
                    continue;
                }

                var closed = Enum.GetNames<CoachClaimRuleCode>()
                    .Concat(Enum.GetNames<CoachGroundingStage>())
                    .ToArray();

                closed.Should().Contain(
                    text,
                    "tag {0} carried '{1}', which is not a member of any closed vocabulary",
                    key,
                    text);
            }
        }
    }

    /// <summary>Instrument names are pinned. A rename breaks every dashboard and every alert.</summary>
    [Theory]
    [InlineData(CoachGroundingMetrics.TurnsEvaluatedName, "coach.grounding.turns_evaluated")]
    [InlineData(CoachGroundingMetrics.FindingsName, "coach.grounding.findings")]
    [InlineData(CoachGroundingMetrics.TurnsRefusedName, "coach.grounding.turns_refused")]
    [InlineData(CoachGroundingMetrics.TurnsAlteredName, "coach.grounding.turns_altered")]
    [InlineData(CoachGroundingMetrics.TurnsSuppressedName, "coach.grounding.turns_suppressed")]
    [InlineData(CoachGroundingMetrics.CanaryName, "coach.grounding.canary")]
    public void Instrument_names_are_pinned(string actual, string expected)
    {
        actual.Should().Be(
            expected,
            "the soak artifact and any alert reference these by name; renaming one silently empties "
            + "a dashboard rather than breaking a build");
    }

    [Theory]
    [InlineData(CoachGroundingTags.Stage, "grounding_stage")]
    [InlineData(CoachGroundingTags.RuleCode, "grounding_rule_code")]
    [InlineData(CoachGroundingTags.Refused, "grounding_refused")]
    [InlineData(CoachGroundingTags.SubstitutionSuppressed, "grounding_substitution_suppressed")]
    public void Tag_names_are_pinned(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Fact]
    public void The_grounding_meter_is_the_existing_coach_meter()
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        metrics.RecordTurn(CoachGroundingTurnProjection.Project(Record())!);

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().BePositive(
            "the probe only listens to {0}, so a point arriving proves the instrument is on it",
            CoachTelemetry.MeterName);
    }

    // ── The canary ───────────────────────────────────────────────────────────

    /// <summary>
    /// The canary fires on demand and touches no real counter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How Jayne's soak test uses it.</b> Resolve <c>CoachGroundingMetrics</c> from the host,
    /// call <see cref="CoachGroundingMetrics.EmitCanary"/> once at the start of the window, and
    /// assert the <c>coach.grounding.canary</c> point arrives in App Insights. A window where the
    /// canary did not land is a window whose zeros prove nothing, and the artifact is void
    /// regardless of what the numerators say.
    /// </para>
    /// <para>
    /// It is separate from every real counter on purpose: firing a defect counter to prove delivery
    /// would put a fabricated finding into the series an operator reads for real ones.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_canary_fires_and_touches_no_real_counter()
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        metrics.EmitCanary();

        probe.Total(CoachGroundingMetrics.CanaryName).Should().Be(1);

        foreach (var real in new[]
                 {
                     CoachGroundingMetrics.TurnsEvaluatedName,
                     CoachGroundingMetrics.FindingsName,
                     CoachGroundingMetrics.TurnsRefusedName,
                     CoachGroundingMetrics.TurnsAlteredName,
                     CoachGroundingMetrics.TurnsSuppressedName
                 })
        {
            probe.Total(real).Should().Be(
                0,
                "{0} must stay untouched: a canary that moved a real counter would put a fabricated "
                + "finding into the numerator the gate reads",
                real);
        }
    }

    /// <summary>
    /// No production code path emits the canary.
    /// </summary>
    /// <remarks>
    /// A source scan over the shipped tree. The canary's whole value is that a point on that series
    /// means a human deliberately probed the pipeline; a production caller would make it noise.
    /// </remarks>
    [Fact]
    public void No_production_code_path_emits_the_canary()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Api")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();

        var root = Path.Combine(directory!.FullName, "src");
        var declaration = Path.Combine("Coach", "Validation", "Claims", "CoachGroundingMetrics.cs");
        var callers = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.EndsWith(declaration, StringComparison.Ordinal))
            {
                continue;
            }

            var code = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(file), @"^[ \t]*///?.*$", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Multiline);

            if (code.Contains("EmitCanary(", StringComparison.Ordinal))
            {
                callers.Add(Path.GetRelativePath(root, file));
            }
        }

        callers.Should().BeEmpty(
            "the canary means 'a human probed the pipeline'. A production caller turns it into "
            + "noise and the soak loses its positive control. Offending: {0}",
            string.Join(", ", callers));
    }

    // ── Performance ──────────────────────────────────────────────────────────

    /// <summary>
    /// Projection plus metric emission stays well inside the turn budget.
    /// </summary>
    /// <remarks>
    /// The ceremony's budget is 10ms p95 for the rules plus emission on a fixture. This measures the
    /// R2 half — projection and counters — with a listener attached, which is the expensive
    /// configuration. Counter.Add is a no-op when nothing listens, so an unlistened host is strictly
    /// faster than what is measured here.
    /// </remarks>
    [Fact]
    public void Projection_and_emission_stay_inside_the_turn_budget()
    {
        using var probe = new Probe();
        using var metrics = new CoachGroundingMetrics();

        var record = Record(refused: true, altered: true, suppressed: true);

        // Warm the instrument set and the reflection the serializer would otherwise pay for first.
        metrics.RecordTurn(CoachGroundingTurnProjection.Project(record)!);

        const int Iterations = 200;
        var samples = new List<double>(Iterations);

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            metrics.RecordTurn(CoachGroundingTurnProjection.Project(record)!);

            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p95 = samples[(int)(Iterations * 0.95)];

        p95.Should().BeLessThan(
            10,
            "the turn path budget is 10ms p95 for rules plus emission; R2's half must not consume it");
    }

    /// <summary>No new async I/O on the turn path.</summary>
    [Fact]
    public void The_metric_surface_is_synchronous()
    {
        var asyncMembers = typeof(CoachGroundingMetrics)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType)
                             || method.ReturnType == typeof(ValueTask)
                             || method.Name.EndsWith("Async", StringComparison.Ordinal))
            .Select(method => method.Name)
            .ToArray();

        asyncMembers.Should().BeEmpty(
            "a metric that awaited anything would put the observability layer inside the learner's "
            + "latency budget. Offending: {0}",
            string.Join(", ", asyncMembers));
    }
}
