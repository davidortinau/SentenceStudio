namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// Whether a change Sam carried out replaced today's plan. One rule for every client, so each
/// refreshes the plan it shows at the same moment.
/// </summary>
public static class CoachWritePlanEffect
{
    /// <summary>
    /// True when <paramref name="now"/> says a plan regeneration is in place and
    /// <paramref name="before"/>, the state the client held for the same operation, did not.
    /// </summary>
    /// <remarks>
    /// The transition, not the state: a card that is refreshed later still reads Executed, and
    /// reloading the plan each time would be wasted work on a page the learner is using.
    /// </remarks>
    public static bool ReplacedPlan(CoachWriteOperationDto? before, CoachWriteOperationDto? now) =>
        now is { ChangeKind: CoachWriteChangeKind.PlanRegenerate, Status: CoachWriteStatus.Executed }
        && before?.Status != CoachWriteStatus.Executed;
}
