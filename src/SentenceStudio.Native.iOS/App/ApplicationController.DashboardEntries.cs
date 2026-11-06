using CoreFoundation;
using Foundation;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Dashboard progress published mid-load. The source sidebar items (NavMenu.razor)
/// are the tabs and the Library switcher (ApplicationController.Library.cs).
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
}
