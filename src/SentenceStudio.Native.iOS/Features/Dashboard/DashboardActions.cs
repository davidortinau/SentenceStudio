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
    Action<string>? OpenQuiz,
    Action<string>? OpenResource = null,
    Action? PreviewPlanVocabulary = null);

internal sealed record DashboardToolbarActions(
    Action Refresh,
    Action RegeneratePlan,
    // The practice log calendar (source NavMenu Activity), labelled History on Study.
    Action OpenHistory);
