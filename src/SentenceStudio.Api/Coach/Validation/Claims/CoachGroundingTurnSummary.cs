using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// What the grounding layer did to one turn, in a shape that can outlive the request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> A learner report is filed minutes or hours after the turn it
/// concerns. The finding buffer is request-scoped and long gone by then, so a report that wants to
/// say "the honesty layer refused this answer" has to read it from something durable. The protected
/// turn outcome is that something.
/// </para>
/// <para>
/// <b>What crosses the durability boundary, and what does not.</b> Closed codes, booleans and
/// counts. No span index, no block index, no text, no language tag, no identifier of any kind.
/// </para>
/// <para>
/// The span indices are the omission worth defending. <c>CoachClaimFinding</c> carries them and
/// they are genuinely useful to a developer holding the answer — but an index into an answer is a
/// pointer at a sentence, and the whole guarantee of a durable grounding record is that it points
/// at nothing. A row that says "finding at block 2, span 1" plus a stored answer is a row that
/// reconstructs the offending sentence, and the two live in the same payload.
/// </para>
/// <para>
/// <b>Every member is bounded and validated on read.</b> This shape crosses a deserialization
/// boundary where a foreign or future payload can arrive, so nothing here is trusted because it
/// parsed — <see cref="IsWellFormed"/> is what a reader consults.
/// </para>
/// <para>
/// <b>Enums are string-serialized.</b> This is persisted state read back by later builds, and an
/// ordinal is coupled to declaration order: inserting a rule code would silently reinterpret every
/// stored summary. The same ruling W8 applied to the dispute.
/// </para>
/// </remarks>
/// <param name="RequestedStage">
/// The rung the deployment asked for, not a collapsed effective value. Recording the request rather
/// than the outcome is what lets a reader tell "Enforce refused it" from "Observe saw it and did
/// nothing", which are the same zero in every other field.
/// </param>
/// <param name="SubstitutionAllowed">
/// Whether repair by substitution was permitted for this turn. Separate from the stage on purpose:
/// Enforce with substitution withheld refuses, and must not read as Observe.
/// </param>
/// <param name="Refused">The answer was withheld entirely.</param>
/// <param name="Altered">At least one span was substituted.</param>
/// <param name="RepairSuppressedForLanguage">
/// Substitution was held back because the deterministic copy is not available in the learner's
/// display language. Visible rather than folded into <see cref="Altered"/>, because a turn that
/// could not be repaired for a Korean learner and one that needed no repair are different facts.
/// </param>
/// <param name="FindingCount">Total findings, bounded.</param>
/// <param name="RuleCounts">Per-rule counts. Closed codes, no duplicates.</param>
/// <param name="LimitationCode">The limitation the turn resolved to, when it resolved one.</param>
/// <param name="ShadowLabel">
/// The shadow router's label. Observability only — plan B5 forbids a rule from reading one, and
/// nothing does. Recorded so the label can be compared against outcomes offline, which is the only
/// way to learn whether the router would ever have been worth promoting.
/// </param>
public sealed record CoachGroundingTurnSummary(
    CoachGroundingStage RequestedStage,
    bool SubstitutionAllowed,
    bool Refused,
    bool Altered,
    bool RepairSuppressedForLanguage,
    int FindingCount,
    IReadOnlyList<CoachGroundingRuleCount> RuleCounts,
    CoachLimitationCode? LimitationCode,
    CoachShadowRouteLabel ShadowLabel)
{
    /// <summary>
    /// The largest finding count this shape will carry.
    /// </summary>
    /// <remarks>
    /// Generous relative to any real turn — an answer with more than this many findings is a
    /// malformed answer, not a chatty one — and present so a foreign payload cannot use the field
    /// as an unbounded integer channel or make a reader allocate on a stored number.
    /// </remarks>
    public const int MaxFindingCount = 512;

    /// <summary>
    /// True when every member is inside its bound and every code is one this build knows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Consulted by the reader before the summary is believed. System.Text.Json materialises any
    /// integer into an enum without throwing, so an undefined ordinal arrives silently and has to
    /// be caught by a census rather than by the deserializer — the same lesson the trace integrity
    /// check records.
    /// </para>
    /// <para>
    /// Duplicate rule codes are rejected rather than summed. A payload with two entries for one
    /// rule was not written by this build, and quietly merging them would let a reader report a
    /// count no writer produced.
    /// </para>
    /// </remarks>
    public bool IsWellFormed()
    {
        if (!Enum.IsDefined(RequestedStage) || !Enum.IsDefined(ShadowLabel))
        {
            return false;
        }

        if (LimitationCode is { } limitation && !Enum.IsDefined(limitation))
        {
            return false;
        }

        if (FindingCount < 0 || FindingCount > MaxFindingCount)
        {
            return false;
        }

        if (RuleCounts is null)
        {
            return false;
        }

        // One entry per rule at most, so the list cannot grow past the vocabulary it indexes.
        if (RuleCounts.Count > Enum.GetValues<CoachClaimRuleCode>().Length)
        {
            return false;
        }

        var seen = new HashSet<CoachClaimRuleCode>();

        foreach (var entry in RuleCounts)
        {
            if (entry is null
                || !Enum.IsDefined(entry.Rule)
                || entry.Count < 1
                || entry.Count > MaxFindingCount
                || !seen.Add(entry.Rule))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One rule and how many times it fired in a turn.</summary>
/// <remarks>
/// A pair rather than a dictionary. A dictionary keyed by enum serializes its keys as strings and
/// reads back tolerantly enough to admit a key this build cannot name; a list of typed pairs makes
/// an unknown code a value the census can reject.
/// </remarks>
/// <param name="Rule">The rule that fired.</param>
/// <param name="Count">How many findings it produced. At least one, or the entry is noise.</param>
public sealed record CoachGroundingRuleCount(CoachClaimRuleCode Rule, int Count);
