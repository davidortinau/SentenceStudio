using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// What the grounding layer did on one turn. Content-free.
/// </summary>
/// <param name="Stage">The rung the turn ran at.</param>
/// <param name="Findings">Every finding, with the action taken. Codes and indices only.</param>
/// <param name="Refused">True when the answer was withheld entirely.</param>
/// <param name="AnswerAltered">True when at least one span was substituted.</param>
/// <param name="ShadowLabel">
/// The optional router's guess. Recorded, never read by a rule. Plan D4 and B5.
/// </param>
/// <param name="Limitation">
/// The typed boundary a capability finding resolves to, when one is known. Never prose.
/// </param>
/// <param name="RepairSuppressedForLanguage">
/// True when the rung permitted substitution and it was held back because the repair copy is
/// English and the answer is not. The findings are complete; only the rewrite was withheld. An
/// operator watching a Repair rollout needs to be able to tell "nothing needed fixing" from
/// "everything needed fixing and none of it could be said in the learner's language".
/// </param>
public sealed record CoachClaimTurnRecord(
    CoachGroundingStage Stage,
    IReadOnlyList<CoachClaimFinding> Findings,
    bool Refused,
    bool AnswerAltered,
    CoachShadowRouteLabel ShadowLabel,
    CoachLimitationDto? Limitation,
    bool RepairSuppressedForLanguage = false)
{
    /// <summary>True when at least one rule fired.</summary>
    public bool HasFindings => Findings.Count > 0;

    /// <summary>Finding codes and their counts. Safe to log without a second review.</summary>
    public IReadOnlyDictionary<CoachClaimRuleCode, int> CountsByRule =>
        Findings.GroupBy(finding => finding.Rule)
            .ToDictionary(group => group.Key, group => group.Count());
}

/// <summary>
/// Holds the grounding record for the current turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <see cref="CoachGroundingStage.Observe"/> is defined as "evaluate
/// and record, change nothing". Without somewhere to record, Observe is indistinguishable from
/// <see cref="CoachGroundingStage.Off"/> — including to a test, which is how a call site that was
/// registered but never invoked survived review in the first place.
/// </para>
/// <para>
/// <b>In memory, and deliberately so.</b> Plan W9 owns the nullable, content-free report columns
/// and the operator filters; that is the persistence and soak surface, and it is the workstream
/// that also owns the metrics those columns feed. Writing a claim summary into the protected turn
/// outcome now would add a member to a payload W4 has just been hardened to read tolerantly across
/// a rollback, for a consumer that does not exist yet. This buffer is the seam W9 reads from.
/// </para>
/// <para>
/// Scoped to the request, like the tool observation buffer, so "the turn" is the scope and there is
/// no cross-turn state to leak or clear.
/// </para>
/// </remarks>
public interface ICoachClaimFindingBuffer
{
    /// <summary>The record for this turn, or null when the grounding layer did not run.</summary>
    CoachClaimTurnRecord? Record { get; }

    /// <summary>Stores the turn's record. The last call wins.</summary>
    void Capture(CoachClaimTurnRecord record);
}

/// <summary>The shipped buffer. A field with a contract.</summary>
public sealed class CoachClaimFindingBuffer : ICoachClaimFindingBuffer
{
    /// <inheritdoc />
    public CoachClaimTurnRecord? Record { get; private set; }

    /// <inheritdoc />
    public void Capture(CoachClaimTurnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
    }
}
