using System.Text.RegularExpressions;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>What an ordering claim in prose is about.</summary>
/// <remarks>
/// A closed set, and deliberately smaller than the space of things prose can say. Every member maps
/// onto the measures <see cref="CoachEvidenceOrder"/> already names; a claim that resolves to none
/// of them is <see cref="Ambiguous"/> and never fires, because the alternative is a rule that
/// invents a contradiction out of a word it did not understand.
/// </remarks>
internal enum CoachOrderClaimMeaning
{
    /// <summary>The span makes no ordering claim at all.</summary>
    None = 0,

    /// <summary>A ranking marker with no measure this build can resolve. Never fires.</summary>
    Ambiguous = 1,

    /// <summary>How well the learner knows the rows.</summary>
    Mastery = 2,

    /// <summary>How recently the rows were added, updated, or used.</summary>
    Recency = 3,

    /// <summary>How much time was spent.</summary>
    Minutes = 4,

    /// <summary>How often the rows come up.</summary>
    Frequency = 5,

    /// <summary>How important the rows are.</summary>
    Priority = 6,

    /// <summary>The prose says there is no ranking.</summary>
    NoOrder = 7
}

/// <summary>Which end of the measure the prose puts first.</summary>
internal enum CoachOrderClaimDirection
{
    /// <summary>Not a directional claim, or the direction is not stated.</summary>
    Unspecified = 0,

    /// <summary>Strongest, newest, longest, most frequent, highest priority.</summary>
    Most = 1,

    /// <summary>Weakest, oldest, shortest, least frequent, lowest priority.</summary>
    Least = 2
}

/// <summary>One parsed ordering claim.</summary>
internal readonly record struct CoachOrderClaim(
    CoachOrderClaimMeaning Meaning,
    CoachOrderClaimDirection Direction)
{
    /// <summary>The span said nothing about order.</summary>
    public static readonly CoachOrderClaim None = new(CoachOrderClaimMeaning.None, CoachOrderClaimDirection.Unspecified);

    /// <summary>A ranking marker this build cannot resolve to a measure.</summary>
    public static readonly CoachOrderClaim Ambiguous =
        new(CoachOrderClaimMeaning.Ambiguous, CoachOrderClaimDirection.Unspecified);

    /// <summary>True when the claim names a measure that can be compared to a stated order.</summary>
    public bool IsResolved =>
        Meaning is not (CoachOrderClaimMeaning.None or CoachOrderClaimMeaning.Ambiguous);
}

/// <summary>
/// Reads an ordering claim out of one display span, and says whether it contradicts a stated order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a bag of superlatives.</b> The rule this feeds used to hold one regex of rank
/// words and bail entirely whenever the evidence stated any order at all — so "your newest words"
/// over a mastery-ranked read went uncaught, and the only thing the rule could catch was prose
/// ranking an explicitly unordered set. Catching the contradiction needs the claim reduced to a
/// <em>measure and a direction</em>, because that is the only form comparable to
/// <see cref="CoachEvidenceOrder"/>.
/// </para>
/// <para>
/// <b>Ambiguity is silence, always.</b> "Your most-practised resource" could mean the most minutes
/// or the most sessions, and the two map to different orders. A parser that guessed would fire on
/// an answer that was correct under the other reading, and a false order-mismatch teaches the model
/// that describing a ranking at all is unsafe. Unresolvable markers return
/// <see cref="CoachOrderClaimMeaning.Ambiguous"/> and stop there.
/// </para>
/// <para>
/// <b>Only in an ordering construction.</b> "You practised recently" makes no ordering claim, and a
/// bare recency word must not be read as one. Every recency marker here requires a superlative or an
/// explicit sort phrase, which is what keeps ordinary prose out of the rule.
/// </para>
/// <para>
/// <b>Bounded English and Korean.</b> Two languages, closed marker lists, no model-supplied order
/// code and no text kept anywhere. The Korean set is the display copy the coach actually writes;
/// it is not a general parser and does not pretend to be.
/// </para>
/// </remarks>
internal static class CoachOrderClaimSemantics
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    // ── No order at all ──────────────────────────────────────────────────────

    private static readonly Regex NoOrder = new(
        @"\bin\s+no\s+particular\s+order\b|\bno\s+particular\s+order\b" +
        @"|\bnot\s+(ranked|sorted|ordered)\b|\bunranked\b" +
        @"|순서\s*없이|특별한\s*순서\s*없이|정렬하지\s*않",
        Options);

    // ── Mastery ──────────────────────────────────────────────────────────────

    private static readonly Regex MasteryMost = new(
        @"\bstrongest\b|\bbest[\s-]known\b|\bhighest\s+mastery\b" +
        @"|\bmost\s+(confident|mastered)\b" +
        @"|\b(sorted|ordered|ranked)\s+by\s+(mastery|how\s+well)\b" +
        @"|\bby\s+how\s+well\s+you\s+know\b|\byou\s+know\s+(them\s+)?best\b" +
        @"|가장\s*잘\s*아는|가장\s*잘\s*알고|제일\s*잘\s*아는|숙련도가\s*가장\s*높은",
        Options);

    private static readonly Regex MasteryLeast = new(
        @"\bweakest\b|\bleast[\s-]known\b|\blowest\s+mastery\b" +
        @"|\bleast\s+(confident|mastered)\b" +
        @"|\byou\s+know\s+(them\s+)?least(\s+well)?\b" +
        @"|가장\s*잘\s*모르는|숙련도가\s*가장\s*낮은|제일\s*약한",
        Options);

    // ── Recency ──────────────────────────────────────────────────────────────
    //
    // Every marker is superlative or an explicit sort phrase. A bare "recently" is ordinary prose
    // and must not be read as a ranking, which is the false positive this rule is most exposed to.

    private static readonly Regex RecencyMost = new(
        @"\bnewest\b|\blatest\b|\bmost\s+recent(ly)?\b" +
        @"|\b(sorted|ordered|ranked)\s+by\s+(when|recency|date|how\s+recently)\b" +
        @"|\bin\s+the\s+order\s+you\s+(added|created)\b" +
        @"|가장\s*최근|제일\s*최근|최근에\s*추가|최신",
        Options);

    private static readonly Regex RecencyLeast = new(
        @"\boldest\b|\bleast\s+recent(ly)?\b|\blongest\s+ago\b" +
        @"|가장\s*오래된|가장\s*예전",
        Options);

    // ── Minutes ──────────────────────────────────────────────────────────────

    private static readonly Regex MinutesMost = new(
        @"\bmost\s+(minutes|time)\b|\bspent\s+the\s+most\s+time\b" +
        @"|\b(sorted|ordered|ranked)\s+by\s+(minutes|time)\b" +
        @"|시간이\s*가장\s*많은|가장\s*많은\s*시간|가장\s*오래\s*연습",
        Options);

    private static readonly Regex MinutesLeast = new(
        @"\bleast\s+(minutes|time)\b|\bspent\s+the\s+least\s+time\b" +
        @"|시간이\s*가장\s*적은",
        Options);

    // ── Frequency ────────────────────────────────────────────────────────────
    //
    // Checked after recency on purpose: "most recently used" is a recency claim, and a frequency
    // pattern loose enough to catch "most used" would swallow it.

    private static readonly Regex FrequencyMost = new(
        @"\bmost\s+(frequent(ly)?|common|often)\b|\bcommonest\b" +
        @"|\b(sorted|ordered|ranked)\s+by\s+frequency\b" +
        @"|가장\s*자주|제일\s*자주|가장\s*흔한",
        Options);

    private static readonly Regex FrequencyLeast = new(
        @"\bleast\s+(frequent(ly)?|common|often)\b|\brarest\b" +
        @"|가장\s*드문",
        Options);

    // ── Priority ─────────────────────────────────────────────────────────────

    private static readonly Regex PriorityMost = new(
        @"\b(highest|top)\s+priority\b|\bmost\s+important\b" +
        @"|\b(sorted|ordered|ranked)\s+by\s+priority\b" +
        @"|우선순위가\s*가장\s*높은|가장\s*중요한",
        Options);

    private static readonly Regex PriorityLeast = new(
        @"\blowest\s+priority\b|\bleast\s+important\b" +
        @"|우선순위가\s*가장\s*낮은",
        Options);

    // ── Unresolvable ranking markers ─────────────────────────────────────────

    /// <summary>
    /// Rank words with no measure attached. Present so the parser can say "an ordering claim was
    /// made and I could not name it" rather than "no claim was made" — the two lead to different
    /// decisions in the rule.
    /// </summary>
    private static readonly Regex AmbiguousRanking = new(
        @"\b(most|least|top|bottom|highest|lowest|best|worst)\b" +
        @"|\b(ranked|sorted|ordered)\s+by\b" +
        @"|\bmost[\s-]practi[cs]ed\b" +
        @"|가장|제일",
        Options);

    /// <summary>The ordering claim this span makes, if any.</summary>
    public static CoachOrderClaim Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CoachOrderClaim.None;
        }

        if (NoOrder.IsMatch(text))
        {
            return new CoachOrderClaim(CoachOrderClaimMeaning.NoOrder, CoachOrderClaimDirection.Unspecified);
        }

        // Order matters: the more specific measure wins, and recency is tested before frequency so
        // "most recently used" cannot be read as "most used".
        if (Match(MasteryMost, MasteryLeast, text) is { } mastery)
        {
            return new CoachOrderClaim(CoachOrderClaimMeaning.Mastery, mastery);
        }

        if (Match(RecencyMost, RecencyLeast, text) is { } recency)
        {
            return new CoachOrderClaim(CoachOrderClaimMeaning.Recency, recency);
        }

        if (Match(MinutesMost, MinutesLeast, text) is { } minutes)
        {
            return new CoachOrderClaim(CoachOrderClaimMeaning.Minutes, minutes);
        }

        if (Match(FrequencyMost, FrequencyLeast, text) is { } frequency)
        {
            return new CoachOrderClaim(CoachOrderClaimMeaning.Frequency, frequency);
        }

        if (Match(PriorityMost, PriorityLeast, text) is { } priority)
        {
            return new CoachOrderClaim(CoachOrderClaimMeaning.Priority, priority);
        }

        return AmbiguousRanking.IsMatch(text) ? CoachOrderClaim.Ambiguous : CoachOrderClaim.None;
    }

    /// <summary>True when <paramref name="order"/> is a real ranking a claim can contradict.</summary>
    public static bool StatesARanking(CoachEvidenceOrder? order) =>
        order is not null
        and not CoachEvidenceOrder.Unknown
        and not CoachEvidenceOrder.Unordered
        and not CoachEvidenceOrder.NotApplicable;

    /// <summary>
    /// True when <paramref name="claim"/> describes the same ranking <paramref name="order"/>
    /// states.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Recency covers two orders.</b> <c>UpdatedDescending</c> and <c>LastUsedAscending</c> both
    /// put the most recent row first — one by edit, one by use — and the wire has no order for "when
    /// it was added". Distinguishing those in prose would be guessing at which recency the writer
    /// meant, so "newest" is treated as compatible with either. The claim that fires is the one
    /// pointing at the other end, or at a different measure entirely.
    /// </para>
    /// <para>
    /// <b>BandLabelAscending matches nothing.</b> It is ordered by the band label itself and, in the
    /// enum's own words, "not by any measure of the learner" — so every learner-measure claim over
    /// it is a claim the read did not support.
    /// </para>
    /// </remarks>
    public static bool Matches(CoachOrderClaim claim, CoachEvidenceOrder order) =>
        (claim.Meaning, claim.Direction, order) switch
        {
            (CoachOrderClaimMeaning.Mastery, CoachOrderClaimDirection.Most, CoachEvidenceOrder.MasteryDescending) => true,

            (CoachOrderClaimMeaning.Recency, CoachOrderClaimDirection.Most, CoachEvidenceOrder.UpdatedDescending) => true,
            (CoachOrderClaimMeaning.Recency, CoachOrderClaimDirection.Most, CoachEvidenceOrder.LastUsedAscending) => true,

            (CoachOrderClaimMeaning.Minutes, CoachOrderClaimDirection.Most, CoachEvidenceOrder.MinutesDescending) => true,
            (CoachOrderClaimMeaning.Frequency, CoachOrderClaimDirection.Most, CoachEvidenceOrder.FrequencyDescending) => true,
            (CoachOrderClaimMeaning.Priority, CoachOrderClaimDirection.Most, CoachEvidenceOrder.PriorityAscending) => true,

            _ => false
        };

    private static CoachOrderClaimDirection? Match(Regex most, Regex least, string text)
    {
        if (most.IsMatch(text))
        {
            return CoachOrderClaimDirection.Most;
        }

        return least.IsMatch(text) ? CoachOrderClaimDirection.Least : null;
    }
}
