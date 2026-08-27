using System.Diagnostics.Metrics;
using SentenceStudio.Api.Coach.Telemetry;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// Grounding counters on the existing coach meter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists: twelve zeros from nothing is not evidence.</b> The gate's soak condition
/// reads twelve foundation invariants and requires them to be zero. Before these counters, a host
/// with the grounding layer <em>deleted</em> would produce the same twelve zeros as a host where it
/// ran perfectly, because nothing was counting. That is the registered-but-unreachable failure the
/// W6 and W8 reviews each caught, reappearing at the gate itself.
/// </para>
/// <para>
/// <b>So the denominator is the load-bearing metric.</b>
/// <see cref="TurnsEvaluatedName"/> increments exactly once per evaluated turn, and a soak window
/// where it reads zero voids the artifact regardless of what the numerators say. Everything else
/// here is a numerator over it.
/// </para>
/// <para>
/// <b>Tags are closed codes only.</b> Rule code, stage, refused, substitution-suppressed. No user,
/// no conversation, no tool name, no content, nothing unbounded — a metric dimension with learner
/// cardinality is both a privacy leak and a billing incident, and the two arrive together.
/// </para>
/// <para>
/// <b>What this does not claim to measure.</b> Several foundation invariants are structural — they
/// are held by a contract test or a startup validator, not by a runtime counter — and there is no
/// counter here that would let a reader believe otherwise. Emitting a permanently-zero counter for
/// a property nothing observes is exactly the false comfort this file was written to remove.
/// </para>
/// </remarks>
public sealed class CoachGroundingMetrics : IDisposable
{
    /// <summary>
    /// The F2 positive control. Zero across a soak window voids the artifact.
    /// </summary>
    public const string TurnsEvaluatedName = "coach.grounding.turns_evaluated";

    /// <summary>Findings by rule code. The seven soak-measured classes are all rule codes.</summary>
    public const string FindingsName = "coach.grounding.findings";

    /// <summary>Turns whose answer was refused.</summary>
    public const string TurnsRefusedName = "coach.grounding.turns_refused";

    /// <summary>Turns whose answer had at least one span substituted.</summary>
    public const string TurnsAlteredName = "coach.grounding.turns_altered";

    /// <summary>Turns where substitution was withheld for the display language.</summary>
    public const string TurnsSuppressedName = "coach.grounding.turns_suppressed";

    /// <summary>
    /// The delivery canary. Test-only, and never incremented by a production code path.
    /// </summary>
    public const string CanaryName = "coach.grounding.canary";

    private readonly Meter _meter;
    private readonly Counter<long> _turnsEvaluated;
    private readonly Counter<long> _findings;
    private readonly Counter<long> _turnsRefused;
    private readonly Counter<long> _turnsAltered;
    private readonly Counter<long> _turnsSuppressed;
    private readonly Counter<long> _canary;

    /// <summary>Creates the grounding counters. Register as a singleton.</summary>
    public CoachGroundingMetrics()
    {
        _meter = new Meter(CoachTelemetry.MeterName);

        _turnsEvaluated = _meter.CreateCounter<long>(
            TurnsEvaluatedName,
            unit: "{turn}",
            description: "Turns the grounding layer evaluated. The denominator every other grounding metric is read over.");

        _findings = _meter.CreateCounter<long>(
            FindingsName,
            unit: "{finding}",
            description: "Grounding findings by closed rule code.");

        _turnsRefused = _meter.CreateCounter<long>(
            TurnsRefusedName,
            unit: "{turn}",
            description: "Turns whose answer the grounding layer refused.");

        _turnsAltered = _meter.CreateCounter<long>(
            TurnsAlteredName,
            unit: "{turn}",
            description: "Turns where at least one span was substituted.");

        _turnsSuppressed = _meter.CreateCounter<long>(
            TurnsSuppressedName,
            unit: "{turn}",
            description: "Turns where substitution was withheld for the display language.");

        _canary = _meter.CreateCounter<long>(
            CanaryName,
            unit: "{ping}",
            description: "Deliberate delivery probe. Never emitted by a production code path.");
    }

    /// <summary>
    /// Records one evaluated turn: the denominator, the flags, and one entry per rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synchronous and allocation-light by design. <c>Counter&lt;long&gt;.Add</c> is a no-op when
    /// nothing is listening, and the turn path may not acquire new async I/O — a metric that awaited
    /// anything would put the observability layer inside the learner's latency budget.
    /// </para>
    /// <para>
    /// The three flag counters fire only when true. A counter incremented by zero is indistinguishable
    /// from one never touched in most backends, and emitting both would double the series for no
    /// information.
    /// </para>
    /// </remarks>
    public void RecordTurn(CoachGroundingTurnSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var stage = summary.RequestedStage.ToString();

        // Exactly one per evaluated turn. Everything else on this method is a numerator over it,
        // and a soak that reads this as zero is reading a host where nothing ran.
        _turnsEvaluated.Add(1, new KeyValuePair<string, object?>(CoachGroundingTags.Stage, stage));

        if (summary.Refused)
        {
            _turnsRefused.Add(
                1,
                new KeyValuePair<string, object?>(CoachGroundingTags.Stage, stage),
                new KeyValuePair<string, object?>(
                    CoachGroundingTags.SubstitutionSuppressed, summary.RepairSuppressedForLanguage));
        }

        if (summary.Altered)
        {
            _turnsAltered.Add(1, new KeyValuePair<string, object?>(CoachGroundingTags.Stage, stage));
        }

        if (summary.RepairSuppressedForLanguage)
        {
            _turnsSuppressed.Add(1, new KeyValuePair<string, object?>(CoachGroundingTags.Stage, stage));
        }

        foreach (var entry in summary.RuleCounts)
        {
            _findings.Add(
                entry.Count,
                new KeyValuePair<string, object?>(CoachGroundingTags.RuleCode, entry.Rule.ToString()),
                new KeyValuePair<string, object?>(CoachGroundingTags.Stage, stage),
                new KeyValuePair<string, object?>(CoachGroundingTags.Refused, summary.Refused));
        }
    }

    /// <summary>
    /// Emits one canary ping, proving the meter reaches the exporter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this is for.</b> A soak artifact of zeros only means something if the pipeline that
    /// would have reported a non-zero is known to work. This counter is how that is established
    /// without inventing a production defect: Jayne's soak test calls it once at the start of the
    /// window and asserts the point arrives in App Insights. A window where the canary did not land
    /// is a window whose zeros prove nothing.
    /// </para>
    /// <para>
    /// <b>Why it is separate from every real counter.</b> Firing a real defect counter to prove
    /// delivery would put a fabricated finding into the same series an operator reads for real ones,
    /// and the gate's own numerators would then include a number nobody's learner produced.
    /// </para>
    /// <para>
    /// <b>No production caller.</b> A source scan holds that; the only callers are the soak harness
    /// and its test. If this ever gains one, the scan fails rather than the canary quietly becoming
    /// a real signal.
    /// </para>
    /// </remarks>
    public void EmitCanary() => _canary.Add(1);

    public void Dispose() => _meter.Dispose();
}

/// <summary>
/// The tag names grounding metrics may use. Closed, and pinned by a contract test.
/// </summary>
/// <remarks>
/// Four names, all closed codes or booleans. Nothing here can carry a learner identifier, a
/// conversation id, a tool argument, or free text — the unbounded-cardinality dimensions that turn
/// a metric into both a privacy leak and a billing incident.
/// </remarks>
public static class CoachGroundingTags
{
    /// <summary>The requested rung, by name.</summary>
    public const string Stage = "grounding_stage";

    /// <summary>A closed <see cref="CoachClaimRuleCode"/> name.</summary>
    public const string RuleCode = "grounding_rule_code";

    /// <summary>Whether the turn refused.</summary>
    public const string Refused = "grounding_refused";

    /// <summary>Whether substitution was withheld for the display language.</summary>
    public const string SubstitutionSuppressed = "grounding_substitution_suppressed";

    /// <summary>Every tag name grounding metrics are permitted to emit.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Stage, RuleCode, Refused, SubstitutionSuppressed];
}
