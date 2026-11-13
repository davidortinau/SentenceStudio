using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Native.iOS.Features.Import;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>Library routes of the YouTube import pages.</summary>
internal static class ApplicationYouTubeRoutes
{
    /// <summary>Source /media-import (and /import).</summary>
    internal const string YouTubeImport = "youtube-import";

    /// <summary>Source /media-import/channel/{Id?}, above the YouTube import page.</summary>
    internal const string YouTubeChannel = "youtube-channel";
}

/// <summary>
/// The Library's YouTube import (source /media-import): a browse-level page pushed on the
/// Library stack from the quick-add grid, so the tab bar stays, as on the other Library pages.
/// It has no Save or Cancel (the source page is left like any other page); Back waits only
/// while a video is being saved.
/// </summary>
internal sealed partial class ApplicationController
{
    private YouTubeImportCoordinator? _youTubeImport;
    private NativeToastScheduler? _youTubeImportToasts;
    private YouTubeChannelCoordinator? _youTubeChannel;
    private NativeToastScheduler? _youTubeChannelToasts;
    private long _youTubeChannelGeneration;

    internal bool IsYouTubeImportOpen => _youTubeImport?.IsOpen == true;

    /// <summary>Changes when the YouTube import page must be drawn again.</summary>
    internal long YouTubeImportVersion => _youTubeImport?.Version ?? -1;

    /// <summary>A save (of a video or a channel) is running: Back and the tabs wait.</summary>
    internal bool IsYouTubeImportNavigationBlocked => _youTubeImport?.IsNavigationBlocked == true
        || _youTubeChannel?.IsNavigationBlocked == true;

    /// <summary>The channel page (Save or Cancel decides how it closes) is open.</summary>
    internal bool IsYouTubeChannelOpen => _youTubeImport is not null && _youTubeChannel?.IsOpen == true;

    /// <summary>Changes when the channel page must be drawn again.</summary>
    internal long YouTubeChannelVersion => _youTubeChannel?.Version ?? -1;

    /// <summary>Changes when another channel page replaces the open one (its title and content change).</summary>
    internal long YouTubeChannelGeneration => _youTubeChannelGeneration;

    internal string YouTubeChannelTitle => Text(_youTubeChannel?.IsNew != false
        ? "ChannelDetail_AddChannel" : "ChannelDetail_EditChannel");

    /// <summary>A link or transcript not saved yet (another tab's entry shows the page).</summary>
    internal bool HasYouTubeImportWork => _youTubeImport?.HasUnsavedWork == true;

    internal string YouTubeImportTitle => Text("Import_YouTubeImport");

    /// <summary>
    /// Library quick-add "YouTube Import": pushes the page above the Library list for the
    /// signed-in owner (an absent or untrusted owner opens nothing).
    /// </summary>
    internal void OpenYouTubeImport()
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked
            || IsLibraryPageOpen || IsResourcesFormOpen || IsQuizOpen || _services is null)
            return;
        var provider = _services.Provider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        var owner = _authState is {
            Phase: NativePhase.Ready,
            Destination: NativeDestination.Dashboard,
            UserId: { Length: > 0 } authenticated }
            ? authenticated : null;
#if DEBUG
        if (owner is null && _preview)
            owner = NativePreviewFixture.FromAssembly()?.Owner;
#endif
        if (string.IsNullOrWhiteSpace(owner)
            || !scope.TryGetUserProfileId(out var current) || current != owner)
        {
            _logger?.LogWarning("YouTube import navigation refused an absent or untrusted owner.");
            return;
        }
        _libraryErrorKey = null;
        _selectedTab = NativeTabs.Library;
        var coordinator = new YouTubeImportCoordinator(
            provider.GetRequiredService<INativeYouTubeImport>(), scope,
            provider.GetRequiredService<ILogger<YouTubeImportCoordinator>>());
        coordinator.Changed += Publish;
        coordinator.ResourcesWritten += OnImportResourcesWritten;
        _youTubeImport = coordinator;
        _ = OpenYouTubeImportAsync(coordinator, owner);
        Publish();
    }

    private async Task OpenYouTubeImportAsync(YouTubeImportCoordinator coordinator, string owner)
    {
        try
        {
            if (await coordinator.OpenAsync(owner) || !ReferenceEquals(_youTubeImport, coordinator))
                return;
            _logger?.LogWarning("YouTube import open was refused for the owner.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS YouTube import open failed.");
        }
        if (!ReferenceEquals(_youTubeImport, coordinator) || _disposed)
            return;
        // A page that opened and then lost its owner shows its blocked state; one that never
        // opened goes back to the Library list with the reason.
        if (coordinator.IsOpen)
            return;
        ReleaseYouTubeImport();
        _libraryErrorKey = "Import_ProfileNotFound";
        Publish();
    }

    internal SwiftView RenderYouTubeImport(SourceUiStyle style) =>
        _youTubeImport is { IsOpen: true } import
            ? YouTubeImportView.Page(import, style, Text,
                new YouTubeImportActions(ViewYouTubeResource, () => OpenYouTubeChannel(null),
                    OpenYouTubeChannel))
            : Views.Text(Text("Common_Loading"));

    /// <summary>What the overlay shows: the banner ids and the current toast (redrawn when it changes).</summary>
    internal string YouTubeImportOverlayKey
    {
        get
        {
            if (_youTubeImport is not { IsOpen: true } import)
                return string.Empty;
            var toast = YouTubeImportToast;
            var banners = NativeMessages.Banners(import.Messages);
            return $"{string.Join(',', banners.Select(message => message.Id))}|{toast?.Id ?? 0}";
        }
    }

    internal SwiftView? RenderYouTubeImportOverlay(SourceUiStyle style) =>
        _youTubeImport is { IsOpen: true } import
            ? YouTubeImportView.Messages(import.Messages, YouTubeImportToast, style, Text,
                import.DismissMessages, DismissYouTubeImportToast)
            : null;

    /// <summary>The newest success or info message, shown once as a toast.</summary>
    private NativeMessage? YouTubeImportToast
    {
        get
        {
            if (_youTubeImport is not { } import)
                return null;
            var toasts = _youTubeImportToasts ??= CreateYouTubeImportToasts();
            toasts.Offer(NativeMessages.LatestToast(import.Messages));
            return toasts.Current;
        }
    }

    private void DismissYouTubeImportToast()
    {
        if (_youTubeImportToasts?.Dismiss() == true)
            Publish();
    }

    private NativeToastScheduler CreateYouTubeImportToasts()
    {
        var toasts = new NativeToastScheduler();
        toasts.Changed += Publish;
        return toasts;
    }

    /// <summary>
    /// Source ViewSavedResource and the history's completed rows: the resource's detail above
    /// the page (Back returns to it).
    /// </summary>
    private void ViewYouTubeResource(string resourceId)
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked
            || IsResourcesFormOpen || !IsYouTubeImportOpen || string.IsNullOrWhiteSpace(resourceId))
            return;
        EnsureResources();
        if (_resources is null)
            return;
        _resources.Open(resourceId);
        Publish();
    }

    // ---- The channel page (source /media-import/channel/{Id?}) ----

    /// <summary>
    /// Source AddChannel and EditChannel: the channel page above the YouTube import page, for the
    /// page's owner (an absent or untrusted owner opens nothing).
    /// </summary>
    private void OpenYouTubeChannel(string? channelId)
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked || _services is null
            || !IsYouTubeImportOpen || IsYouTubeChannelOpen || IsResourcesFormOpen)
            return;
        var provider = _services.Provider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        var owner = _authState is { Phase: NativePhase.Ready, UserId: { Length: > 0 } authenticated }
            ? authenticated : null;
#if DEBUG
        if (owner is null && _preview)
            owner = NativePreviewFixture.FromAssembly()?.Owner;
#endif
        if (string.IsNullOrWhiteSpace(owner)
            || !scope.TryGetUserProfileId(out var current) || current != owner)
        {
            _logger?.LogWarning("YouTube channel navigation refused an absent or untrusted owner.");
            return;
        }
        var coordinator = new YouTubeChannelCoordinator(
            provider.GetRequiredService<INativeYouTubeImport>(), scope,
            provider.GetRequiredService<ILogger<YouTubeChannelCoordinator>>());
        coordinator.Changed += Publish;
        coordinator.Finished += OnYouTubeChannelFinished;
        _youTubeChannel = coordinator;
        _youTubeChannelGeneration++;
        _ = OpenYouTubeChannelAsync(coordinator, owner, channelId);
        Publish();
    }

    private async Task OpenYouTubeChannelAsync(YouTubeChannelCoordinator coordinator, string owner,
        string? channelId)
    {
        try
        {
            if (await coordinator.OpenAsync(owner, channelId) || !ReferenceEquals(_youTubeChannel, coordinator))
                return;
            _logger?.LogWarning("YouTube channel page open was refused for the owner.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS YouTube channel page open failed.");
        }
        if (!ReferenceEquals(_youTubeChannel, coordinator) || _disposed || coordinator.IsOpen)
            return;
        ReleaseYouTubeChannel();
        Publish();
    }

    /// <summary>
    /// Source SaveChannel's navigation: a saved channel returns to the page (its toast shows
    /// there and the channels and history are read again, as the source page re-initializes); an
    /// unresolved new channel opens its edit page instead.
    /// </summary>
    private void OnYouTubeChannelFinished(YouTubeChannelOutcome outcome, string? channelId)
    {
        if (_youTubeChannel is not { } channel)
            return;
        var toast = NativeMessages.LatestToast(channel.Messages);
        var banners = NativeMessages.Banners(channel.Messages);
        ReleaseYouTubeChannel();
        if (outcome == YouTubeChannelOutcome.EditSaved && !string.IsNullOrEmpty(channelId))
        {
            Publish();
            OpenYouTubeChannel(channelId);
            _youTubeChannel?.ShowMessages(banners);
            return;
        }
        if (_youTubeImport is { } import)
        {
            if (toast is not null)
                import.ShowMessage(toast);
            _ = import.ReloadAfterChannelPageAsync();
        }
        Publish();
    }

    internal SwiftView RenderYouTubeChannel(SourceUiStyle style) =>
        _youTubeChannel is { IsOpen: true } channel
            ? YouTubeChannelView.Page(channel, style, Text)
            : Views.Text(Text("Common_Loading"));

    internal string YouTubeChannelOverlayKey
    {
        get
        {
            if (_youTubeChannel is not { IsOpen: true } channel)
                return string.Empty;
            var toast = YouTubeChannelToast;
            var banners = NativeMessages.Banners(channel.Messages);
            return $"{string.Join(',', banners.Select(message => message.Id))}|{toast?.Id ?? 0}";
        }
    }

    internal SwiftView? RenderYouTubeChannelOverlay(SourceUiStyle style) =>
        _youTubeChannel is { IsOpen: true } channel
            ? YouTubeImportView.Messages(channel.Messages, YouTubeChannelToast, style, Text,
                channel.DismissMessages, DismissYouTubeChannelToast)
            : null;

    private NativeMessage? YouTubeChannelToast
    {
        get
        {
            if (_youTubeChannel is not { } channel)
                return null;
            var toasts = _youTubeChannelToasts ??= CreateYouTubeChannelToasts();
            toasts.Offer(NativeMessages.LatestToast(channel.Messages));
            return toasts.Current;
        }
    }

    private void DismissYouTubeChannelToast()
    {
        if (_youTubeChannelToasts?.Dismiss() == true)
            Publish();
    }

    private NativeToastScheduler CreateYouTubeChannelToasts()
    {
        var toasts = new NativeToastScheduler();
        toasts.Changed += Publish;
        return toasts;
    }

    /// <summary>Back (or a swipe) from the channel page: the same as Cancel.</summary>
    internal void OnYouTubeChannelPopped()
    {
        if (_youTubeChannel is null)
            return;
        ReleaseYouTubeChannel();
        _ = _youTubeImport?.ReloadAfterChannelPageAsync();
        Publish();
    }

    private void ReleaseYouTubeChannel()
    {
        _youTubeChannelToasts?.Reset();
        if (_youTubeChannel is not { } channel)
            return;
        _youTubeChannel = null;
        channel.Changed -= Publish;
        channel.Finished -= OnYouTubeChannelFinished;
        channel.Dispose();
    }

    /// <summary>Back from the YouTube import page to the Library list.</summary>
    internal void OnYouTubeImportPopped()
    {
        if (_youTubeImport is null)
            return;
        ReleaseYouTubeImport();
        Publish();
    }

    private void ReleaseYouTubeImport()
    {
        ReleaseYouTubeChannel();
        _youTubeImportToasts?.Reset();
        if (_youTubeImport is not { } import)
            return;
        _youTubeImport = null;
        import.Changed -= Publish;
        import.ResourcesWritten -= OnImportResourcesWritten;
        import.Dispose();
    }
}
