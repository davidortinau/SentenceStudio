using CoreFoundation;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Dashboard progress published mid-load, and the Dashboard menu entries that
/// stand in for source sidebar items (NavMenu.razor).
/// </summary>
internal sealed partial class ApplicationController
{
    /// <summary>
    /// Only mid-load states are applied here (source isLoading / isPlanLoading):
    /// the vocabulary and number statistics publish before the plan finishes.
    /// Every other state still arrives with the awaited presenter result.
    /// </summary>
    private void OnDashboardProgress(NativeDashboardState state)
    {
        if (_disposed || state.Phase != NativePhase.Loading)
            return;
        if (NSThread.IsMain)
            ApplyDashboardProgress();
        else
            DispatchQueue.MainQueue.DispatchAsync(ApplyDashboardProgress);
    }

    private void ApplyDashboardProgress()
    {
        // Re-read on the main thread: a final state that already arrived wins.
        if (_disposed || _dashboard?.State is not { Phase: NativePhase.Loading } current)
            return;
        _dashboardState = current;
        Publish();
    }

    /// <summary>
    /// Source sidebar Minimal Pairs (NavMenu.razor:49): opens the Minimal Pairs
    /// activity with no resource or skill, whatever the Dashboard mode.
    /// </summary>
    internal void OpenMinimalPairs()
    {
        if (_destination != NativeDestination.Dashboard || _selectedTab != 0
            || NavigationBlocked || _settingsOpen || IsQuizOpen || IsActivityOpen
            || IsChooseOwnSelectorPresented || IsResourcesFormOpen || IsSkillsOpen
            || IsProfileOpen || IsVocabularyOpen || IsActivityLogOpen
            || _services is null || _dashboard is null)
            return;
        Run(async () =>
        {
            var selection = _dashboard.SelectNavigationEntry();
            if (selection is null)
            {
                _logger?.LogWarning("Minimal Pairs navigation refused an absent owner.");
                return;
            }
            var launch = await _services.Provider
                .GetRequiredService<NativeActivityLaunchService>()
                .FromChooseOwnAsync(selection, NativeActivityCatalog.MinimalPairs,
                    _lifetime.Token);
            if (launch is null)
            {
                _logger?.LogWarning("Minimal Pairs navigation launch was refused.");
                return;
            }
            StartActivity(launch);
        });
    }
}
