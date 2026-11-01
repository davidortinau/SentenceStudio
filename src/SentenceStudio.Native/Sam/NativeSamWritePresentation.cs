using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Native;

/// <summary>What a proposed-change card is showing. Zero is Unreadable: nothing computed offers no controls.</summary>
public enum NativeSamWriteStage
{
    Unreadable = 0,
    Proposed,
    ConfirmationRequired,
    Superseded,
    Expired,
    Unavailable,
    InDoubt,
    Applied,
    Undone,
    Declined,
    Failed,
}

/// <summary>
/// Turns one proposal's authoritative server state into the stage the card renders
/// and the controls it may offer. A port of the source SamWritePresentation
/// (SentenceStudio.UI Shared/Sam, frozen reference 59c72262); every rule fails
/// towards showing less, and the clock is only ever used to take an Undo away.
/// </summary>
public static class NativeSamWritePresentation
{
    public static NativeSamWriteStage Stage(CoachWriteOperationDto? operation, bool isActionable,
        bool isConfirming, DateTime utcNow, bool isUnavailable = false)
    {
        if (operation is null || !IsWellFormed(operation))
            return NativeSamWriteStage.Unreadable;
        if (isUnavailable)
            return operation.Status switch
            {
                CoachWriteStatus.Executed => NativeSamWriteStage.Applied,
                CoachWriteStatus.Undone => NativeSamWriteStage.Undone,
                CoachWriteStatus.Rejected => NativeSamWriteStage.Declined,
                CoachWriteStatus.Expired => NativeSamWriteStage.Expired,
                CoachWriteStatus.Failed => NativeSamWriteStage.Failed,
                _ => NativeSamWriteStage.Unavailable,
            };
        return operation.Status switch
        {
            CoachWriteStatus.Executed => NativeSamWriteStage.Applied,
            CoachWriteStatus.Undone => NativeSamWriteStage.Undone,
            CoachWriteStatus.Rejected => NativeSamWriteStage.Declined,
            CoachWriteStatus.Expired => NativeSamWriteStage.Expired,
            CoachWriteStatus.Failed => NativeSamWriteStage.Failed,
            CoachWriteStatus.Executing => NativeSamWriteStage.InDoubt,
            CoachWriteStatus.Proposed when operation.ExpiresAtUtc <= utcNow => NativeSamWriteStage.Expired,
            CoachWriteStatus.Proposed when !isActionable => NativeSamWriteStage.Superseded,
            CoachWriteStatus.Proposed when isConfirming => NativeSamWriteStage.ConfirmationRequired,
            CoachWriteStatus.Proposed => NativeSamWriteStage.Proposed,
            _ => NativeSamWriteStage.Unreadable,
        };
    }

    /// <summary>The same coherence rule the presenter applies before it will act.</summary>
    public static bool IsWellFormed(CoachWriteOperationDto? operation) =>
        operation is not null
        && operation.OperationId.Length > 0
        && operation.Status != CoachWriteStatus.Unknown
        && operation.RiskClass != CoachWriteRiskClass.Unknown
        && (operation.RiskClass == CoachWriteRiskClass.WriteHard) == operation.RequiresConfirmation
        && string.Equals(operation.ApprovalMode,
            operation.RequiresConfirmation ? "confirm" : "accept", StringComparison.Ordinal);

    public static string HeadingKey(CoachWriteChangeKind kind) => kind switch
    {
        CoachWriteChangeKind.VocabularyAdd => "Coach_WriteKindVocabularyAdd",
        CoachWriteChangeKind.VocabularyEdit => "Coach_WriteKindVocabularyEdit",
        CoachWriteChangeKind.VocabularyLink => "Coach_WriteKindVocabularyLink",
        CoachWriteChangeKind.VocabularyRemove => "Coach_WriteKindVocabularyRemove",
        CoachWriteChangeKind.SkillAdd => "Coach_WriteKindSkillAdd",
        CoachWriteChangeKind.SkillEdit => "Coach_WriteKindSkillEdit",
        CoachWriteChangeKind.SkillArchive => "Coach_WriteKindSkillArchive",
        CoachWriteChangeKind.ResourceAdd => "Coach_WriteKindResourceAdd",
        CoachWriteChangeKind.ResourceEdit => "Coach_WriteKindResourceEdit",
        CoachWriteChangeKind.ResourceRemove => "Coach_WriteKindResourceRemove",
        CoachWriteChangeKind.SettingChange => "Coach_WriteKindSettingChange",
        CoachWriteChangeKind.VideoImport => "Coach_WriteKindVideoImport",
        _ => "Coach_WriteKindUnknown",
    };

    public static string StateKey(NativeSamWriteStage stage) => stage switch
    {
        NativeSamWriteStage.Proposed => "Coach_WriteStateProposed",
        NativeSamWriteStage.ConfirmationRequired => "Coach_WriteStateProtected",
        NativeSamWriteStage.Applied => "Coach_WriteStateApplied",
        NativeSamWriteStage.Undone => "Coach_WriteStateUndone",
        NativeSamWriteStage.Declined => "Coach_WriteStateDeclined",
        NativeSamWriteStage.Expired or NativeSamWriteStage.Superseded => "Coach_WriteStateExpired",
        NativeSamWriteStage.Unavailable => "Coach_WriteStateUnavailable",
        NativeSamWriteStage.InDoubt => "Coach_WriteStateInDoubt",
        NativeSamWriteStage.Failed => "Coach_WriteStateFailed",
        _ => "Coach_WriteStateUnreadable",
    };

    /// <summary>The stage's hint line, when the source shows one.</summary>
    public static string? HintKey(NativeSamWriteStage stage) => stage switch
    {
        NativeSamWriteStage.Proposed => "Coach_WriteNothingChangedYet",
        NativeSamWriteStage.InDoubt => "Coach_WriteInDoubtHint",
        NativeSamWriteStage.Expired => "Coach_WriteExpiredHint",
        NativeSamWriteStage.Superseded => "Coach_WriteSupersededHint",
        NativeSamWriteStage.Unreadable => "Coach_WriteUnreadableHint",
        _ => null,
    };

    public static bool ShowsAccept(CoachWriteOperationDto operation, NativeSamWriteStage stage) =>
        stage == NativeSamWriteStage.Proposed && !operation.RequiresConfirmation;

    public static bool ShowsReview(CoachWriteOperationDto operation, NativeSamWriteStage stage) =>
        stage == NativeSamWriteStage.Proposed && operation.RequiresConfirmation;

    public static bool ShowsDecline(NativeSamWriteStage stage) =>
        stage is NativeSamWriteStage.Proposed or NativeSamWriteStage.ConfirmationRequired;

    public static bool ShowsUndo(CoachWriteOperationDto operation, NativeSamWriteStage stage, DateTime utcNow,
        bool isUnavailable = false) =>
        !isUnavailable
        && stage == NativeSamWriteStage.Applied
        && operation.Receipt is { CanUndo: true, UndoExpiresAtUtc: { } closesAt }
        && closesAt > utcNow;

    public static bool ShowsRefresh(NativeSamWriteStage stage) => stage == NativeSamWriteStage.InDoubt;

    public static bool ShowsIrreversibleWarning(CoachWriteOperationDto operation, NativeSamWriteStage stage) =>
        stage is NativeSamWriteStage.Proposed or NativeSamWriteStage.ConfirmationRequired
        && !operation.IsReversible;

    public static bool ShowsReversibleNote(CoachWriteOperationDto operation, NativeSamWriteStage stage) =>
        stage is NativeSamWriteStage.Proposed or NativeSamWriteStage.ConfirmationRequired
        && operation.IsReversible;

    /// <summary>A completed change describes itself from its receipt; a proposal from its preview.</summary>
    public static string? Summary(CoachWriteOperationDto operation) =>
        operation.Receipt is { Summary.Length: > 0 } receipt ? receipt.Summary : operation.Summary;

    public static IReadOnlyList<string> Lines(CoachWriteOperationDto operation) =>
        operation.Receipt is { Lines.Count: > 0 } receipt ? receipt.Lines : operation.Lines;
}
