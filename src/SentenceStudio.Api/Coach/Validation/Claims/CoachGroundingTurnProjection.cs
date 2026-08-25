using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// The one place a judged turn becomes a durable grounding summary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Total, and total on purpose.</b> Every member of <see cref="CoachGroundingTurnSummary"/> is
/// filled from the record — none is defaulted, none is left for a caller to remember. A projection
/// with an optional field is a projection whose output depends on which call site built it, and the
/// report columns downstream cannot tell "the turn did not refuse" from "this caller forgot to say".
/// </para>
/// <para>
/// <b>One-way.</b> Findings carry block and span indices; the summary does not. An index into an
/// answer is a pointer at a sentence, and the stored answer sits in the same payload — so this is
/// the boundary where the pointer is dropped, and it is dropped by construction rather than by a
/// caller choosing not to pass it.
/// </para>
/// <para>
/// <b>Deterministic ordering.</b> Rule counts come out sorted by code. The record's dictionary has
/// no defined order, and an unordered list would make two identical turns serialize to different
/// bytes — which turns a stored-payload comparison into a coin flip and makes a diff of two reports
/// unreadable.
/// </para>
/// </remarks>
public static class CoachGroundingTurnProjection
{
    /// <summary>
    /// Projects a judged turn, or null when the ladder did not run.
    /// </summary>
    /// <remarks>
    /// Null for an Off deployment, which never produces a record. The contract permits the section
    /// to be absent in that case, and writing an all-zero summary instead would be worse than
    /// absent: it reads as "the layer looked and found nothing".
    /// </remarks>
    public static CoachGroundingTurnSummary? Project(CoachClaimTurnRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        return new CoachGroundingTurnSummary(
            record.Stage,

            // The two axes, kept apart all the way into storage. Suppression is the reason
            // substitution did not run; it is not a claim that the rung was lower than it was.
            SubstitutionAllowed: !record.RepairSuppressedForLanguage,
            Refused: record.Refused,
            Altered: record.AnswerAltered,
            RepairSuppressedForLanguage: record.RepairSuppressedForLanguage,

            FindingCount: Math.Min(record.Findings.Count, CoachGroundingTurnSummary.MaxFindingCount),
            RuleCounts: RuleCounts(record),

            // The typed limitation the turn resolved to, reduced to its code. The DTO carries
            // counts and a destination; neither belongs in a record that outlives the turn.
            LimitationCode: record.Limitation?.Code,

            ShadowLabel: record.ShadowLabel);
    }

    /// <summary>
    /// Unique rule counts, ordered by code, bounded per entry.
    /// </summary>
    /// <remarks>
    /// Entries at or below zero are dropped rather than stored. The census on the reading side
    /// rejects them, so emitting one would produce a summary this build writes and refuses to read
    /// — the worst kind of asymmetry, because it only shows up after the row is already durable.
    /// </remarks>
    private static IReadOnlyList<CoachGroundingRuleCount> RuleCounts(CoachClaimTurnRecord record) =>
        [.. record.CountsByRule
            .Where(pair => pair.Value > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => new CoachGroundingRuleCount(
                pair.Key,
                Math.Min(pair.Value, CoachGroundingTurnSummary.MaxFindingCount)))];
}
