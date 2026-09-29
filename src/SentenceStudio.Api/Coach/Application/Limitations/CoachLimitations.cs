using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application.Limitations;

/// <summary>
/// Builds the two boundary answers Sam has to get right, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class touches no data.</b> Every count arrives as a parameter, from an application
/// service the caller already holds. That is what keeps a limitation renderable in both hosts, in a
/// unit test, and in a synthetic acceptance run without a database — and it is what keeps the
/// no-DbContext-under-Coach boundary intact by construction rather than by review.
/// </para>
/// <para>
/// <b>It also produces no prose.</b> Codes, counts, a typed destination, closed alternatives. The
/// sentence is <c>CoachDeterministicCopy</c>'s job and the localisation is the client's, so a
/// number can never end up inside a translated string where the next release will strand it.
/// </para>
/// <para>
/// <b>Neither answer navigates or executes anything.</b> W7 is metadata. A learner who wants the
/// destination still has to tap it, which is the same authorization rule every other launch obeys.
/// </para>
/// </remarks>
public static class CoachLimitations
{
    /// <summary>
    /// The ladder, in one place so no caller can reorder it or drop a rung.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ascending in support and shared by every S16 answer. A per-call ladder would let one code
    /// path start at the rung nearest the answer.
    /// </para>
    /// <para>
    /// <b>The cloze sits below the form cue, and that ordering is language-specific.</b> The first
    /// draft placed the form cue second on the English intuition that an initial letter and a
    /// length are a small nudge. In Korean they are very nearly the answer: the target is often two
    /// or three syllable blocks, and the first block plus the count leaves a candidate set a
    /// learner can frequently close by elimination without retrieving anything. A cloze, by
    /// contrast, supplies context and still requires the learner to produce the whole form. So
    /// support ascends context-first — category, then cloze, then the form-revealing cue — and the
    /// ladder is monotonic in how much of the <em>form</em> it discloses, which is the property the
    /// retrieval is protecting.
    /// </para>
    /// <para>
    /// The enum ordinals are untouched. <see cref="CoachHintKind"/> numbers the kinds; this numbers
    /// the rungs, and conflating the two is what made the original order look self-evident.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<CoachHintRungDto> HintLadder =
    [
        new CoachHintRungDto(1, CoachHintKind.Category),
        new CoachHintRungDto(2, CoachHintKind.Cloze),
        new CoachHintRungDto(3, CoachHintKind.FormCue)
    ];

    /// <summary>
    /// How much of the target's written form a rung discloses. Higher reveals more.
    /// </summary>
    /// <remarks>
    /// The ordering the ladder must be monotonic in, stated separately from the ladder so a test
    /// can check one against the other rather than restating the sequence. A category names none of
    /// the form; a cloze supplies surrounding context and none of the form; a form cue supplies
    /// part of the form itself.
    /// </remarks>
    public static int FormDisclosureRank(CoachHintKind kind) => kind switch
    {
        CoachHintKind.Category => 1,
        CoachHintKind.Cloze => 2,
        CoachHintKind.FormCue => 3,

        // An unrecognised rung discloses an unknown amount, which must never sort below a known
        // one. Ranking it highest keeps the monotonicity check honest instead of accommodating.
        _ => int.MaxValue
    };

    /// <summary>
    /// S15. The learner asked to delete all their vocabulary and start clean.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure mode this replaces is a coach that either does it, or refuses flatly and leaves
    /// the learner stuck with a real need. Both are wrong. The learner's goal — a clean start — is
    /// legitimate; the mechanism they proposed is unbounded and unrecoverable, and Sam is not the
    /// right actor for it.
    /// </para>
    /// <para>
    /// So: no destructive proposal is generated, the consequence is stated as a count rather than a
    /// warning adjective, and bounded alternatives follow. No whole-data or export surface is
    /// named: neither a whole-account reset nor a safe owner-scoped Profile/Settings export is
    /// available in this build.
    /// </para>
    /// </remarks>
    /// <param name="affectedWordCount">
    /// How many of the learner's words the request would remove. Server-counted, and stated so the
    /// learner is weighing a fact instead of an adjective.
    /// </param>
    /// <param name="asOfUtc">When that count was true.</param>
    /// <param name="coverage">
    /// How much of the learner's vocabulary the count covers. Defaults to
    /// <see cref="CoachEvidenceCoverage.Unknown"/>: a caller that has not said what its count spans
    /// has not established that it spans everything, and defaulting to
    /// <see cref="CoachEvidenceCoverage.CompleteOwnedSet"/> turned "nobody stated the coverage" into
    /// "the coverage is total" for every caller that forgot the argument. A complete claim now has
    /// to be made deliberately.
    /// </param>
    public static CoachLimitationDto BulkVocabularyDeletion(
        int affectedWordCount,
        DateTime asOfUtc,
        CoachEvidenceCoverage coverage = CoachEvidenceCoverage.Unknown)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(affectedWordCount);

        return new CoachLimitationDto
        {
            Code = CoachLimitationCode.ExceedsSafeChangeScope,
            Coverage = coverage,
            AsOfUtc = NormalizeAsOf(asOfUtc),

            // Null rather than zero. A rendered "0 words" reads as a fact the server checked and
            // found empty; an absent count reads as a count the answer does not make. Only one of
            // those is true when the caller had nothing to count.
            AffectedCount = affectedWordCount > 0 ? affectedWordCount : null,

            // The recommended surface is the bounded one. A learner who wants one list gone can do
            // it here, one list at a time, and see what they are removing while they do it.
            Destination = CoachRouteCatalog.Build(CoachRouteName.Vocabulary),

            // A whole-account reset is not available; never link Settings as if it were.
            FullScopeSurface = null,

            Alternatives =
            [
                CoachAlternativeCode.RemoveOneListAtATime,
                CoachAlternativeCode.StartAFreshList
            ],

            ExportSurface = null
        };
    }

    /// <summary>
    /// S16. The learner asked for today's review answers so they can keep a streak.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request is not misconduct and the answer must not read like it thinks so. The learner is
    /// telling you two true things at once: the session is too long today, and the streak matters to
    /// them. A lecture answers neither, and the acceptance criterion says so out loud — no moral
    /// lecture, never penalise the request.
    /// </para>
    /// <para>
    /// What is refused is narrow: handing over the answers converts every retrieval into a reading,
    /// and the retrieval is the entire mechanism the streak is supposed to be protecting. So the
    /// counter-offer is the smallest help that keeps retrieval intact — the ladder — plus the thing
    /// the learner was really asking for, which is a shorter session.
    /// </para>
    /// <para>
    /// The ladder carries no text. Three rungs, each a closed code, ascending in support and
    /// stopping one rung short of disclosure by construction. There is no field on this shape that
    /// could hold a term, so no later change to hint generation can leak one through W7.
    /// </para>
    /// <para>
    /// There is no destination. Naming a screen here would imply the answers are visible on one,
    /// which is both false and exactly the wrong thing to imply.
    /// </para>
    /// </remarks>
    /// <param name="dueItemCount">How many items today's review holds.</param>
    /// <param name="shorterSessionItemCount">
    /// How many the shorter session would hold. Must be at least one and fewer than the full set:
    /// an "offer" of the same length is not an offer, and an offer of nothing is a skip wearing a
    /// different name.
    /// </param>
    /// <param name="asOfUtc">When those counts were true.</param>
    /// <param name="reviewDate">
    /// The learner-local day the review set belongs to. Required, because
    /// <see cref="CoachEvidenceCoverage.SingleDay"/> is a claim about a specific day and a coverage
    /// of "one day" with no day named is not checkable by the learner, the renderer, or a test. The
    /// first draft asserted SingleDay and carried no date at all.
    /// </param>
    public static CoachLimitationDto ReviewAnswerDisclosure(
        int dueItemCount,
        int shorterSessionItemCount,
        DateTime asOfUtc,
        DateOnly reviewDate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dueItemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(shorterSessionItemCount);

        return new CoachLimitationDto
        {
            Code = CoachLimitationCode.WouldRemoveLearningValue,

            // Today's review set, not the learner's whole vocabulary. SingleDay is the honest
            // coverage; CompleteOwnedSet would claim the refusal spoke for every word they own.
            // The day is stated as a degenerate window so the claim and the evidence for it travel
            // together — a SingleDay coverage whose bounds a renderer cannot show is a coverage the
            // learner has to take on trust.
            Coverage = CoachEvidenceCoverage.SingleDay,
            WindowStartDate = reviewDate,
            WindowEndDate = reviewDate,
            AsOfUtc = NormalizeAsOf(asOfUtc),

            // Absent rather than zero, for the same reason as S15: a rendered "0 items due" is a
            // fact, and "no count was made" is not the same fact.
            AffectedCount = dueItemCount > 0 ? dueItemCount : null,
            Destination = null,
            FullScopeSurface = null,
            Alternatives = [CoachAlternativeCode.UseHintLadder, CoachAlternativeCode.TakeAShorterSession],
            HintLadder = HintLadder,
            ShorterSession = BuildShorterSessionOffer(dueItemCount, shorterSessionItemCount)
        };
    }

    /// <summary>
    /// Builds the shorter-session offer, or withholds it when there is nothing honest to offer.
    /// </summary>
    /// <remarks>
    /// Returns null rather than a degenerate offer. A one-item review cannot be shortened into
    /// anything that is still a review, and an offer of zero items is a skip — which is the outcome
    /// S16 exists to give the learner an alternative to. When the caller's suggestion is out of
    /// range the offer is dropped; the ladder still stands on its own.
    /// </remarks>
    private static CoachShorterSessionOfferDto? BuildShorterSessionOffer(int fullCount, int suggested)
    {
        if (suggested < 1 || suggested >= fullCount)
        {
            return null;
        }

        // Always true here, and on the wire anyway so a reviewer reads the claim rather than
        // inferring it: every item in the shortened set is still a target-language retrieval
        // attempt. Shortening is the only lever pulled. Difficulty is untouched.
        return new CoachShorterSessionOfferDto(suggested, fullCount, PreservesRetrieval: true);
    }

    /// <summary>
    /// Whole-second UTC, matching <c>CoachResultScope.AsOfUtc</c>.
    /// </summary>
    /// <remarks>
    /// Truncated, never rounded — rounding up would place an "as of" claim in the future — and the
    /// same normalisation the read scopes use, so a limitation timestamp and an evidence timestamp
    /// from the same turn compare equal instead of differing in the seventh decimal.
    /// </remarks>
    private static DateTime NormalizeAsOf(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}
