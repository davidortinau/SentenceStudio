using System.Text.RegularExpressions;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// While a dispute is open, the next answer must do one of three things.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect is Case D.</b> The coach repeated a disputed list with more confidence. The
/// learner had already said it was wrong; answering again, more firmly, told them the correction
/// went nowhere. A wrong answer is a mistake. A wrong answer that survives being corrected is a
/// system the learner cannot reach, and there is no recovery from that inside the conversation.
/// </para>
/// <para>
/// <b>The three exits, and why they are the only three.</b> Each one is something the coach
/// <em>did</em>, observable in the trace or in the answer's own words:
/// </para>
/// <list type="number">
/// <item>
/// <b>Re-read with materially different parameters.</b> Different <em>typed</em> parameters, from
/// the trace — not a second call to the same definition. Case D's repeat cited the same read; if
/// the same read were an exit, the rule would permit exactly the behaviour it exists to stop.
/// </item>
/// <item>
/// <b>Correct the prior claim by name.</b> AC-S14 requires the prior claim to be <em>named</em>,
/// not quietly replaced. A learner who is silently given a different answer cannot tell whether
/// they were heard or whether the coach is guessing again.
/// </item>
/// <item>
/// <b>State an honest limitation.</b> W7's territory. "I looked and I cannot tell you" resolves a
/// dispute, because it is a true statement about what happened rather than another attempt.
/// </item>
/// </list>
/// <para>
/// <b>The flag is a total bypass.</b> With <c>Coach:CorrectionState:Enabled</c> off there is no
/// dispute to be open, so this rule never fires — enforced by the context carrying no dispute
/// rather than by a check inside the rule, so an off deployment cannot be half-on.
/// </para>
/// </remarks>
public sealed class CoachRepeatedDisputedClaimRule : ICoachClaimRule
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>
    /// An explicit admission that the coach's own earlier claim was wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Narrowed after the language review, and the narrowing is the point.</b> The first draft
    /// closed a dispute on <c>\b(i\s+(said|told you|reported|listed|counted))\b</c> and on the bare
    /// words <c>correcting|correction</c>. Both are things a coach says while doing the exact thing
    /// the learner objected to: "I counted 12 again" contains "I counted", names no error, and
    /// repeats the disputed number — and it was closing the dispute that existed to stop it.
    /// "Correction:" as a discourse label did the same.
    /// </para>
    /// <para>
    /// What remains requires a <em>past self-error admission</em>: the speaker says an earlier claim
    /// of theirs was wrong, mistaken, misread or miscounted. "I said 12" is a restatement; "my
    /// earlier count was wrong" is an admission. Only the second is a resolution, because only the
    /// second tells the learner their correction was accepted.
    /// </para>
    /// <para>
    /// <b>Tied to the prior claim, not free-floating.</b> Every alternative below names either the
    /// speaker's earlier statement ("my earlier answer", "what I said before") or a past error of
    /// their own ("I was wrong", "I misread"). A generic apology still resolves nothing: "sorry
    /// about that" acknowledges a feeling and names no claim, which is what AC-S14 asks for.
    /// </para>
    /// </remarks>
    private static readonly Regex NamesPriorClaim = new(
        // "I was wrong", "I got that wrong", "I got it wrong".
        @"\b(i\s+(was|got)\s+(wrong|that\s+wrong|it\s+wrong))\b" +

        // A past error of the speaker's own, named as an error.
        @"|\b(i\s+(misread|miscounted|misspoke|miscalculated|mistook))\b" +
        @"|\b(i\s+(had|have)\s+(that|it|this)\s+wrong)\b" +

        // The speaker's earlier statement, named and called wrong. The adjective is required: "my
        // earlier answer" alone is a reference, not an admission.
        @"|\b(my\s+(earlier|previous|last|first)\s+" +
        @"(answer|reply|claim|count|list|number|statement)\s+" +
        @"(was|is)\s+(wrong|incorrect|not\s+right|mistaken|off))\b" +

        // "what I said before was wrong", "what I told you earlier was incorrect".
        @"|\b(what\s+i\s+(said|told\s+you)\s+(before|earlier|last\s+time)\s+" +
        @"(was|is)\s+(wrong|incorrect|not\s+right|mistaken))\b" +

        // "that was wrong" — anaphoric, and dangerous bare. In a language tutor the overwhelmingly
        // common referent is the learner's last utterance: every correction Sam gives is some form
        // of "that was wrong, here is the right form". Read bare, the act of teaching discharged
        // the dispute, on the turn most likely to still be repeating the disputed claim. It counts
        // only when the same span anchors it to a prior claim of the speaker's own.
        @"|(?=[^\n]*\b(i\s+(said|told\s+you|gave\s+you|reported|listed|counted|answered)" +
        @"|my\s+(earlier|previous|last|first)\b|what\s+i\s+said)\b)" +
        @"(?=[^\n]*\b(that|it)\s+was\s+(wrong|incorrect|not\s+right)\b)" +

        // Korean: 제가 잘못 (I was mistaken / I got it wrong), and the 말씀드린 forms that name an
        // earlier statement of the speaker's own.
        @"|(\uC81C\uAC00\s*\uC798\uBABB)|(\uC81C\uAC00\s*\uD2C0\uB838)" +
        @"|(\uC544\uAE4C\s*\uB9D0\uC500\uB4DC\uB9B0)" +
        @"|(\uC55E\uC11C\s*\uB9D0\uC500\uB4DC\uB9B0)|(\uC798\uBABB\s*\uB9D0\uC500\uB4DC\uB838)",
        Options);

    /// <summary>
    /// Limitation codes that can discharge a dispute, because they bound the disputed claim itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not every limitation is about this dispute.</b> A turn can carry a limitation and an open
    /// dispute at the same time without the one being about the other: "I will not remove all 412
    /// of your words" refuses a bulk change and says nothing about whether the count the learner
    /// disputed was right. Closing on it would hand the coach an escape hatch on any turn where the
    /// learner asked for two things.
    /// </para>
    /// <para>
    /// The three below are capability boundaries — the answer the learner is pushing for is not
    /// something this build produces, is only produced on a screen, or is refused by design. Those
    /// leave the dispute nowhere to go, so holding the constraint open would only suppress the
    /// limitation itself on every following turn. <see cref="CoachLimitationCode.Unknown"/> is
    /// excluded for the reason it is excluded everywhere: it is the documented unset value, and
    /// treating unset as sufficient turns a missing code into a close.
    /// </para>
    /// </remarks>
    private static readonly CoachLimitationCode[] LimitationCodesThatBoundTheClaim =
    [
        CoachLimitationCode.NotBuilt,
        CoachLimitationCode.AvailableOnAnotherSurface,
        CoachLimitationCode.RefusedByDesign
    ];

    public CoachClaimRuleCode Code => CoachClaimRuleCode.RepeatedDisputedClaim;

    public IEnumerable<CoachClaimFinding> Evaluate(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Dispute is not { IsOpen: true })
        {
            yield break;
        }

        if (ClassifyExit(context) != CoachDisputeExit.None)
        {
            yield break;
        }

        // Reported against the answer rather than a span. The defect is what the answer failed to
        // do, and an absence has no coordinates — pinning it to a sentence would send a reader to
        // one that is not individually wrong.
        yield return new CoachClaimFinding(Code, CoachClaimRepairAction.None);
    }

    /// <summary>
    /// Which of the three exits the answer took, or <see cref="CoachDisputeExit.None"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public because the coordinator needs the same answer to decide how a dispute closed, and two
    /// implementations of "good enough" would eventually disagree — the rule would refuse a turn the
    /// coordinator had already recorded as resolved, or worse, the reverse.
    /// </para>
    /// <para>
    /// Ordered by strength. A turn that both re-read and named its prior claim reports the re-read,
    /// because looking somewhere new is the more complete response and the ordering must be
    /// deterministic rather than dependent on which check happens to run first.
    /// </para>
    /// </remarks>
    public static CoachDisputeExit ClassifyExit(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Dispute is not { } dispute)
        {
            return CoachDisputeExit.None;
        }

        if (ReReadWithDifferentParameters(dispute, context.Trace))
        {
            return CoachDisputeExit.ReRead;
        }

        var spans = context.Spans;

        if (spans.Any(span => NamesPriorClaim.IsMatch(span.Text)))
        {
            return CoachDisputeExit.NamedCorrection;
        }

        // Typed, never prose. A phrase list matching "I can't tell you that" could be produced by a
        // model that had consulted nothing and declared nothing — which is precisely the answer a
        // standing dispute exists to constrain, so the sentence that sounds most like an honest
        // boundary was the easiest way out of the constraint. The limitation this reads is
        // projected from the turn's own findings and cannot be written by the answer text.
        if (context.Limitation is { } limitation
            && Array.IndexOf(LimitationCodesThatBoundTheClaim, limitation.Code) >= 0)
        {
            return CoachDisputeExit.Limitation;
        }

        return CoachDisputeExit.None;
    }

    /// <summary>
    /// True when the turn read something the disputed answer did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compared by <see cref="CoachScopeDefinition"/> — the typed question a read answers — rather
    /// than by tool name or by call count. Two calls to the same definition with different
    /// arguments are the same question asked twice, and Case D's more-confident repeat would pass a
    /// count-based check trivially.
    /// </para>
    /// <para>
    /// A definition the disputed answer never used is the signal, so a re-read that also repeats an
    /// old read still resolves: the coach looked somewhere new, which is what the learner asked for.
    /// </para>
    /// </remarks>
    private static bool ReReadWithDifferentParameters(
        CoachTurnDisputeState dispute,
        CoachTurnTraceSummary? trace)
    {
        if (trace is null)
        {
            return false;
        }

        var disputed = dispute.DisputedDefinitionCodes;

        return trace.Calls.Any(call =>
            call.Outcome == Tools.Observation.CoachToolCallOutcome.Succeeded
            && call.DefinitionCode != CoachScopeDefinition.Unspecified
            && !disputed.Contains(call.DefinitionCode));
    }
}

/// <summary>
/// How an answer satisfied an open dispute, if it did.
/// </summary>
/// <remarks>
/// The three permitted exits, plus the case that is not an exit. "Answered again" is deliberately
/// absent: it is the behaviour the rule exists to refuse, and giving it a member would invite a
/// future caller to treat it as a fourth way out.
/// </remarks>
public enum CoachDisputeExit
{
    /// <summary>The answer did none of the three things. This is the refusal case.</summary>
    None = 0,

    /// <summary>The turn read a definition the disputed answer did not.</summary>
    ReRead = 1,

    /// <summary>The answer named and corrected its prior claim.</summary>
    NamedCorrection = 2,

    /// <summary>The answer stated an honest limitation.</summary>
    Limitation = 3
}
