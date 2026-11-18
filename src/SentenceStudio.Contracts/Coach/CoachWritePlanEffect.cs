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
    /// <remarks>
    /// A setting change counts too: the one setting Sam may change is the session length, and it
    /// revises today's plan in the same approval (and its undo puts the plan back). On a day the
    /// plan was not resized this reloads an unchanged plan, which is wasted work but never wrong.
    /// </remarks>
    public static bool ReplacedPlan(CoachWriteOperationDto? before, CoachWriteOperationDto? now) =>
        now is { ChangeKind: CoachWriteChangeKind.PlanRegenerate or CoachWriteChangeKind.SettingChange }
        && (now.Status == CoachWriteStatus.Executed && before?.Status != CoachWriteStatus.Executed
            || now.ChangeKind == CoachWriteChangeKind.SettingChange
                && now.Status == CoachWriteStatus.Undone && before?.Status != CoachWriteStatus.Undone);
}
