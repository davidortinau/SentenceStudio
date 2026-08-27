using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.Claims;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// W9/J4. The counter the soak dashboard reads and the row the post-hoc investigation reads must
/// describe the same turn the same way, all the way into real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite exists, given the coverage that already surrounds it.</b> Three seams are each
/// already tested in isolation and none of the three tests the join:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>CoachResponseReportGroundingTests</c> proves a <see cref="CoachGroundingTurnSummary"/> projects
/// to the eight columns correctly — from a hand-built summary.
/// </item>
/// <item>
/// <c>CoachResponseReportGroundingPostgresTests</c> proves those columns exist, are nullable, round
/// trip, survive Down, and are erased with the row — again from a hand-built summary.
/// </item>
/// <item>
/// <c>CoachGroundingNonVacuityTests</c> proves the counters move for a real evaluated turn.
/// </item>
/// </list>
/// <para>
/// So the summary that reaches Postgres has never been the summary that a real turn produced, and
/// the row has never been compared against the metric counted for that same turn. The two are built
/// from one object precisely so they cannot disagree — this is the test that says so, and the test
/// that fails the day someone re-projects one of them separately.
/// </para>
/// <para>
/// <b>Why it matters to the gate rather than only to tidiness.</b> §8.2's foundation invariants are
/// read as counter values over a soak window. When a window shows a non-zero, the next step is to
/// open the stored report rows for that window and look at what happened. If the dashboard says
/// "refused" and the row says "altered", the investigation is over before it starts and the soak
/// result is unreadable in both directions: a real regression looks like a projection bug, and a
/// projection bug looks like a real regression.
/// </para>
/// <para>
/// <b>Condition (a) only.</b> Nothing here is production evidence. It proves the two surfaces agree
/// in this build; it does not prove any invariant reads zero anywhere.
/// </para>
/// <para>
/// Fixture rule (§14): synthetic shape only — counts, an order, a withheld count. No authentic text,
/// account identifier, term, or conversation identifier.
/// </para>
/// </remarks>
[Collection(GlobalTelemetryListenerCollection.Name)]
[Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
[Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
public sealed class CoachGroundingReportAgreementPostgresTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("grounding_report_agreement");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>
    /// One evaluated turn. The counters and the stored row are compared field by field.
    /// </summary>
    /// <remarks>
    /// The summary is taken from <c>result.Grounding</c> — the same instance the evaluator handed to
    /// <c>RecordTurn</c>. Re-deriving an equivalent summary here would test this test.
    /// </remarks>
    [PostgresFact]
    public async Task The_counted_turn_and_the_stored_row_describe_the_same_turn()
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        var evaluator = Evaluator(metrics);

        var result = evaluator.Evaluate(
            CoachGroundingStage.Repair,
            ClaimFixture.Answer("You have 42 words due this week."),
            evidence: [ClaimFixture.Evidence(matched: 84, returned: 20)],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        var summary = result.Grounding;

        summary.Should().NotBeNull(
            "a Repair-rung turn ran the ladder, so it must have produced a durable summary. A null "
            + "here would mean the metric fired on something that will never reach a report row");

        // ── What the dashboard counted ────────────────────────────────────────
        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(1);
        var countedFindings = probe.Count(CoachGroundingMetrics.FindingsName);
        var countedAltered = probe.Total(CoachGroundingMetrics.TurnsAlteredName);
        var countedRefused = probe.Total(CoachGroundingMetrics.TurnsRefusedName);
        var countedSuppressed = probe.Total(CoachGroundingMetrics.TurnsSuppressedName);

        countedFindings.Should().BeGreaterThan(
            0, "the fixture states a count no recorded count supports, so something must have fired");

        // ── What reaches Postgres ─────────────────────────────────────────────
        var facts = CoachResponseReportService.ProjectGrounding(summary);

        await using var db = _harness.NewContext();

        var row = Row("agree", facts);

        db.CoachResponseReports.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var read = await db.CoachResponseReports.AsNoTracking()
            .SingleAsync(entity => entity.Id == row.Id);

        // ── The two must agree ────────────────────────────────────────────────
        read.GroundingStage.Should().Be(
            (int)CoachGroundingStage.Repair,
            "the rung the metric was tagged with is the rung the row records. A soak reader filters "
            + "the dashboard by stage and then filters the rows by stage; the two filters must "
            + "select the same turns");

        read.GroundingFindingCount.Should().Be(
            (int)countedFindings,
            "the findings counter and the stored finding count are two readings of one number. If "
            + "they can differ, a window with N findings on the dashboard and M in the rows gives "
            + "an investigator no way to tell which one is the artefact");

        read.GroundingAltered.Should().Be(
            countedAltered == 1,
            "altered is a per-turn flag on both surfaces");

        read.GroundingRefused.Should().Be(
            countedRefused == 1,
            "and so is refused. Repair never refuses, so both must read false here");

        read.GroundingRefused.Should().BeFalse("Repair is below the refusing rung");

        read.GroundingRepairSuppressed.Should().Be(
            countedSuppressed == 1,
            "suppression is the axis kept separate from stage all the way into storage. An English "
            + "turn suppresses nothing, so both must read false");

        read.GroundingRuleCodes.Should().NotBeNullOrWhiteSpace(
            "the row must name which rules fired. A finding count with no codes tells an "
            + "investigator that something went wrong and nothing about what");

        read.GroundingRuleCodes!.Split(',').Should().Contain(
            nameof(CoachClaimRuleCode.CountClaimMismatch),
            "and the code it names is the rule the fixture provoked");
    }

    /// <summary>
    /// A refusing turn agrees too, on the axis that only Enforce can move.
    /// </summary>
    /// <remarks>
    /// Refusal is the one outcome a learner sees as a missing answer rather than a different one, so
    /// it is the one an investigator is most likely to be chasing. It is also the only axis the
    /// previous test cannot exercise, because Repair cannot refuse.
    /// </remarks>
    [PostgresFact]
    public async Task A_refused_turn_reaches_the_row_as_refused()
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        var result = Evaluator(metrics).Evaluate(
            CoachGroundingStage.Enforce,
            ClaimFixture.Answer("Here are your words."),
            evidence: [ClaimFixture.Evidence(withheld: 5)],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        result.Answer.Should().BeNull("a refused turn ships no answer");

        probe.Total(CoachGroundingMetrics.TurnsRefusedName).Should().Be(
            1, "withheld-not-disclosed has no substitute, so Enforce refuses");

        var facts = CoachResponseReportService.ProjectGrounding(result.Grounding);

        await using var db = _harness.NewContext();

        var row = Row("refused", facts);

        db.CoachResponseReports.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var read = await db.CoachResponseReports.AsNoTracking()
            .SingleAsync(entity => entity.Id == row.Id);

        read.GroundingRefused.Should().BeTrue(
            "the dashboard counted a refusal, so the row an investigator opens must show one");

        read.GroundingAltered.Should().BeFalse(
            "refused and altered are mutually exclusive: a turn that shipped nothing altered nothing");

        read.GroundingStage.Should().Be((int)CoachGroundingStage.Enforce);

        read.GroundingRuleCodes!.Split(',').Should().Contain(
            nameof(CoachClaimRuleCode.WithheldNotDisclosed));
    }

    /// <summary>
    /// Off writes no summary, so it writes no row. The bypass reaches storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off must be indistinguishable from a build with no grounding layer. A report row carrying a
    /// grounding stage of 0 would be a distinguishing mark — and worse, it would put rows into the
    /// soak corpus for the rung that is supposed to contribute nothing, giving every ratio a
    /// denominator that includes turns the ladder never judged.
    /// </para>
    /// <para>
    /// This does not need Postgres to be true, but it is asserted here because this is the suite that
    /// owns the metric-to-row relationship, and "no metric, therefore no row" is part of it.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Off_produces_no_summary_and_therefore_no_grounding_columns()
    {
        using var probe = new MeterProbe();
        using var metrics = new CoachGroundingMetrics();

        var result = Evaluator(metrics).Evaluate(
            CoachGroundingStage.Off,
            ClaimFixture.Answer("You have 42 words due this week."),
            evidence: [ClaimFixture.Evidence(matched: 84, returned: 20)],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            0, "Off does not evaluate, so it contributes no denominator");

        result.Grounding.Should().BeNull("and produces nothing durable to store");

        var facts = CoachResponseReportService.ProjectGrounding(result.Grounding);

        await using var db = _harness.NewContext();

        var row = Row("off", facts);

        db.CoachResponseReports.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var read = await db.CoachResponseReports.AsNoTracking()
            .SingleAsync(entity => entity.Id == row.Id);

        read.GroundingStage.Should().BeNull(
            "an Off turn leaves the grounding columns null rather than writing stage 0. Null means "
            + "'the ladder did not run'; 0 would mean 'the ladder ran at Off', and only one of "
            + "those is true");
        read.GroundingRefused.Should().BeNull();
        read.GroundingAltered.Should().BeNull();
        read.GroundingFindingCount.Should().BeNull();
        read.GroundingRuleCodes.Should().BeNull();
    }

    /// <summary>
    /// A synthetic report row carrying one projected grounding fact set. Shape only — no authentic
    /// identifier, text, or term (§14 fixture rule).
    /// </summary>
    private static CoachResponseReport Row(string marker, CoachResponseReportService.CoachGroundingReportFacts facts) => new()
    {
        Id = $"rep-{marker}-{Guid.NewGuid():N}"[..40],
        UserProfileId = $"user-{marker}",
        ConversationId = $"conv-{marker}",
        CoachMessageId = $"msg-{marker}",
        CoachMessageSequence = 2,
        RequestMessageId = $"req-{marker}",
        RequestMessageSequence = 1,
        Reason = CoachResponseReportReason.IncorrectOrMisleading,
        ResponseKind = CoachMessageKind.PedagogicalAnswer,
        ReportedAtUtc = new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc),
        SchemaVersion = 2,
        GroundingStage = facts.Stage,
        GroundingRefused = facts.Refused,
        GroundingAltered = facts.Altered,
        GroundingRepairSuppressed = facts.RepairSuppressed,
        GroundingFindingCount = facts.FindingCount,
        GroundingRuleCodes = facts.RuleCodes,
        GroundingLimitationCode = facts.LimitationCode,
        GroundingShadowLabel = facts.ShadowLabel
    };

    private static CoachTurnGroundingEvaluator Evaluator(CoachGroundingMetrics metrics)
    {
        var resolver = new StubCapabilityResolver();
        var manifest = new StubCapabilityManifest();

        return new CoachTurnGroundingEvaluator(
            new CoachClaimRuleEngine(resolver, manifest),
            resolver,
            NullLogger<CoachTurnGroundingEvaluator>.Instance,
            router: null,
            findings: null,
            metrics: metrics);
    }

    /// <summary>Reads the grounding counters out of the process meter for one test.</summary>
    private sealed class MeterProbe : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private readonly List<(string Name, long Value)> _measurements = [];
        private readonly Lock _gate = new();

        public MeterProbe()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CoachTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                lock (_gate)
                {
                    _measurements.Add((instrument.Name, value));
                }
            });

            _listener.Start();
        }

        public long Total(string name)
        {
            lock (_gate)
            {
                return _measurements.Where(entry => entry.Name == name).Sum(entry => entry.Value);
            }
        }

        public long Count(string name)
        {
            lock (_gate)
            {
                return _measurements.Count(entry => entry.Name == name);
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
