using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// Translates the write ledger's internal vocabulary into the closed contract the learner's
/// client reads.
/// </summary>
/// <remarks>
/// <para>
/// The translation exists so the client never sees an internal identifier. A tool name is not
/// translatable, is not a stable public concept, and the contract privacy rules refuse a member
/// that names one at all; a closed <see cref="CoachWriteChangeKind"/> gives the card its heading,
/// its icon, and its screen-reader label in the learner's own language instead.
/// </para>
/// <para>
/// Every mapping fails towards the least capable answer. An unrecognised tool becomes
/// <see cref="CoachWriteChangeKind.Unknown"/> and renders neutral copy; an unrecognised risk class
/// or status becomes <c>Unknown</c> and renders no approval control at all. A client that cannot
/// tell what something is must not offer to approve it.
/// </para>
/// </remarks>
public static class CoachWriteProjection
{
    /// <summary>Maps a registered write tool onto the closed kind the client renders.</summary>
    public static CoachWriteChangeKind ChangeKind(string? toolName) => toolName switch
    {
        CoachToolNames.ProposeVocabularyEntry => CoachWriteChangeKind.VocabularyAdd,
        CoachToolNames.ProposeVocabularyEdit => CoachWriteChangeKind.VocabularyEdit,
        CoachToolNames.ProposeVocabularyLink => CoachWriteChangeKind.VocabularyLink,
        CoachToolNames.ProposeVocabularyRemoval => CoachWriteChangeKind.VocabularyRemove,
        CoachToolNames.ProposeSkillEntry => CoachWriteChangeKind.SkillAdd,
        CoachToolNames.ProposeSkillEdit => CoachWriteChangeKind.SkillEdit,
        CoachToolNames.ProposeSkillArchive => CoachWriteChangeKind.SkillArchive,
        CoachToolNames.ProposeResourceEntry => CoachWriteChangeKind.ResourceAdd,
        CoachToolNames.ProposeResourceEdit => CoachWriteChangeKind.ResourceEdit,
        CoachToolNames.ProposeResourceRemoval => CoachWriteChangeKind.ResourceRemove,
        CoachToolNames.ProposePreferenceChange => CoachWriteChangeKind.SettingChange,
        CoachToolNames.ProposeYouTubeImport => CoachWriteChangeKind.VideoImport,
        _ => CoachWriteChangeKind.Unknown
    };

    /// <summary>Maps the registered risk class. Read tools never produce a proposal.</summary>
    public static CoachWriteRiskClass RiskClass(CoachToolRiskClass riskClass) => riskClass switch
    {
        CoachToolRiskClass.WriteSoft => CoachWriteRiskClass.WriteSoft,
        CoachToolRiskClass.WriteHard => CoachWriteRiskClass.WriteHard,
        _ => CoachWriteRiskClass.Unknown
    };

    /// <summary>Maps the ledger status onto the client's state vocabulary.</summary>
    public static CoachWriteStatus Status(CoachWriteOperationStatus status) => status switch
    {
        CoachWriteOperationStatus.Proposed => CoachWriteStatus.Proposed,
        CoachWriteOperationStatus.Executing => CoachWriteStatus.Executing,
        CoachWriteOperationStatus.Executed => CoachWriteStatus.Executed,
        CoachWriteOperationStatus.Undone => CoachWriteStatus.Undone,
        CoachWriteOperationStatus.Rejected => CoachWriteStatus.Rejected,
        CoachWriteOperationStatus.Expired => CoachWriteStatus.Expired,
        CoachWriteOperationStatus.Failed => CoachWriteStatus.Failed,
        _ => CoachWriteStatus.Unknown
    };

    /// <summary>Maps the kind of row an operation touched.</summary>
    public static CoachWriteTargetKind TargetKind(CoachWriteEntityKind entityKind) => entityKind switch
    {
        CoachWriteEntityKind.VocabularyWord => CoachWriteTargetKind.VocabularyWord,
        CoachWriteEntityKind.SkillProfile => CoachWriteTargetKind.Skill,
        CoachWriteEntityKind.LearningResource => CoachWriteTargetKind.LearningResource,
        CoachWriteEntityKind.ResourceVocabularyLink => CoachWriteTargetKind.VocabularyLink,
        CoachWriteEntityKind.UserProfile => CoachWriteTargetKind.LearnerSetting,
        CoachWriteEntityKind.DailyPlan => CoachWriteTargetKind.DailyPlan,
        _ => CoachWriteTargetKind.None
    };
}
