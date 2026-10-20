using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.ActivityLog;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationController
{
    private ActivityLogCoordinator? _activityLog;

    internal bool IsActivityLogOpen => _activityLog?.IsOpen == true;
    internal int ActivityLogViewVersion => _activityLog?.ViewVersion ?? -1;
    internal ActivityLogState ActivityLogState => _activityLog?.State
        ?? new(ActivityLogPhase.Closed, [], null, null, false, 8);

    internal void OpenActivityLog()
    {
        if (_destination != NativeDestination.Dashboard || _selectedTab != 0
            || NavigationBlocked || _settingsOpen || IsQuizOpen
            || IsChooseOwnSelectorPresented
            || IsResourcesFormOpen || IsSkillsOpen || IsProfileOpen
            || IsVocabularyOpen || IsActivityLogOpen || _services is null)
            return;
        var expectedOwner = _authState is {
            Phase: NativePhase.Ready,
            Destination: NativeDestination.Dashboard,
            UserId: { Length: > 0 } authenticated
        } ? authenticated : null;
#if DEBUG
        if (expectedOwner is null && _preview
            && Foundation.NSBundle.MainBundle.BundleIdentifier
                == "com.simplyprofound.sentencestudio.native.fixture")
            expectedOwner = NativePreviewFixture.FromAssembly()?.Owner;
#endif
        var scope = _services.Provider.GetRequiredService<DeviceUserScopeProvider>();
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !scope.TryGetUserProfileId(out var activeOwner)
            || !string.Equals(expectedOwner, activeOwner, StringComparison.Ordinal))
        {
            _logger?.LogWarning(
                "Activity Log navigation refused an absent or untrusted owner.");
            return;
        }
        _activityLog ??= new ActivityLogCoordinator(
            new ActivityLogReader(
                _services.Provider.GetRequiredService<IServiceScopeFactory>(),
                _services.Provider.GetRequiredService<ILogger<ActivityLogReader>>()),
            scope, _services.Provider
                .GetRequiredService<ILogger<ActivityLogCoordinator>>());
        _activityLog.Changed -= Publish;
        _activityLog.Changed += Publish;
        Run(async () => await _activityLog.OpenAsync(
            expectedOwner, _lifetime.Token));
    }

    internal SwiftView RenderActivityLog(SourceUiStyle style) =>
        _activityLog is { IsOpen: true } log
            ? ActivityLogView.Page(log.State, style, Text,
                log.ToggleDay, () => _ = log.LoadMoreAsync(_lifetime.Token))
            : Views.Text(Text("Common_Loading"));

    internal void RefreshActivityLog()
    {
        if (_activityLog is { IsOpen: true } log)
            _ = log.RefreshAsync(_lifetime.Token);
    }

    internal void FilterActivityLog(ActivityCategory? filter)
    {
        if (_activityLog is { IsOpen: true } log)
            _ = log.FilterAsync(filter, _lifetime.Token);
    }

    internal void OnActivityLogPopped()
    {
        _activityLog?.Close();
        Publish();
    }
}
