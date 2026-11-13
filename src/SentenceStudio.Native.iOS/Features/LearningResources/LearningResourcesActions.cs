namespace SentenceStudio.Native.iOS.Features.LearningResources;

/// <summary>
/// The Resources page's actions. The scene updates a mounted page in place, so the toolbar's
/// actions read the state when tapped.
/// </summary>
internal sealed record LearningResourcesActions(
    Action<string> Search,
    Action<string?> FilterType,
    Action<string?> FilterLanguage,
    Action ToggleViewMode,
    Action<string> Open,
    Action Add,
    Action CreateStarter,
    Action Reload,
    Action<string> Sort,
    Action OpenFilters,
    Action ToggleFilterPanel,
    Action ClearFilters,
    Action ClearSearchAndFilters,
    Action<double>? PageWidthChanged = null,
    Action<IReadOnlyList<string>>? KeepFilterTokens = null);

/// <summary>The phone filter sheet's actions: its choices wait for Apply.</summary>
internal sealed record LearningResourceFilterActions(
    Action<string?> ChangeType,
    Action<string?> ChangeLanguage,
    Action<string> Sort,
    Action Apply,
    Action Cancel,
    Action Clear);
