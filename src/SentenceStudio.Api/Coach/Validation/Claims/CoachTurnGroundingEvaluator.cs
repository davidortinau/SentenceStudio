using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// The one place the grounding ladder runs on a real turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> <see cref="CoachClaimRuleEngine"/> shipped registered and never
/// resolved: nine rules, a full test suite, and no path from a learner's turn to any of them. The
/// gap was invisible because every rule test constructed its own context. This type is the missing
/// half — it builds the context from what the turn actually produced, and it is small enough to
/// read in one sitting so the absence of a caller could not hide in it again.
/// </para>
/// <para>
/// <b>Three inputs, and a router label is not one of them.</b> Plan B5. The context carries the
/// answer's claim shape, the W4 trace of what was really called, and the W5 manifest. The optional
/// shadow router runs <em>after</em> the rules and its label goes into the record, never into the
/// context — <see cref="CoachClaimRuleContext"/> has no member it could occupy, and calling it
/// after the fact means it cannot influence a rule even by accident.
/// </para>
/// <para>
/// <b>Nothing here logs learner text.</b> The record holds codes, indices, and counts. The log line
/// holds codes and a total. An offending sentence never leaves the answer it was written in, which
/// is what lets this run at Observe in production without a second privacy review.
/// </para>
/// </remarks>
public sealed class CoachTurnGroundingEvaluator
{
    private readonly CoachClaimRuleEngine _engine;
    private readonly ICoachCapabilityResolver _resolver;
    private readonly ICoachShadowClaimRouter? _router;
    private readonly ICoachClaimFindingBuffer? _findings;
    private readonly CoachGroundingMetrics? _metrics;
    private readonly ILogger _logger;

    /// <summary>Builds the evaluator over the engine and its optional companions.</summary>
    public CoachTurnGroundingEvaluator(
        CoachClaimRuleEngine engine,
        ICoachCapabilityResolver resolver,
        ILogger logger,
        ICoachShadowClaimRouter? router = null,
        ICoachClaimFindingBuffer? findings = null,
        CoachGroundingMetrics? metrics = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _router = router;
        _findings = findings;

        // Optional so a host that has not registered the meter still answers a learner. Absent
        // metrics are a missing diagnostic; a turn that throws because of one is a missing turn.
        _metrics = metrics;
    }

    /// <summary>
    /// Runs the ladder over one composed answer and reports what the turn should do with it.
    /// </summary>
    /// <param name="stage">The configured rung.</param>
    /// <param name="answer">The answer as composed, before anything is stored or returned.</param>
    /// <param name="evidence">The evidence the turn built from its own scopes. W3.</param>
    /// <param name="observations">The turn's tool-call observations. W4.</param>
    /// <param name="proposedCapabilities">Capabilities the turn's intent proposed. W5.</param>
    /// <param name="capabilityStage">The promoted capability stage.</param>
    /// <param name="handshake">The client's capabilities, merged for this turn only.</param>
    /// <param name="dispute">
    /// The learner's open correction, when one is in force. Without it
    /// <see cref="CoachRepeatedDisputedClaimRule"/> has nothing to fire on — which is the state the
    /// rule shipped in, registered and unreachable.
    /// </param>
    public CoachTurnGroundingResult Evaluate(
        CoachGroundingStage stage,
        CoachAnswerDto answer,
        IReadOnlyList<CoachEvidenceDto> evidence,
        ICoachTurnObservationBuffer? observations,
        IReadOnlyList<string> proposedCapabilities,
        CoachCapabilityStage capabilityStage,
        CoachClientCapabilityHandshake? handshake,
        CoachTurnDisputeState? dispute = null)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(proposedCapabilities);

        if (stage == CoachGroundingStage.Off)
        {
            // Off bypasses the rules, so it also bypasses the dispute. The caller still holds the
            // dispute and still reports it to the learner; what it does not get is a rule that
            // could refuse an answer on a rung the operator has not promoted to.
            // The bypass is total: no scan, no record, no log line. Off must be indistinguishable
            // from the build that had no grounding layer, or promoting to Observe would be
            // measuring the difference between two things that both already changed the turn.
            return CoachTurnGroundingResult.Unchanged(answer);
        }

        var context = new CoachClaimRuleContext
        {
            Answer = answer,
            Evidence = evidence,
            Trace = CoachTurnTraceProjection.Project(observations),
            ProposedCapabilities = proposedCapabilities,
            Stage = capabilityStage,
            Handshake = handshake,
            Dispute = dispute
        };

        // W7 coupling, and the reason it lives here rather than in the engine: every substitution
        // the engine can make is an English constant from CoachDeterministicCopy, and the server
        // has no localisation — learner-visible copy is the client's resx by design. Substituting
        // into an answer a learner is reading in Korean would replace one honest Korean sentence
        // with an English one, which is a different kind of dishonesty and a worse one, because it
        // is the grounding layer doing it.
        //
        // So substitution is withheld for a non-English answer. The findings are still recorded in
        // full, and Enforce still refuses, because a refusal carries no new copy — it takes the
        // notice path the shape validator has always used.
        //
        // The two facts travel as two arguments. They were briefly collapsed into one — the stage
        // was downgraded to Observe when substitution could not run — and the collapse disabled
        // refusal for every Korean learner at Enforce, because the engine's refusal test reads the
        // stage it was handed. The comment above said Enforce still refuses; the code disagreed,
        // and the only coverage tested the language predicate in isolation, so nothing caught it.
        //
        // The engine now decides refusal from the real stage and substitution from the policy, and
        // it distinguishes a finding whose substitute was withheld from one that never had a
        // substitute. Only the second refuses.
        var repairSuppressed = SuppressRepairForLanguage(stage, answer);

        var outcome = _engine.Evaluate(context, stage, substitutionAllowed: !repairSuppressed);

        // After the rules, never before. See the remark on B5.
        var label = _router?.Classify(context) ?? CoachShadowRouteLabel.Unknown;

        var altered = !outcome.Refused
            && outcome.Findings.Any(finding => finding.Action == CoachClaimRepairAction.Substituted);

        // Reused from the engine rather than re-projected. The dispute exit inside the engine
        // already decided against this exact limitation, and running the projection twice would
        // let the rule and the coordinator disagree the day the two calls stop matching.
        var limitation = outcome.Limitation;

        var record = new CoachClaimTurnRecord(
            stage, outcome.Findings, outcome.Refused, altered, label, limitation, repairSuppressed);

        _findings?.Capture(record);

        // The durable projection, built once and reused by both the metric and the write site. Two
        // projections of the same record would let the dashboard and the stored row disagree the
        // day one of them changed, and they are read months apart.
        var summary = CoachGroundingTurnProjection.Project(record);

        if (summary is not null)
        {
            // Synchronous, and no new async I/O on the turn path: Counter<long>.Add is a no-op when
            // nothing is listening. This is also the F2 positive control — the denominator that
            // makes a soak window of zeros mean "nothing went wrong" rather than "nothing ran".
            _metrics?.RecordTurn(summary);
        }

        if (record.HasFindings)
        {
            // Codes and counts. Nothing on this line can carry a sentence the learner wrote or the
            // model wrote, which is the property that makes it safe at Observe in production.
            _logger.LogInformation(
                "[Coach] Grounding {Stage}: {FindingCount} finding(s) {Codes}; refused={Refused} altered={Altered}",
                stage,
                record.Findings.Count,
                string.Join(",", record.CountsByRule.Select(pair => $"{pair.Key}={pair.Value}")),
                record.Refused,
                altered);
        }

        // The judged context carries the limitation so the coordinator classifies the exit against
        // the same typed fact the rule did.
        return new CoachTurnGroundingResult(
            outcome.Refused ? null : outcome.Answer,
            record,
            context.WithLimitation(limitation),
            summary);
    }

    /// <summary>
    /// Whether substitution must be held back because the repair copy is not in the learner's
    /// display language.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately narrow: it asks whether the copy fits, not whether the finding is real. Every
    /// finding is still recorded at every rung, so promoting a Korean deployment to Repair produces
    /// exactly the measurements Observe would — which is the honest state of affairs until the
    /// repair sentences exist as client resource keys.
    /// </para>
    /// <para>
    /// Missing or unparseable tags suppress. An answer that does not say what language it is in is
    /// not evidence that it is in English, and defaulting to "substitute anyway" would put English
    /// into exactly the answers whose language nobody established.
    /// </para>
    /// </remarks>
    internal static bool SuppressRepairForLanguage(CoachGroundingStage stage, CoachAnswerDto answer)
    {
        if (stage < CoachGroundingStage.Repair)
        {
            return false;
        }

        var tag = answer.DisplayLanguageTag;

        return string.IsNullOrWhiteSpace(tag)
            || !tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            || (tag.Length > 2 && tag[2] is not ('-' or '_'));
    }
}

/// <summary>What the turn should do with the answer it composed.</summary>
/// <param name="Answer">The answer to ship, or null when the turn must refuse.</param>
/// <param name="Record">The content-free record of what the ladder did.</param>
/// <param name="Context">
/// The context the rules ran on, so the caller can resolve a dispute against exactly what the
/// ladder judged rather than rebuilding an equivalent one. Null when the ladder did not run.
/// </param>
/// <param name="Grounding">
/// The durable summary for the protected turn outcome, or null when the ladder did not run. Built
/// here rather than at the write site so the metric and the stored row are the same object.
/// </param>
public sealed record CoachTurnGroundingResult(
    CoachAnswerDto? Answer,
    CoachClaimTurnRecord? Record,
    CoachClaimRuleContext? Context = null,
    CoachGroundingTurnSummary? Grounding = null)
{
    /// <summary>True when the turn must not ship this answer.</summary>
    public bool Refused => Answer is null;

    /// <summary>The Off result: the answer, untouched, and no record at all.</summary>
    public static CoachTurnGroundingResult Unchanged(CoachAnswerDto answer) => new(answer, null);
}
