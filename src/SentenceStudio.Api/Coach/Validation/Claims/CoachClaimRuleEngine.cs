using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// Runs every rule, then repairs by substitution and refuses only what substitution cannot fix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Substitution first, refusal last.</b> The plan says it twice and the reason is worth stating:
/// a refusal is a worse outcome for the learner than an imperfect answer, and a grounding layer
/// that refuses freely trains everyone to turn it off. Nearly every finding here has a truthful
/// sentence that fits in the offending span's place — "I have not checked your practice history"
/// is honest, useful, and shorter than what it replaces.
/// </para>
/// <para>
/// <b>The escalation is a property of the stage, not of the rule.</b> At
/// <see cref="CoachGroundingStage.Observe"/> nothing is altered no matter how severe the finding;
/// at <see cref="CoachGroundingStage.Repair"/> substitution happens and an unrepairable finding
/// still ships; only <see cref="CoachGroundingStage.Enforce"/> refuses. That ordering is what makes
/// the ladder safe to climb one rung at a time in production.
/// </para>
/// <para>
/// <b>Nothing here is generated.</b> Every replacement is a constant from
/// <c>CoachDeterministicCopy</c>, free of counts and dates, because a repair that invented a number
/// would be the defect wearing the fix's clothes.
/// </para>
/// </remarks>
public sealed class CoachClaimRuleEngine
{
    private readonly IReadOnlyList<ICoachClaimRule> _rules;
    private readonly ICoachCapabilityResolver _resolver;

    /// <summary>
    /// Builds the engine over the nine rules.
    /// </summary>
    /// <remarks>
    /// The rule set is constructed here rather than injected as <c>IEnumerable&lt;ICoachClaimRule&gt;</c>
    /// so a registration mistake cannot silently drop a rule. A missing DI registration produces a
    /// smaller rule set and a green test suite; a missing line here fails the census test.
    /// </remarks>
    public CoachClaimRuleEngine(ICoachCapabilityResolver resolver, ICoachCapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(manifest);

        _resolver = resolver;

        _rules =
        [
            new CoachUnverifiedLearnerStateClaimRule(),
            new CoachNegativeClaimWithoutCoverageRule(),
            new CoachFabricatedCheckRule(),
            new CoachOrderClaimMismatchRule(),
            new CoachCountClaimMismatchRule(),
            new CoachWithheldNotDisclosedRule(),
            new CoachCapabilityAbsentRule(resolver),
            new CoachFalseLimitationRule(resolver),
            new CoachSideEffectNotDisclosedRule(manifest),
            new CoachRepeatedDisputedClaimRule()
        ];
    }

    /// <summary>Every registered rule, in evaluation order.</summary>
    public IReadOnlyList<ICoachClaimRule> Rules => _rules;

    /// <summary>Scans without repairing. What <see cref="CoachGroundingStage.Observe"/> does.</summary>
    public IReadOnlyList<CoachClaimFinding> Scan(CoachClaimRuleContext context) =>
        Scan(context, out _);

    /// <summary>
    /// Scans, and reports the limitation the turn declared along the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the projection happens mid-scan.</b> The dispute exit resolves on a typed limitation
    /// rather than on prose, and the exit is decided inside
    /// <see cref="CoachRepeatedDisputedClaimRule"/> — which runs here, not in the evaluator. If the
    /// limitation were projected after the scan and handed only to the coordinator, the rule and
    /// the coordinator would be answering the same question from different inputs, and a turn the
    /// coordinator recorded as resolved would still be constrained by the rule.
    /// </para>
    /// <para>
    /// Projecting before the last rule is exactly equivalent to projecting after it:
    /// <see cref="CoachClaimLimitationProjection"/> reads only capability-absent and
    /// false-limitation findings, and both of those rules run earlier in the list. The dispute rule
    /// contributes nothing the projection would look at.
    /// </para>
    /// </remarks>
    private IReadOnlyList<CoachClaimFinding> Scan(
        CoachClaimRuleContext context,
        out CoachLimitationDto? limitation)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<CoachClaimFinding>();
        limitation = null;

        foreach (var rule in _rules)
        {
            if (rule.Code == CoachClaimRuleCode.RepeatedDisputedClaim)
            {
                limitation = CoachClaimLimitationProjection.Project(
                    findings,
                    context.ProposedCapabilities,
                    _resolver,
                    context.Stage,
                    context.Handshake);

                findings.AddRange(rule.Evaluate(context.WithLimitation(limitation)));
                continue;
            }

            findings.AddRange(rule.Evaluate(context));
        }

        return findings;
    }

    /// <summary>Scans, then acts according to the stage and the substitution policy.</summary>
    /// <remarks>
    /// <para>
    /// <b>Two axes, not one.</b> The stage says how far the layer may act; <paramref name="substitutionAllowed"/>
    /// says whether deterministic English copy may be written into this particular answer. They were
    /// briefly collapsed — a Korean answer at Enforce was evaluated "one rung down" as Observe — and
    /// the collapse silently disabled refusal for the majority learner population: the engine's own
    /// refusal test reads <c>stage &gt;= Enforce</c>, and the stage it was handed said Observe.
    /// </para>
    /// <para>
    /// The two axes are independent because refusal carries no copy. A refusal takes the notice path
    /// the shape validator has always used, which is already localized on the client, so there is no
    /// reason a language that blocks substitution should also block refusal.
    /// </para>
    /// <para>
    /// The distinction that makes this safe is inside <see cref="Repair"/>: a finding whose
    /// substitute was withheld for language is <em>suppressed</em>, not unrepairable. Only a finding
    /// with no substitute at all — a missing disclosure, an absent capability, a repeated disputed
    /// claim — refuses. So a Korean turn whose findings were all substitutable ships unaltered with
    /// the suppression recorded, and a Korean turn with a structural finding refuses exactly as an
    /// English one would.
    /// </para>
    /// </remarks>
    /// <param name="context">The turn under audit.</param>
    /// <param name="stage">The rung the deployment asked for. Never a collapsed value.</param>
    /// <param name="substitutionAllowed">
    /// Whether deterministic copy may be written into this answer. False for a display language the
    /// repair constants are not written in.
    /// </param>
    public CoachClaimRuleOutcome Evaluate(
        CoachClaimRuleContext context,
        CoachGroundingStage stage,
        bool substitutionAllowed = true)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (stage == CoachGroundingStage.Off)
        {
            return CoachClaimRuleOutcome.Clean(context.Answer);
        }

        var findings = Scan(context, out var limitation);

        if (findings.Count == 0)
        {
            return CoachClaimRuleOutcome.Clean(context.Answer) with { Limitation = limitation };
        }

        if (stage == CoachGroundingStage.Observe)
        {
            return new CoachClaimRuleOutcome(
                context.Answer,
                [.. findings.Select(finding => finding with { Action = CoachClaimRepairAction.ObservedOnly })],
                Refused: false,
                Limitation: limitation);
        }

        return Repair(context, findings, stage, substitutionAllowed) with { Limitation = limitation };
    }

    /// <summary>
    /// Substitutes what it may, and refuses only what no substitute could have fixed.
    /// </summary>
    /// <remarks>
    /// Findings sort into three buckets rather than two. <b>Substituted</b>: a substitute exists, it
    /// has a span, and the policy permits writing it. <b>Suppressed</b>: a substitute exists but the
    /// policy withholds it for this answer's language — recorded, not acted on, and <em>not</em> a
    /// reason to refuse, because the answer the learner would have got is still honest prose the
    /// rules merely wanted to soften. <b>Structurally unrepairable</b>: no substitute exists at any
    /// language, which is the only bucket that refuses at Enforce.
    /// </remarks>
    private static CoachClaimRuleOutcome Repair(
        CoachClaimRuleContext context,
        IReadOnlyList<CoachClaimFinding> findings,
        CoachGroundingStage stage,
        bool substitutionAllowed)
    {
        var answer = context.Answer;

        if (answer is null)
        {
            // Nothing to repair and nothing to ship. A capability finding on a turn with no answer
            // is still worth recording, so the findings survive.
            return new CoachClaimRuleOutcome(
                null,
                [.. findings.Select(finding => finding with { Action = CoachClaimRepairAction.None })],
                Refused: false);
        }

        // Keyed by (block, span) so two rules firing on one sentence produce one substitution
        // rather than a substitution of a substitution.
        var replacements = new Dictionary<(int Block, int Span), string>();
        var resolved = new List<CoachClaimFinding>();
        var suppressed = new List<CoachClaimFinding>();
        var unrepairable = new List<CoachClaimFinding>();

        foreach (var finding in findings)
        {
            var repairable = SubstituteFor(finding.Rule) is { } substitute
                && finding is { BlockIndex: not null, SpanIndex: not null };

            if (!repairable)
            {
                // No substitute at any language. A missing disclosure, an absent capability, a
                // repeated disputed claim: the defect is something the answer failed to do, and
                // there is no sentence that undoes a thing that never happened.
                unrepairable.Add(finding);
                continue;
            }

            if (!substitutionAllowed)
            {
                // A substitute exists and the policy withholds it. Recorded and left alone — the
                // answer the learner reads is the model's own honest prose, not a claim the rules
                // rewrote, so refusing here would take a whole turn away over copy that merely
                // could not be localized.
                suppressed.Add(finding);
                continue;
            }

            replacements.TryAdd(
                (finding.BlockIndex!.Value, finding.SpanIndex!.Value),
                SubstituteFor(finding.Rule)!);

            resolved.Add(finding with { Action = CoachClaimRepairAction.Substituted });
        }

        // Only the structural bucket refuses. Suppression is a language fact, not a severity one.
        var refuse = stage >= CoachGroundingStage.Enforce && unrepairable.Count > 0;

        foreach (var finding in unrepairable)
        {
            resolved.Add(finding with
            {
                Action = refuse ? CoachClaimRepairAction.Refused : CoachClaimRepairAction.ObservedOnly
            });
        }

        foreach (var finding in suppressed)
        {
            resolved.Add(finding with { Action = CoachClaimRepairAction.ObservedOnly });
        }

        if (refuse)
        {
            return new CoachClaimRuleOutcome(null, resolved, Refused: true);
        }

        return new CoachClaimRuleOutcome(
            replacements.Count == 0 ? answer : ApplyReplacements(answer, replacements),
            resolved,
            Refused: false);
    }

    /// <summary>
    /// The deterministic sentence that replaces a span, or null when the rule has no substitution.
    /// </summary>
    /// <remarks>
    /// Every value is a count-free, date-free constant. The three rules with no substitution are
    /// the ones whose defect is an <em>absence</em> — a missing disclosure, a missing capability, a
    /// missing side-effect statement — and you cannot replace a sentence that was never written.
    /// Those are the findings that escalate.
    /// </remarks>
    private static string? SubstituteFor(CoachClaimRuleCode rule) => rule switch
    {
        CoachClaimRuleCode.UnverifiedLearnerStateClaim => CoachDeterministicCopy.UncheckedLearnerState,
        CoachClaimRuleCode.NegativeClaimWithoutCoverage => CoachDeterministicCopy.PartialCoverageNegative,
        CoachClaimRuleCode.FabricatedCheck => CoachDeterministicCopy.NoReadHappened,
        CoachClaimRuleCode.OrderClaimMismatch => CoachDeterministicCopy.UnrankedResult,
        CoachClaimRuleCode.CountClaimMismatch => CoachDeterministicCopy.UnsupportedCount,
        CoachClaimRuleCode.FalseLimitation => CoachDeterministicCopy.CapableAfterAll,

        // Deliberately absent: RepeatedDisputedClaim. There is no sentence that repairs it,
        // because the defect is that the coach did not re-read, did not name its prior claim, and
        // did not state a limitation. Substituting a span would leave all three still undone and
        // would hand the learner a politer version of being ignored.
        _ => null
    };

    /// <summary>
    /// Rebuilds the answer with the substituted spans. The original is never mutated.
    /// </summary>
    /// <remarks>
    /// <c>PlainText</c> is rebuilt from the repaired spans rather than left alone. It is a
    /// convenience projection that several surfaces read directly, and a repaired answer whose
    /// plain text still carries the invented sentence would be repaired in the panel and unrepaired
    /// everywhere else — which is worse than not repairing at all, because it looks fixed.
    /// </remarks>
    private static CoachAnswerDto ApplyReplacements(
        CoachAnswerDto answer,
        IReadOnlyDictionary<(int Block, int Span), string> replacements)
    {
        var blocks = new List<CoachAnswerBlockDto>(answer.Blocks.Count);

        for (var blockIndex = 0; blockIndex < answer.Blocks.Count; blockIndex++)
        {
            var block = answer.Blocks[blockIndex];
            var spans = new List<CoachAnswerSpanDto>(block.Spans.Count);

            for (var spanIndex = 0; spanIndex < block.Spans.Count; spanIndex++)
            {
                var span = block.Spans[spanIndex];

                spans.Add(replacements.TryGetValue((blockIndex, spanIndex), out var replacement)
                    ? new CoachAnswerSpanDto
                    {
                        Text = replacement,
                        Language = span.Language,
                        LanguageTag = span.LanguageTag
                    }
                    : span);
            }

            blocks.Add(new CoachAnswerBlockDto
            {
                Kind = block.Kind,
                Label = block.Label,
                Spans = spans
            });
        }

        return new CoachAnswerDto
        {
            Topic = answer.Topic,
            Blocks = blocks,
            PlainText = string.Join(
                " ",
                blocks.SelectMany(block => block.Spans)
                    .Select(span => span.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))),
            TargetLanguageTag = answer.TargetLanguageTag,
            DisplayLanguageTag = answer.DisplayLanguageTag,
            EndsWithRecallQuestion = answer.EndsWithRecallQuestion
        };
    }
}

/// <summary>What the engine decided.</summary>
/// <param name="Answer">The repaired answer, or null when the turn was refused.</param>
/// <param name="Findings">Every finding, with the action taken. Content-free.</param>
/// <param name="Refused">True when the answer was withheld entirely.</param>
/// <param name="Limitation">
/// The bounded inability this turn declared, projected from the findings, or null. Carried on the
/// outcome so the caller reuses the projection the dispute exit was decided against rather than
/// running a second one that could differ.
/// </param>
public sealed record CoachClaimRuleOutcome(
    CoachAnswerDto? Answer,
    IReadOnlyList<CoachClaimFinding> Findings,
    bool Refused,
    CoachLimitationDto? Limitation = null)
{
    /// <summary>An outcome with no finding.</summary>
    public static CoachClaimRuleOutcome Clean(CoachAnswerDto? answer) => new(answer, [], false);

    /// <summary>True when at least one rule fired.</summary>
    public bool HasFindings => Findings.Count > 0;

    /// <summary>
    /// The finding codes and their counts, for a log line or a metric.
    /// </summary>
    /// <remarks>
    /// Codes and counts only. Nothing on this projection can carry an offending sentence, which is
    /// what makes it safe to write into a log without a second review.
    /// </remarks>
    public IReadOnlyDictionary<CoachClaimRuleCode, int> CountsByRule =>
        Findings.GroupBy(finding => finding.Rule)
            .ToDictionary(group => group.Key, group => group.Count());
}
