using System.Text.RegularExpressions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>One honesty rule.</summary>
/// <remarks>
/// A rule reports; it does not repair. Splitting detection from repair is what lets
/// <see cref="CoachGroundingStage.Observe"/> exist at all — the same rule runs in production for
/// weeks producing nothing but counts, and the repair path is turned on separately once those
/// counts are understood.
/// </remarks>
public interface ICoachClaimRule
{
    /// <summary>Which rule this is.</summary>
    CoachClaimRuleCode Code { get; }

    /// <summary>Findings, in span order. Empty when the answer is honest by this rule.</summary>
    IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context);
}

// ─────────────────────────────────────────────────────────────────────────────
// Foundation rules: was the claim checked?
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The answer asserts something about the learner without a read behind it.
/// </summary>
/// <remarks>
/// <para>
/// The original defect, and the reason the whole workstream exists: the coach describing a learner's
/// practice history it never looked at. Fluent, specific, and invented.
/// </para>
/// <para>
/// The bar is a successful read in the trace, not a plausible-looking answer. Note the asymmetry
/// with <see cref="CoachFabricatedCheckRule"/>: this rule fires when the claim exists and the read
/// does not; that one fires when the answer <em>says</em> it looked. An answer can be wrong in
/// either direction independently.
/// </para>
/// <para>
/// <b>No trace means no finding.</b> A turn that recorded nothing is unproven, and treating
/// unproven as guilty would make every pre-W4 stored turn a violation the moment this shipped.
/// </para>
/// </remarks>
public sealed class CoachUnverifiedLearnerStateClaimRule : ICoachClaimRule
{
    public CoachClaimRuleCode Code => CoachClaimRuleCode.UnverifiedLearnerStateClaim;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Trace is null)
        {
            yield break;
        }

        if (context.TraceShowsASuccessfulRead && context.Evidence.Count > 0)
        {
            yield break;
        }

        foreach (var span in context.Spans)
        {
            if (CoachLearnerStateReferent.IsLearnerStateClaim(span.Text))
            {
                yield return new CoachClaimFinding(
                    Code, CoachClaimRepairAction.None, span.BlockIndex, span.SpanIndex);
            }
        }
    }
}

/// <summary>
/// The answer says the learner has none of something, over evidence that saw only part.
/// </summary>
/// <remarks>
/// <para>
/// A negative is the one claim a page cannot support. "You have no verbs to review" is true only
/// across the whole population, and a read that returned the first twenty rows establishes nothing
/// about the twenty-first. The evidence panel already distinguishes a complete set from a page;
/// this rule is what makes that distinction bind on the prose beside it.
/// </para>
/// <para>
/// Unlike the unverified-state rule, this one fires <em>even when a read happened</em> — a
/// successful paged read is exactly the case it exists for.
/// </para>
/// </remarks>
public sealed class CoachNegativeClaimWithoutCoverageRule : ICoachClaimRule
{
    public CoachClaimRuleCode Code => CoachClaimRuleCode.NegativeClaimWithoutCoverage;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.EvidenceCoversCompleteSet)
        {
            yield break;
        }

        foreach (var span in context.Spans)
        {
            if (CoachLearnerStateReferent.IsUnboundedNegative(span.Text))
            {
                yield return new CoachClaimFinding(
                    Code, CoachClaimRepairAction.None, span.BlockIndex, span.SpanIndex);
            }
        }
    }
}

/// <summary>
/// The answer says it checked, and the trace says otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Narrower than the unverified-state rule and worse when it fires. "Let me look at your
/// vocabulary… you have thirty words due" is not merely unsupported, it describes an action that
/// did not occur. A learner has no way to catch that and every reason to believe it.
/// </para>
/// <para>
/// The phrase list is deliberately about the <em>act of checking</em>, not about certainty. "It
/// looks like" is hedging, and hedging is honest; "I checked" is a factual assertion about the
/// turn.
/// </para>
/// </remarks>
public sealed class CoachFabricatedCheckRule : ICoachClaimRule
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>First-person assertions that a read occurred.</summary>
    private static readonly Regex CheckAssertion = new(
        @"\b(i|i've|i have)\s+(just\s+)?(checked|looked(\s+at)?|reviewed|pulled(\s+up)?|" +
        @"searched|read|examined|fetched|queried|counted)\b" +
        @"|\b(let me|i'll|i will)\s+(check|look|pull|search|count)\b" +
        @"|\b(looking|checking)\s+at\s+your\b" +
        @"|\bafter\s+(checking|reviewing|looking\s+at)\b",
        Options);

    public CoachClaimRuleCode Code => CoachClaimRuleCode.FabricatedCheck;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // No trace is no evidence of absence. Only a recorded turn can prove a check did not run.
        if (context.Trace is null || context.TraceShowsASuccessfulRead)
        {
            yield break;
        }

        foreach (var span in context.Spans)
        {
            if (CheckAssertion.IsMatch(span.Text))
            {
                yield return new CoachClaimFinding(
                    Code, CoachClaimRepairAction.None, span.BlockIndex, span.SpanIndex);
            }
        }
    }
}

/// <summary>
/// The answer names a ranking the evidence did not produce, or contradicts the one it did.
/// </summary>
/// <remarks>
/// <para>
/// "Your most-practised resources" reads as a ranking whether or not one exists, and an unordered
/// read presented as a ranking invents a hierarchy the learner will then act on. The evidence
/// states its order; this rule refuses to let prose upgrade <c>Unordered</c> into a top-of-list.
/// </para>
/// <para>
/// <b>And it refuses to let prose rename an order that exists.</b> The rule previously exited the
/// moment any evidence item stated a real order — "specifically about prose outrunning evidence
/// that exists" — which inverted its usefulness: it caught prose ranking an admittedly unranked
/// set, and went silent on prose describing a mastery-ranked read as "your newest words". The
/// second is the worse answer of the two. It is specific, confident, and wrong about the one thing
/// the learner uses to decide what to study first, and over a corpus where most reads do declare an
/// order it was the case the rule could never see.
/// </para>
/// <para>
/// <b>Two paths, and the older one is unchanged.</b> Where no evidence item states a ranking, any
/// rank marker still fires exactly as before — including bare superlatives with no measure, because
/// over an unordered set every one of them is unsupported. Where a ranking <em>is</em> stated, the
/// claim must resolve to a measure and a direction before it can contradict anything, and an
/// unresolvable marker stays silent. Guessing which of two readings a word meant, and firing on the
/// one the answer did not intend, teaches the model that describing a ranking at all is unsafe.
/// </para>
/// <para>
/// Nothing here reads a model-supplied order code, a router label, or persists any text: the rule
/// reduces prose to a closed meaning and compares it to a closed order.
/// </para>
/// </remarks>
public sealed class CoachOrderClaimMismatchRule : ICoachClaimRule
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>Superlatives and rank words that only mean something over an ordered set.</summary>
    /// <remarks>
    /// The unordered-evidence path only. Over a read that declared no order, "top" and "best" are
    /// unsupported whatever they are ranking by, so the measure never has to be resolved.
    /// </remarks>
    private static readonly Regex OrderClaim = new(
        @"\b(most|least|top|bottom|highest|lowest|best|worst|strongest|weakest|newest|oldest|latest)\b" +
        @"|\b(ranked|sorted|ordered)\s+by\b" +
        @"|\byour\s+(first|last)\s+\w+\s+(is|are|was|were)\b" +
        @"|가장|제일",
        Options);

    public CoachClaimRuleCode Code => CoachClaimRuleCode.OrderClaimMismatch;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // With no evidence at all there is no order to contradict; the unverified-state rule owns
        // that turn. This rule is specifically about prose outrunning the evidence the turn built.
        if (context.Evidence.Count == 0)
        {
            yield break;
        }

        var statedOrders = context.Evidence
            .Select(item => item.Order)
            .Where(CoachOrderClaimSemantics.StatesARanking)
            .Select(order => order!.Value)
            .ToArray();

        foreach (var span in context.Spans)
        {
            if (statedOrders.Length == 0)
            {
                // Unchanged behaviour: any rank marker over a read that declared no ranking.
                if (OrderClaim.IsMatch(span.Text))
                {
                    yield return new CoachClaimFinding(
                        Code, CoachClaimRepairAction.None, span.BlockIndex, span.SpanIndex);
                }

                continue;
            }

            var claim = CoachOrderClaimSemantics.Parse(span.Text);
            if (!claim.IsResolved)
            {
                continue;
            }

            // Compatible with any one stated order is enough. An answer over several reads may be
            // describing the ranking of one of them, and firing because it did not describe all of
            // them would punish a true sentence.
            if (statedOrders.Any(order => CoachOrderClaimSemantics.Matches(claim, order)))
            {
                continue;
            }

            yield return new CoachClaimFinding(
                Code, CoachClaimRepairAction.None, span.BlockIndex, span.SpanIndex);
        }
    }
}

/// <summary>
/// The answer states a number the evidence does not support.
/// </summary>
/// <remarks>
/// <para>
/// The live defect verbatim: a correct fifteen-minute revision narrated as "12 minutes total, with
/// a 5-word vocabulary review and a 10-minute reading activity". Every number wrong, the underlying
/// data right the whole time.
/// </para>
/// <para>
/// Scoped to learner-state spans. "Korean has 7 speech levels" is a fact about the language and
/// must survive; the referent test is what separates it from "you have 7 words due". This is the
/// concrete reason B6 forbids a digit ban.
/// </para>
/// <para>
/// Small numbers are exempt. One through three appear constantly as ordinary quantifiers — "one of
/// your resources", "the first two" — and matching them against a count set produces noise that
/// buries the real findings.
/// </para>
/// </remarks>
public sealed class CoachCountClaimMismatchRule : ICoachClaimRule
{
    /// <summary>Below this, a number is prose rather than a count.</summary>
    private const int SmallNumberCeiling = 3;

    public CoachClaimRuleCode Code => CoachClaimRuleCode.CountClaimMismatch;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Evidence.Count == 0)
        {
            yield break;
        }

        var supported = context.SupportedCounts;

        foreach (var span in context.Spans)
        {
            if (!CoachLearnerStateReferent.IsLearnerStateClaim(span.Text))
            {
                continue;
            }

            foreach (var stated in CoachLearnerStateReferent.StatedCounts(span.Text))
            {
                if (stated > SmallNumberCeiling && !supported.Contains(stated))
                {
                    yield return new CoachClaimFinding(
                        Code,
                        CoachClaimRepairAction.None,
                        span.BlockIndex,
                        span.SpanIndex,
                        ClaimedCount: stated,
                        EvidenceCount: supported.Count == 0 ? null : supported.Max());
                }
            }
        }
    }
}

/// <summary>
/// Rows were held back and the answer presents the remainder as everything.
/// </summary>
/// <remarks>
/// Withholding is correct — a due word must not be named in an answer the learner is about to be
/// tested on. Withholding silently is not. The count is the disclosure; the reason is the courtesy.
/// This rule fires on the count alone, because an answer that omits the number has already told the
/// learner they saw the whole set.
/// </remarks>
public sealed class CoachWithheldNotDisclosedRule : ICoachClaimRule
{
    public CoachClaimRuleCode Code => CoachClaimRuleCode.WithheldNotDisclosed;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.WithheldTotal <= 0)
        {
            yield break;
        }

        if (IsStructurallyDisclosed(context))
        {
            yield break;
        }

        // Reported against the answer rather than a span: the defect is an absence, and an absence
        // has no coordinates. A finding pinned to an arbitrary span would send a reader to a
        // sentence that is not wrong.
        yield return new CoachClaimFinding(
            Code,
            CoachClaimRepairAction.None,
            BlockIndex: null,
            SpanIndex: null,
            ClaimedCount: null,
            EvidenceCount: context.WithheldTotal);
    }

    /// <summary>
    /// Whether the server's own evidence discloses the withholding, in any language.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This replaced an English regex, and the regex was the bug.</b> Disclosure was established
    /// by matching words like "withheld" and "not shown" in the answer's spans. A Korean answer
    /// that disclosed the withholding perfectly matched none of them, so the rule fired on a coach
    /// that had done exactly the right thing — and at Enforce it refused the turn for it. A
    /// classifier that only recognises honesty in English is not a grounding rule; it is an English
    /// test wearing one.
    /// </para>
    /// <para>
    /// <b>The disclosure is structural, so it is language-independent.</b> A visible evidence item
    /// carrying <c>WithheldCount &gt; 0</c> together with a known, non-<c>None</c> reason <em>is</em>
    /// the disclosure: the client renders that pair in the learner's own language, and every
    /// supported language gets the same statement. The server does not need to read prose to know
    /// what it published.
    /// </para>
    /// <para>
    /// <b>An incoherent pair discloses nothing.</b> A count with no reason cannot be rendered as a
    /// sentence — "4 held back" with no because — and a reason with no count states no scale. Both
    /// leave the finding standing and unrepairable, which is the honest outcome: the answer withheld
    /// rows and the panel beside it cannot say why.
    /// </para>
    /// </remarks>
    private static bool IsStructurallyDisclosed(CoachClaimRuleContext context)
    {
        foreach (var item in context.Evidence)
        {
            if (item is null)
            {
                continue;
            }

            if (item.WithheldCount is not > 0)
            {
                continue;
            }

            if (item.WithheldReason is not { } reason
                || reason == CoachWithheldReason.None
                || reason == CoachWithheldReason.Unknown
                || !Enum.IsDefined(reason))
            {
                // A count the panel cannot explain. Not disclosure.
                continue;
            }

            return true;
        }

        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Capability rules: can the app actually do this? Plan §5.6. All three are new.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The answer proposes something this build cannot do. AC-F2.
/// </summary>
/// <remarks>
/// <para>
/// Over-claiming. The answer offers to change a theme, start an activity, or write something, and
/// the manifest resolves that capability to anything other than <c>Present</c> — because the stage
/// has not been promoted, because the client did not advertise it, or because it does not exist.
/// </para>
/// <para>
/// The repair is not a refusal. AC-F2 requires the answer to name <c>/settings</c> and forbids a
/// flat "I cannot", which is why this rule's repair path goes through a W7 limitation carrying a
/// real destination. A learner told "no" learns the app cannot do it; a learner told "on the
/// settings screen" gets what they came for.
/// </para>
/// </remarks>
public sealed class CoachCapabilityAbsentRule : ICoachClaimRule
{
    private readonly ICoachCapabilityResolver _resolver;

    public CoachCapabilityAbsentRule(ICoachCapabilityResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public CoachClaimRuleCode Code => CoachClaimRuleCode.CapabilityAbsent;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var capability in context.ProposedCapabilities)
        {
            var availability = _resolver.Resolve(capability, context.Stage, context.Handshake);

            if (availability != CoachCapabilityAvailability.Present)
            {
                yield return new CoachClaimFinding(Code, CoachClaimRepairAction.None);
            }
        }
    }
}

/// <summary>
/// The answer claims inability for something the app plainly does. AC-F3.
/// </summary>
/// <remarks>
/// <para>
/// Under-claiming, and the direction that is easy to miss because it looks like caution. A coach
/// that says "I can't change your theme" when the theme capability resolves <c>Present</c> has told
/// the learner a falsehood that costs them a feature. If it resolves
/// <c>PresentOnAnotherSurface</c>, the honest answer is the screen, not "no".
/// </para>
/// <para>
/// Detection needs both halves: an inability phrase <em>and</em> a capability the caller proposed.
/// Matching inability language alone would fire on "I can't tell you today's answers", which is a
/// correct refusal and a W7 boundary.
/// </para>
/// </remarks>
public sealed class CoachFalseLimitationRule : ICoachClaimRule
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>First-person statements of inability.</summary>
    private static readonly Regex Inability = new(
        @"\b(i\s+can['\u2019]?t|i\s+cannot|i\s+am\s+not\s+able|i['\u2019]?m\s+not\s+able|" +
        @"i\s+don['\u2019]?t\s+have\s+the\s+ability|that['\u2019]?s\s+not\s+something\s+i\s+can|" +
        @"i\s+am\s+unable|i['\u2019]?m\s+unable)\b" +
        @"|\b(isn['\u2019]?t|is\s+not)\s+(something|a\s+thing)\s+i\s+can\b" +
        @"|\bnot\s+supported\b",
        Options);

    private readonly ICoachCapabilityResolver _resolver;

    public CoachFalseLimitationRule(ICoachCapabilityResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public CoachClaimRuleCode Code => CoachClaimRuleCode.FalseLimitation;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var capable = context.ProposedCapabilities.Any(capability =>
            _resolver.Resolve(capability, context.Stage, context.Handshake)
                is CoachCapabilityAvailability.Present
                or CoachCapabilityAvailability.PresentOnAnotherSurface);

        if (!capable)
        {
            yield break;
        }

        foreach (var span in context.Spans)
        {
            if (Inability.IsMatch(span.Text))
            {
                yield return new CoachClaimFinding(
                    Code, CoachClaimRepairAction.None, span.BlockIndex, span.SpanIndex);
            }
        }
    }
}

/// <summary>
/// A proposed capability changes something and the answer does not say so. AC-G2.
/// </summary>
/// <remarks>
/// <para>
/// Registered before anything can trigger it, per the plan — the rule exists from W6 even though
/// the capabilities that will exercise it are post-gate. A rule added at the same time as the first
/// capability that needs it is a rule nobody reviewed under pressure.
/// </para>
/// <para>
/// Only effect classes that actually change something are in scope. <c>Read</c> has nothing to
/// disclose, and demanding a disclosure sentence beside every read would train both the model and
/// the reader to ignore the disclosure that matters.
/// </para>
/// </remarks>
public sealed class CoachSideEffectNotDisclosedRule : ICoachClaimRule
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>Language that states a consequence before it happens.</summary>
    private static readonly Regex Disclosure = new(
        @"\b(this\s+will|that\s+will|it\s+will|i['\u2019]?ll)\s+\w+" +
        @"|\b(chang(e|es|ing)|updat(e|es|ing)|sav(e|es|ing)|start(s|ing)?|creat(e|es|ing)|" +
        @"remov(e|es|ing)|delet(e|es|ing)|publish(es|ing)?|appl(y|ies|ying))\b" +
        @"|\byou\s+can\s+undo\b" +
        @"|\bcan['\u2019]?t\s+be\s+undone\b",
        Options);

    private readonly ICoachCapabilityManifest _manifest;

    public CoachSideEffectNotDisclosedRule(ICoachCapabilityManifest manifest)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }

    public CoachClaimRuleCode Code => CoachClaimRuleCode.SideEffectNotDisclosed;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var discloses = context.Spans.Any(span => Disclosure.IsMatch(span.Text));

        foreach (var capability in context.ProposedCapabilities)
        {
            var descriptor = _manifest.Find(capability);

            if (descriptor is null || descriptor.EffectClass == CoachCapabilityEffectClass.Read)
            {
                continue;
            }

            if (!discloses)
            {
                yield return new CoachClaimFinding(Code, CoachClaimRepairAction.None);
            }
        }
    }
}
