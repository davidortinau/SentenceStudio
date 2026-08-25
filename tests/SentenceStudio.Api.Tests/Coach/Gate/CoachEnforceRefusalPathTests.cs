using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// F1 as the soak will read it: the Korean Enforce split, observed through the counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside <c>CoachKoreanEnforceTests</c>.</b> That suite owns the engine-level
/// truth of F1 — Enforce refuses a Korean structural finding, a Korean turn of substitutable
/// findings ships unaltered, and the old stage-collapse would have shipped what Enforce now refuses.
/// This file does not restate any of that. It asserts the part the gate depends on and the engine
/// suite does not touch: that the split is <em>visible in the instrument the soak reads</em>.
/// </para>
/// <para>
/// <b>Why the distinction is load-bearing.</b> Suppression and refusal are different outcomes with
/// different learner costs, and the soak artifact separates them. If both landed on
/// <c>turns_refused</c>, a Korean-heavy window would read as a refusal spike and a rollback would
/// be ordered for a working system. If neither did, a real Korean refusal regression would be
/// invisible for the whole window. The engine can be perfectly correct and the artifact still
/// unreadable, which is exactly the seam these tests sit in.
/// </para>
/// <para>
/// <b>Non-firing coverage.</b> Korean teaching content — Sino-Korean and native numerals, 시/분 time
/// expressions, counters, dates — must produce no finding <em>and still reach the denominator</em>.
/// The engine suite proves the first. The second is the zero fixture the soak's rates are read over,
/// and without it a clean Korean window is indistinguishable from an unmeasured one.
/// </para>
/// </remarks>
[Collection(GlobalTelemetryListenerCollection.Name)]
public sealed class CoachEnforceRefusalPathTests
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

    private static (CoachTurnGroundingEvaluator Evaluator, CoachGroundingMetrics Metrics) Wire()
    {
        var metrics = new CoachGroundingMetrics();

        var evaluator = new CoachTurnGroundingEvaluator(
            Engine(),
            new StubCapabilityResolver(),
            NullLogger.Instance,
            router: null,
            findings: null,
            metrics: metrics);

        return (evaluator, metrics);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Refusal is visible, and it carries a denominator
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Korean structural finding at Enforce refuses, and the refusal reaches the counter over a
    /// denominator that also incremented.
    /// </summary>
    /// <remarks>
    /// The regression the F1 ceremony was called for, stated in the artifact's own terms. The stage
    /// tag must read <c>Enforce</c>: the old collapse evaluated the turn as Observe, and had it
    /// survived into the instrument the soak would have attributed a Korean refusal to the wrong
    /// rung and read Enforce as quieter than it was.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void A_korean_structural_finding_at_enforce_is_counted_as_a_refusal_at_enforce()
    {
        using var probe = new MeterProbe();
        var (evaluator, metrics) = Wire();
        using var _ = metrics;

        var result = evaluator.Evaluate(
            CoachGroundingStage.Enforce,
            Answer("Here are your words.", "ko-KR"),
            evidence: [ClaimFixture.Evidence(withheld: 5)],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        result.Refused.Should().BeTrue(
            "a structural finding has no substitute at any language, so the language carve-out "
            + "never applied to it");

        probe.Total(CoachGroundingMetrics.TurnsRefusedName).Should().Be(1);

        probe.TagsFor(CoachGroundingMetrics.TurnsRefusedName)[0][CoachGroundingTags.Stage]
            .Should().Be(
                nameof(CoachGroundingStage.Enforce),
                "the old collapse evaluated this turn one rung down. If the instrument ever reads "
                + "Observe here, the soak is attributing Korean refusals to a rung nobody promoted "
                + "to and Enforce looks quieter than it is");

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            1, "a refused turn is still an evaluated turn and still belongs in the denominator");
    }

    /// <summary>
    /// A Korean turn whose findings are all substitutable ships unaltered, and is counted as
    /// suppressed rather than refused or altered.
    /// </summary>
    /// <remarks>
    /// Three assertions, and each names a distinct way the artifact could mislead: counted as
    /// refused would manufacture a refusal spike; counted as altered would claim English repair copy
    /// went out to a Korean learner; counted as neither would erase the carve-out from the record
    /// entirely.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void A_korean_substitutable_finding_at_enforce_is_counted_as_suppressed_not_refused()
    {
        using var probe = new MeterProbe();
        var (evaluator, metrics) = Wire();
        using var _ = metrics;

        var original = Answer("You have 42 words due this week.", "ko-KR");

        var result = evaluator.Evaluate(
            CoachGroundingStage.Enforce,
            original,
            evidence: [ClaimFixture.Evidence(matched: 84, returned: 20)],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        result.Record!.Findings.Should().ContainSingle()
            .Which.Rule.Should().Be(
                CoachClaimRuleCode.CountClaimMismatch,
                "the turn must actually carry a substitutable finding. Without this the suppression "
                + "assertions below pass on a turn where nothing fired, because the suppression flag "
                + "is a property of the policy and not of the findings");

        result.Refused.Should().BeFalse(
            "taking the whole turn away over copy that could not be localized costs the learner "
            + "more than the finding did");

        result.Answer!.PlainText.Should().Be(
            original.PlainText,
            "unaltered means unaltered. Substituting an English constant into a Korean answer is "
            + "the outcome the suppression path exists to prevent");

        probe.Total(CoachGroundingMetrics.TurnsSuppressedName).Should().Be(
            1, "the suppression is on the record, not silently dropped");

        probe.Total(CoachGroundingMetrics.TurnsRefusedName).Should().Be(
            0, "counting suppression as refusal would read as a Korean refusal spike and get a "
            + "working system rolled back");

        probe.Total(CoachGroundingMetrics.TurnsAlteredName).Should().Be(
            0, "nothing was altered, and an altered count here would claim English repair copy "
            + "reached a Korean learner");

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(1);
    }

    /// <summary>The English control for the pair above, at the same rung.</summary>
    /// <remarks>
    /// The same substitutable finding in English is altered rather than suppressed. Without this,
    /// the Korean result could be produced by a build where substitution was broken everywhere.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void The_same_finding_in_english_is_counted_as_altered_rather_than_suppressed()
    {
        using var probe = new MeterProbe();
        var (evaluator, metrics) = Wire();
        using var _ = metrics;

        var result = evaluator.Evaluate(
            CoachGroundingStage.Enforce,
            Answer("You have 42 words due this week.", "en-US"),
            evidence: [ClaimFixture.Evidence(matched: 84, returned: 20)],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        result.Record!.Findings.Should().ContainSingle()
            .Which.Rule.Should().Be(
                CoachClaimRuleCode.CountClaimMismatch,
                "the same finding as the Korean case, so the two are a comparison");

        probe.Total(CoachGroundingMetrics.TurnsSuppressedName).Should().Be(
            0, "English is the language the repair constants are written in");

        probe.Total(CoachGroundingMetrics.TurnsAlteredName).Should().Be(
            1, "so the same finding repairs rather than suppressing, and the counters must be able "
            + "to tell the two apart");
    }

    /// <summary>
    /// The old stage collapse, replayed against the instrument: it would not have produced a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mutation the F1 fix removed was "a Korean answer at Enforce is evaluated as Observe". This
    /// reproduces exactly that by handing the engine the collapsed rung, and asserts the outcome the
    /// defect produced — no refusal — so the regression is pinned by a demonstration rather than by
    /// a comment.
    /// </para>
    /// <para>
    /// This test fails if the collapse is ever reintroduced <em>and</em> if refusal is ever wired to
    /// something other than the requested stage: both would make Observe start refusing, which is
    /// its own defect.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void The_collapsed_rung_produces_no_refusal_which_is_what_the_defect_shipped()
    {
        using var probe = new MeterProbe();
        var (evaluator, metrics) = Wire();
        using var _ = metrics;

        // The mutation: the rung the old code substituted for Enforce on a Korean turn.
        evaluator.Evaluate(
            CoachGroundingStage.Observe,
            Answer("Here are your words.", "ko-KR"),
            evidence: [ClaimFixture.Evidence(withheld: 5)],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        probe.Total(CoachGroundingMetrics.TurnsRefusedName).Should().Be(
            0,
            "Observe observes. The defect was reaching this outcome while the operator had "
            + "configured Enforce, for the majority learner population, on the rung whose entire "
            + "job is refusing");

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            1, "Observe still counts, which is what makes an Observe window readable at all");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Korean teaching content: no finding, and still a denominator
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Korean teaching content produces no finding at Enforce and still increments the denominator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The zero fixture for a Korean window. <c>CoachKoreanEnforceTests</c> proves these strings fire
    /// nothing; what the gate additionally needs is that a turn carrying them is still <em>judged</em>
    /// — otherwise a clean Korean window and a window where Korean traffic bypassed the layer produce
    /// the same artifact.
    /// </para>
    /// <para>
    /// The rows cover the four families §14 names: Sino-Korean numerals, native numerals, 시/분 time
    /// expressions, and dates. A rule that mistook 칠십 for a claim about the learner would refuse
    /// lessons, and it would do it at the rung where refusal is total.
    /// </para>
    /// </remarks>
    [Theory]
    // Sino-Korean numerals.
    [InlineData("칠십은 70입니다.")]
    [InlineData("숫자 100은 백이라고 읽습니다.")]
    // Native Korean numerals and counters.
    [InlineData("하나, 둘, 셋을 세어 봅시다.")]
    [InlineData("사과 세 개를 샀어요.")]
    // 시/분 time expressions.
    [InlineData("지금은 3시 30분입니다.")]
    [InlineData("수업은 여덟 시에 시작합니다.")]
    // Dates.
    [InlineData("오늘은 2026년 8월 22일입니다.")]
    [InlineData("생일이 몇 월 며칠이에요?")]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void Korean_teaching_content_fires_nothing_and_still_reaches_the_denominator(
        string lesson)
    {
        using var probe = new MeterProbe();
        var (evaluator, metrics) = Wire();
        using var _ = metrics;

        var result = evaluator.Evaluate(
            CoachGroundingStage.Enforce,
            Answer(lesson, "ko-KR"),
            evidence: [],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        result.Refused.Should().BeFalse(
            "a numeral in a lesson is not a claim about the learner, and refusing it would take a "
            + "lesson away from the learner it was written for");

        probe.Count(CoachGroundingMetrics.FindingsName).Should().Be(
            0, "no rule may fire on teaching content");

        probe.Total(CoachGroundingMetrics.TurnsRefusedName).Should().Be(0);

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(
            1,
            "and the clean turn still counts. A zero over an empty denominator is the artifact the "
            + "gate must refuse, in Korean exactly as in English");
    }

    /// <summary>
    /// The non-firing sweep is non-vacuous: a real learner-state claim in Korean still fires.
    /// </summary>
    /// <remarks>
    /// Without this, a build where the rules simply never ran on Korean would pass every row above.
    /// It is the same control <c>CoachKoreanEnforceTests</c> keeps at engine level, restated here
    /// against the counter because that is the layer this file is about.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void A_real_claim_in_korean_still_reaches_the_findings_counter()
    {
        using var probe = new MeterProbe();
        var (evaluator, metrics) = Wire();
        using var _ = metrics;

        evaluator.Evaluate(
            CoachGroundingStage.Enforce,
            Answer("Here are your words.", "ko-KR"),
            evidence: [ClaimFixture.Evidence(withheld: 5)],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        probe.Count(CoachGroundingMetrics.FindingsName).Should().BePositive(
            "the teaching rows above only mean something if the rules were live on Korean at all");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // What turns_suppressed actually counts
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>turns_suppressed</c> counts turns where the policy was in force, not turns where a repair
    /// was withheld. A clean Korean turn increments it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found while writing this suite.</b> The suppression flag is set from the stage and the
    /// answer's language tag alone, before the rules run, so it is true on every non-English turn at
    /// Repair or above whether or not anything fired. The first draft of the test above asserted
    /// <c>turns_suppressed == 1</c> on a Korean turn that produced no findings at all, and passed.
    /// </para>
    /// <para>
    /// <b>Why it matters to the soak, and why it is not a defect.</b> The behaviour is correct — the
    /// flag records that substitution was unavailable for the turn, which is exactly what the
    /// durable record should say. What it is not is a repair-withheld count. For a Korean-majority
    /// window at Enforce the ratio <c>turns_suppressed / turns_evaluated</c> approaches 1.0, and a
    /// reader who takes that for "the layer suppressed almost everything" will roll back a working
    /// system. The runbook says so; this pins the semantics so the runbook stays true.
    /// </para>
    /// <para>
    /// The readable quantity is <c>findings</c> against <c>turns_altered</c>, not the suppression
    /// count on its own.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void Turns_suppressed_counts_policy_in_force_and_a_clean_korean_turn_increments_it()
    {
        using var probe = new MeterProbe();
        var (evaluator, metrics) = Wire();
        using var _ = metrics;

        var result = evaluator.Evaluate(
            CoachGroundingStage.Enforce,
            Answer("지금은 3시 30분입니다.", "ko-KR"),
            evidence: [],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        result.Record!.Findings.Should().BeEmpty("nothing fired on a clock reading");

        probe.Total(CoachGroundingMetrics.TurnsSuppressedName).Should().Be(
            1,
            "and the counter still moves, because it records that substitution was unavailable for "
            + "the turn rather than that a repair was withheld. Read it beside findings, never alone");

        probe.Count(CoachGroundingMetrics.FindingsName).Should().Be(
            0, "which is the series that tells the reader nothing was wrong");
    }

    /// <summary>
    /// An English turn at the same rung does not increment it, so the counter is language-keyed and
    /// not simply always-on.
    /// </summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.SoakMeasured)]
    public void Turns_suppressed_stays_at_zero_for_a_clean_english_turn()
    {
        using var probe = new MeterProbe();
        var (evaluator, metrics) = Wire();
        using var _ = metrics;

        evaluator.Evaluate(
            CoachGroundingStage.Enforce,
            Answer("Here is your study plan for today.", "en-US"),
            evidence: [],
            observations: null,
            proposedCapabilities: [],
            CoachCapabilityStage.Read,
            handshake: null);

        probe.Total(CoachGroundingMetrics.TurnsSuppressedName).Should().Be(
            0, "English is the language the substitutions are written in, so nothing is withheld");

        probe.Total(CoachGroundingMetrics.TurnsEvaluatedName).Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A local listener over the coach meter's grounding instruments.</summary>
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
