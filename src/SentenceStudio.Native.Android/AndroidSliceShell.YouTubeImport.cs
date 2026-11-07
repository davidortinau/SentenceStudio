using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android;

/// <summary>
/// YouTube import (source /media-import, NavMenu "Media Import") as a More screen over the
/// Dashboard tab, on the shared owner-gated <see cref="YouTubeImportCoordinator"/> and
/// <see cref="YouTubeChannelCoordinator"/> that iOS uses (iOS departures 36-45): My Channels,
/// Single Video and Import History, and the channel page (source /media-import/channel/{id?})
/// above it. Back waits while a video or a channel is being saved.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private YouTubeImportCoordinator? _youTubeImport;
    private YouTubeChannelCoordinator? _youTubeChannel;
    /// <summary>The channel page's own snackbar, so its toast can be handed to the import page after.</summary>
    private NativeToastScheduler? _channelToasts;

    private bool IsYouTubeChannelOpen => _youTubeImport is not null && _youTubeChannel is not null;

    private bool IsYouTubeNavigationBlocked => _youTubeImport?.IsNavigationBlocked == true
        || _youTubeChannel?.IsNavigationBlocked == true;

    private void OpenYouTubeImport() => OpenMoreScreen(MoreScreen.YouTubeImport);

    private void OpenYouTubeImportCoordinator(IServiceProvider provider, DeviceUserScopeProvider scope,
        string? owner)
    {
        if (owner is null)
            return;
        var coordinator = new YouTubeImportCoordinator(
            provider.GetRequiredService<INativeYouTubeImport>(), scope,
            provider.GetRequiredService<ILogger<YouTubeImportCoordinator>>());
        coordinator.Changed += OnMoreChanged;
        coordinator.ResourcesWritten += OnImportResourcesWritten;
        _youTubeImport = coordinator;
        RunMoreOnUi(() => OpenYouTubeImportAsync(coordinator, owner));
    }

    private async Task OpenYouTubeImportAsync(YouTubeImportCoordinator coordinator, string owner)
    {
        try
        {
            if (await coordinator.OpenAsync(owner) || !ReferenceEquals(_youTubeImport, coordinator))
                return;
            global::Android.Util.Log.Warn("SentenceStudio.Native", "YouTube import open was refused for the owner.");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("SentenceStudio.Native", $"YouTube import open failed: {ex}");
        }
        // A page that opened and then lost its owner shows its blocked state; one that never
        // opened says the profile was not found.
        if (ReferenceEquals(_youTubeImport, coordinator) && !coordinator.IsOpen)
            ReleaseYouTubeImport();
        OnMoreChanged();
    }

    /// <summary>A coordinator call from a tap (UI thread); failures show as the shell's error.</summary>
    private void YouTubeCommand(Func<YouTubeImportCoordinator, Task> command)
    {
        if (_youTubeImport is { } import)
            _ = GuardMoreAsync(() => command(import));
    }

    private void YouTubeEdit(Action<YouTubeImportCoordinator> edit)
    {
        if (_youTubeImport is not { } import)
            return;
        edit(import);
        RenderMoreNow();
    }

    private void ChannelCommand(Func<YouTubeChannelCoordinator, Task> command)
    {
        if (_youTubeChannel is { } channel)
            _ = GuardMoreAsync(() => command(channel));
    }

    private void ChannelEdit(Action<YouTubeChannelCoordinator> edit)
    {
        if (_youTubeChannel is not { } channel)
            return;
        edit(channel);
        RenderMoreNow();
    }

    /// <summary>Back on YouTube import, innermost first: a menu, the channel page.</summary>
    private bool HandleYouTubeBack()
    {
        if (_importMenu is not null)
        {
            CloseImportMenu();
            return true;
        }
        if (IsYouTubeNavigationBlocked)
            return true;
        if (IsYouTubeChannelOpen)
        {
            CancelYouTubeChannel();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Source AddChannel and EditChannel: the channel page above the YouTube import page, for the
    /// verified owner (an absent or untrusted owner opens nothing).
    /// </summary>
    private void OpenYouTubeChannel(string? channelId)
    {
        if (_more != MoreScreen.YouTubeImport || _youTubeImport is not { IsOpen: true }
            || IsYouTubeChannelOpen || IsYouTubeNavigationBlocked || _scope is null)
            return;
        if (MoreOwner() is not { } owner)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                "YouTube channel navigation refused an absent or untrusted owner.");
            return;
        }
        var provider = _scope.ServiceProvider;
        var coordinator = new YouTubeChannelCoordinator(
            provider.GetRequiredService<INativeYouTubeImport>(),
            provider.GetRequiredService<DeviceUserScopeProvider>(),
            provider.GetRequiredService<ILogger<YouTubeChannelCoordinator>>());
        coordinator.Changed += OnMoreChanged;
        coordinator.Finished += OnYouTubeChannelFinished;
        _youTubeChannel = coordinator;
        _importMenu = null;
        RunMoreOnUi(() => OpenYouTubeChannelAsync(coordinator, owner, channelId));
        OnMoreChanged();
    }

    private async Task OpenYouTubeChannelAsync(YouTubeChannelCoordinator coordinator, string owner,
        string? channelId)
    {
        try
        {
            if (await coordinator.OpenAsync(owner, channelId) || !ReferenceEquals(_youTubeChannel, coordinator))
                return;
            global::Android.Util.Log.Warn("SentenceStudio.Native", "YouTube channel page open was refused for the owner.");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("SentenceStudio.Native", $"YouTube channel page open failed: {ex}");
        }
        if (ReferenceEquals(_youTubeChannel, coordinator) && !coordinator.IsOpen)
            ReleaseYouTubeChannel();
        OnMoreChanged();
    }

    /// <summary>
    /// Source SaveChannel's navigation: a saved channel returns to the page (its toast shows
    /// there and the channels and history are read again); an unresolved new channel opens its
    /// edit page instead.
    /// </summary>
    private void OnYouTubeChannelFinished(YouTubeChannelOutcome outcome, string? channelId) => Post(() =>
    {
        if (_youTubeChannel is not { } channel)
            return;
        var toast = NativeMessages.LatestToast(channel.Messages);
        var banners = NativeMessages.Banners(channel.Messages);
        ReleaseYouTubeChannel();
        if (outcome == YouTubeChannelOutcome.EditSaved && !string.IsNullOrEmpty(channelId))
        {
            OpenYouTubeChannel(channelId);
            if (_youTubeChannel is { } edit)
            {
                edit.ShowMessages(banners);
                return;
            }
            // The edit page could not open: its "saved but unresolved" banner shows on the import page.
            foreach (var banner in banners)
                _youTubeImport?.ShowMessage(banner);
        }
        if (_youTubeImport is { } import)
        {
            if (toast is not null)
                import.ShowMessage(toast);
            _ = GuardMoreAsync(import.ReloadAfterChannelPageAsync);
        }
        OnMoreChanged();
    });

    /// <summary>Source Cancel and Back: the channel page closes without saving; the lists load again.</summary>
    private void CancelYouTubeChannel()
    {
        if (_youTubeChannel is null || IsYouTubeNavigationBlocked)
            return;
        ReleaseYouTubeChannel();
        if (_youTubeImport is { } import)
            _ = GuardMoreAsync(import.ReloadAfterChannelPageAsync);
        OnMoreChanged();
    }

    /// <summary>
    /// Source ViewSavedResource and the history's completed rows: the resource's editor; closing
    /// it returns to the page.
    /// </summary>
    private void ViewYouTubeResource(string resourceId) => Publish(() =>
    {
        if (!IsDashboardTab || Busy.Value || _more != MoreScreen.YouTubeImport || IsYouTubeChannelOpen
            || string.IsNullOrWhiteSpace(resourceId)
            || EnsureResources() is not { } resources || resources.IsFormOpen)
            return;
        _resourceFormFromDashboard = true;
        _resourceFormReturn = Destination;
        Destination = NativeDestination.Resources;
        resources.OnBrowserAppeared();
        resources.Open(resourceId);
    });

    private void ReleaseYouTubeChannel()
    {
        if (_youTubeChannel is not { } channel)
            return;
        _youTubeChannel = null;
        _channelToasts?.Reset();
        channel.Changed -= OnMoreChanged;
        channel.Finished -= OnYouTubeChannelFinished;
        channel.Dispose();
    }

    /// <summary>The scheduler for the More screen's toast: the channel page's own while it is open.</summary>
    private NativeToastScheduler MoreToasts
    {
        get
        {
            if (_youTubeChannel is null)
                return ImportToasts;
            if (_channelToasts is null)
            {
                _channelToasts = new NativeToastScheduler();
                _channelToasts.Changed += () => Publish(() => { });
            }
            return _channelToasts;
        }
    }

    private void ReleaseYouTubeImport()
    {
        ReleaseYouTubeChannel();
        _importMenu = null;
        _importToasts?.Reset();
        if (_youTubeImport is not { } import)
            return;
        _youTubeImport = null;
        import.Changed -= OnMoreChanged;
        import.ResourcesWritten -= OnImportResourcesWritten;
        import.Dispose();
    }
}
