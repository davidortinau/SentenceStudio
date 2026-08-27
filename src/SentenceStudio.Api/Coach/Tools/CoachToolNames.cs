namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// The names of all coach tools. The set is closed at compile time.
/// Validation uses <see cref="ICoachToolRegistry"/> for the enabled subset;
/// these constants remain for compile-time references and tests.
/// </summary>
public static class CoachToolNames
{
    // --- Core 5 (always enabled when coach is on) ---
    public const string GetLearnerProfileSummary = "get_learner_profile_summary";
    public const string GetPracticeBalance = "get_practice_balance";
    public const string GetVocabularyDueSummary = "get_vocabulary_due_summary";
    public const string GetResourceCatalog = "get_resource_catalog";
    public const string PreviewPracticePlan = "preview_practice_plan";
    public const string GetPracticeHistorySummary = "get_practice_history_summary";

    // --- Sam read tools (require SamOverlay + SamReadTools) ---
    public const string ListUserVocabularies = "list_user_vocabularies";
    public const string GetVocabularyWordDetail = "get_vocabulary_word_detail";
    public const string GetSkillList = "get_skill_list";
    public const string GetSkillDetail = "get_skill_detail";
    public const string GetLearningResourceList = "get_learning_resource_list";
    public const string GetLearningResourceDetail = "get_learning_resource_detail";
    public const string GetCurrentProfileSummary = "get_current_profile_summary";
    public const string GetLearnerSettingsSummary = "get_learner_settings_summary";
    public const string GetCurrentPlanSummary = "get_current_plan_summary";

    // --- Sam write tools (require SamOverlay + SamReadTools + SamWriteTools) ---
    // Every name starts with propose_, and every one of them produces a proposal the learner
    // has to approve. None of them writes on its own, which is why a name like
    // propose_vocabulary_removal is not the destructive-sounding name it looks like.
    public const string ProposeVocabularyEntry = "propose_vocabulary_entry";
    public const string ProposeVocabularyEdit = "propose_vocabulary_edit";
    public const string ProposeVocabularyLink = "propose_vocabulary_link";
    public const string ProposeVocabularyRemoval = "propose_vocabulary_removal";
    public const string ProposeSkillEntry = "propose_skill_entry";
    public const string ProposeSkillEdit = "propose_skill_edit";
    public const string ProposeSkillArchive = "propose_skill_archive";
    public const string ProposeResourceEntry = "propose_resource_entry";
    public const string ProposeResourceEdit = "propose_resource_edit";
    public const string ProposeResourceRemoval = "propose_resource_removal";
    public const string ProposePreferenceChange = "propose_preference_change";
    public const string ProposeYouTubeImport = "propose_youtube_import";

    /// <summary>The prefix every write-intent tool name carries.</summary>
    /// <remarks>
    /// Load-bearing, not cosmetic. The allow-list refuses write-sounding verbs outright; this
    /// prefix, combined with a registered write risk class, is the only thing that lifts that
    /// refusal, so a tool cannot smuggle in a mutation under a read-shaped name.
    /// </remarks>
    public const string ProposePrefix = "propose_";

    /// <summary>
    /// The stored stand-in for a tool name the frozen registry does not contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A server constant, and the reason the turn trace's one string member cannot become a
    /// channel. The trace's whole claim is that it holds no free text; the tool name is exempt
    /// only because it comes from a build-time registration. A name that is not in
    /// <see cref="ICoachToolRegistry.All"/> has, by definition, not come from one — so it is
    /// replaced with this before anything is serialized, and the raw input never reaches the
    /// protected column.
    /// </para>
    /// <para>
    /// <b>Collapse, not refusal.</b> The entry and its ordinal are kept. Dropping the call would
    /// renumber the turn and quietly shorten the record of what happened, which is a worse lie
    /// than "a call was made and this build cannot name it".
    /// </para>
    /// <para>
    /// Deliberately absent from <see cref="All"/>, <see cref="AllRegistered"/> and
    /// <see cref="AllWrite"/>: it is not a tool, nothing may register it, and it must never be
    /// callable. It also does not carry <see cref="ProposePrefix"/>, so a collapsed name can never
    /// read as a write-intent tool.
    /// </para>
    /// </remarks>
    public const string Unregistered = "unregistered_tool";

    /// <summary>The original 5 core tool names. Kept for backward compatibility.</summary>
    public static IReadOnlyList<string> CoreFive { get; } =
    [
        GetLearnerProfileSummary,
        GetPracticeBalance,
        GetVocabularyDueSummary,
        GetResourceCatalog,
        PreviewPracticePlan
    ];

    /// <summary>
    /// The five core tool names — the original closed set.
    /// Identical to <see cref="CoreFive"/>. Kept for backward compatibility
    /// with existing tests and validation that reference <c>CoachToolNames.All</c>.
    /// New code should use <see cref="ICoachToolRegistry.EnabledNames"/>.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        GetLearnerProfileSummary,
        GetPracticeBalance,
        GetVocabularyDueSummary,
        GetResourceCatalog,
        PreviewPracticePlan,
        GetPracticeHistorySummary
    ];

    /// <summary>Every known tool name including Sam read tools, in registration order.</summary>
    public static IReadOnlyList<string> AllRegistered { get; } =
    [
        GetLearnerProfileSummary,
        GetPracticeBalance,
        GetVocabularyDueSummary,
        GetResourceCatalog,
        PreviewPracticePlan,
        GetPracticeHistorySummary,
        ListUserVocabularies,
        GetVocabularyWordDetail,
        GetSkillList,
        GetSkillDetail,
        GetLearningResourceList,
        GetLearningResourceDetail,
        GetCurrentProfileSummary,
        GetLearnerSettingsSummary,
        GetCurrentPlanSummary,
        ProposeVocabularyEntry,
        ProposeVocabularyEdit,
        ProposeVocabularyLink,
        ProposeVocabularyRemoval,
        ProposeSkillEntry,
        ProposeSkillEdit,
        ProposeSkillArchive,
        ProposeResourceEntry,
        ProposeResourceEdit,
        ProposeResourceRemoval,
        ProposePreferenceChange,
        ProposeYouTubeImport
    ];

    /// <summary>Every write-intent tool name, in registration order.</summary>
    public static IReadOnlyList<string> AllWrite { get; } =
    [
        ProposeVocabularyEntry,
        ProposeVocabularyEdit,
        ProposeVocabularyLink,
        ProposeVocabularyRemoval,
        ProposeSkillEntry,
        ProposeSkillEdit,
        ProposeSkillArchive,
        ProposeResourceEntry,
        ProposeResourceEdit,
        ProposeResourceRemoval,
        ProposePreferenceChange,
        ProposeYouTubeImport
    ];
}
