namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal sealed record DashboardActions(
    Action Generate,
    Action CreateStarter,
    Action ToggleInsight,
    Action<NativeDashboardMode> SelectMode,
    Action OpenResourceSelector,
    Action OpenSkillSelector,
    Action RetryChooseOwn,
    Action<bool> ExpandActivities,
    Action<string>? LaunchChooseOwnActivity,
    Action AddResource,
    Action<string>? OpenQuiz);

internal sealed record DashboardToolbarActions(
    Action Refresh,
    Action RegeneratePlan,
    Action OpenSettings,
    Action OpenProfile,
    Action OpenSkills,
    Action OpenVocabulary,
    Action OpenActivityLog,
    Action SignOut);
