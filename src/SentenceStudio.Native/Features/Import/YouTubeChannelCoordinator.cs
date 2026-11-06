using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Import;

/// <summary>A discovered upload and its latest import status (source ChannelDetail DiscoveredVideo).</summary>
public sealed record YouTubeDiscoveredVideo(string VideoId, string Title, string Url, VideoImportStatus? ImportStatus)
{
    public bool IsCompleted => ImportStatus == VideoImportStatus.Completed;
    public bool IsFailed => ImportStatus == VideoImportStatus.Failed;
    public bool IsInProgress => ImportStatus.HasValue
        && ImportStatus != VideoImportStatus.Completed
        && ImportStatus != VideoImportStatus.Failed;
    public bool CanSelect => !IsCompleted && !IsInProgress;
}

/// <summary>How the channel page ends: back to the list, or the saved channel's edit page.</summary>
public enum YouTubeChannelOutcome { None, Saved, EditSaved }

public sealed record YouTubeChannelState(
    bool Open,
    bool Blocked,
    bool IsNew,
    bool IsLoading,
    bool IsSaving,
    bool IsResolving,
    NativeMessage? Error,
    string ChannelUrl,
    string Language,
    int CheckIntervalHours,
    bool IsActive,
    bool HasChannel,
    bool HasYouTubeChannelId,
    string? ResolutionError,
    bool IsRetryingResolve,
    ImmutableArray<YouTubeDiscoveredVideo> Videos,
    ImmutableHashSet<string> SelectedVideoIds,
    bool IsCheckingVideos,
    bool IsImportingVideos,
    bool HasCheckedVideos,
    NativeMessage? DiscoveryError,
    bool IsRetryingImport,
    string? RetryingVideoId,
    ImmutableArray<NativeMessage> Messages)
{
    /// <summary>Source Save Channel: disabled while saving or resolving, or without a link and a language.</summary>
    public bool CanSave => Open && !Blocked && !IsSaving && !IsResolving
        && !string.IsNullOrWhiteSpace(ChannelUrl) && !string.IsNullOrWhiteSpace(Language);

    /// <summary>Source: the discovery card shows only when editing a loaded channel.</summary>
    public bool ShowsDiscovery => !IsNew && HasChannel;

    /// <summary>Source Select None / Select All New: every selectable new video is selected.</summary>
    public bool AllNewSelected => SelectedVideoIds.Count == Videos.Count(video => video.CanSelect && !video.IsFailed);
}

/// <summary>
/// The source Channel Detail page (src/SentenceStudio.UI/Pages/ChannelDetail.razor, routes
/// /media-import/channel/{Id?}) for one verified owner: the channel form (link, language, check
/// interval, monitoring), Save and Cancel, and on an existing channel the video discovery (check
/// for new videos, select, import selected, retry a failed video, retry resolving the channel).
/// The channel rows and imports go through the API (/api/channels, /api/imports); resolving a
/// channel and listing its uploads run on the device through the shared ChannelMonitorService.
/// An absent or changed owner blocks the page and nothing is read or written.
/// </summary>
public sealed class YouTubeChannelCoordinator : IDisposable
{
    /// <summary>Source CheckForNewVideos: the channel's 20 most recent uploads.</summary>
    public const int DiscoveryCount = 20;

    /// <summary>Source ChannelDetail check intervals.</summary>
    public static readonly ImmutableArray<int> CheckIntervals = [6, 12, 24, 48];

    private readonly INativeYouTubeImport _import;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly ILogger _logger;
    private readonly TimeSpan _retryRefreshDelay;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp _ownerStamp;
    private bool _open;
    private bool _blocked;
    private bool _disposed;

    // Source fields (ChannelDetail.razor:227-262), with the same defaults.
    private string? _channelId;
    private bool _isLoading = true;
    private bool _isSaving;
    private bool _isResolving;
    private NativeMessage? _error;
    private string _channelUrl = string.Empty;
    private string _language = string.Empty;
    private int _checkIntervalHours = 24;
    private bool _isActive = true;
    private MonitoredChannel? _channel;
    private List<YouTubeDiscoveredVideo> _videos = [];
    private HashSet<string> _selected = new(StringComparer.Ordinal);
    private bool _isCheckingVideos;
    private bool _isImportingVideos;
    private bool _hasCheckedVideos;
    private NativeMessage? _discoveryError;
    private string? _resolutionError;
    private bool _isRetryingResolve;
    private bool _isRetryingImport;
    private string? _retryingVideoId;
    private ImmutableArray<NativeMessage> _messages = [];

    public YouTubeChannelCoordinator(INativeYouTubeImport import, DeviceUserScopeProvider userScope,
        ILogger<YouTubeChannelCoordinator> logger, TimeSpan? retryRefreshDelay = null)
    {
        _import = import;
        _userScope = userScope;
        _logger = logger;
        _retryRefreshDelay = retryRefreshDelay ?? TimeSpan.FromSeconds(1);
    }

    public event Action? Changed;

    /// <summary>Raised when the page ends: back to the list (saved) or the saved channel's edit page.</summary>
    public event Action<YouTubeChannelOutcome, string?>? Finished;

    public long Version { get; private set; }

    public bool IsOpen => _open;

    public bool IsNew => string.IsNullOrEmpty(_channelId);

    /// <summary>A save is running: Back waits (source: the buttons are disabled).</summary>
    public bool IsNavigationBlocked => _isSaving;

    public ImmutableArray<NativeMessage> Messages => _messages;

    public YouTubeChannelState State => new(_open, _blocked, IsNew, _isLoading, _isSaving, _isResolving,
        _error, _channelUrl, _language, _checkIntervalHours, _isActive, _channel is not null,
        !string.IsNullOrEmpty(_channel?.YouTubeChannelId), _resolutionError, _isRetryingResolve,
        [.. _videos], _selected.ToImmutableHashSet(StringComparer.Ordinal), _isCheckingVideos,
        _isImportingVideos, _hasCheckedVideos, _discoveryError, _isRetryingImport, _retryingVideoId,
        _messages);

    /// <summary>
    /// Source OnInitializedAsync: an existing channel loads (from the owner's channel list); a new
    /// one starts with the profile's target language.
    /// </summary>
    public async Task<bool> OpenAsync(string? expectedOwner, string? channelId)
    {
        if (_disposed || _open)
            return false;
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !_userScope.TryGetUserProfileId(out var active) || active != expectedOwner)
        {
            _logger.LogWarning("YouTube channel page refused an absent or untrusted owner.");
            return false;
        }
        var stamp = _userScope.CaptureStamp();
        if (stamp.PublishedOwner != expectedOwner || !_userScope.IsCurrent(stamp))
        {
            _logger.LogWarning("YouTube channel page refused a changed owner.");
            return false;
        }
        _owner = expectedOwner;
        _ownerStamp = stamp;
        _channelId = string.IsNullOrWhiteSpace(channelId) ? null : channelId;
        _open = true;
        _isLoading = true;
        Notify();
        var token = _lifetime.Token;
        try
        {
            if (!IsNew)
            {
                var channels = await OffUiAsync(() => _import.ListChannelsAsync(expectedOwner, token));
                if (!TryOwner(out _))
                    return false;
                _channel = channels.FirstOrDefault(row => row.Id == _channelId);
                if (_channel is not null)
                {
                    _channelUrl = _channel.ChannelUrl ?? string.Empty;
                    _language = _channel.Language ?? string.Empty;
                    _checkIntervalHours = _channel.CheckIntervalHours;
                    _isActive = _channel.IsActive;
                }
                else
                    _error = NativeMessage.Error("YouTubeChannel_NotFound");
            }
            else
            {
                // Source: a failed profile read leaves the language empty.
                try
                {
                    _language = await OffUiAsync(() => _import.GetTargetLanguageAsync(expectedOwner, token))
                        ?? string.Empty;
                }
                catch (Exception ex) when (ex is not UnauthorizedAccessException
                    && (ex is not OperationCanceledException || !_disposed))
                {
                    _logger.LogInformation(ex, "YouTube channel page could not read the profile language.");
                }
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load channel");
            _error = NativeMessage.Error("YouTubeChannel_LoadFailed");
        }
        finally
        {
            if (!_disposed)
            {
                _isLoading = false;
                Notify();
            }
        }
        return TryOwner(out _);
    }

    // ---- The form (bound inputs: no redraw) ----

    public void SetChannelUrl(string value)
    {
        if (CanEdit())
            _channelUrl = value ?? string.Empty;
    }

    /// <summary>The language select (redraws: Save's availability follows it).</summary>
    public void SetLanguage(string value)
    {
        if (!CanEdit())
            return;
        _language = value ?? string.Empty;
        Notify();
    }

    public void SetCheckIntervalHours(int hours)
    {
        if (CanEdit() && CheckIntervals.Contains(hours))
            _checkIntervalHours = hours;
    }

    public void SetIsActive(bool value)
    {
        if (CanEdit())
            _isActive = value;
    }

    /// <summary>Save's availability as the link is typed (no redraw).</summary>
    public bool CanSave => State.CanSave;

    /// <summary>Source SaveChannel.</summary>
    public async Task SaveAsync()
    {
        if (!_open || _blocked || _disposed || _isSaving || _isResolving || !TryOwner(out var owner))
            return;
        if (string.IsNullOrWhiteSpace(_channelUrl) || string.IsNullOrWhiteSpace(_language))
        {
            _error = NativeMessage.Error("YouTubeChannel_Required");
            Notify();
            return;
        }
        _isSaving = true;
        _error = null;
        _messages = [];
        Notify();
        var outcome = (YouTubeChannelOutcome.None, (string?)null);
        try
        {
            outcome = IsNew
                ? await AddNewAsync(owner, _lifetime.Token)
                : await UpdateExistingAsync(owner, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save channel");
            _error = NativeMessage.Error("YouTubeChannel_SaveFailed", ex.Message);
        }
        finally
        {
            if (!_disposed)
            {
                _isSaving = false;
                _isResolving = false;
                Notify();
            }
        }
        if (outcome.Item1 != YouTubeChannelOutcome.None && !_disposed && !_blocked)
            Finished?.Invoke(outcome.Item1, outcome.Item2);
    }

    /// <summary>
    /// Source SaveChannel for a new channel: the server resolves its metadata and saves it; an
    /// unresolved channel is saved too and its edit page opens.
    /// </summary>
    private async Task<(YouTubeChannelOutcome, string?)> AddNewAsync(string owner, CancellationToken token)
    {
        _isResolving = true;
        Notify();
        try
        {
            var channel = new MonitoredChannel
            {
                ChannelUrl = _channelUrl,
                Language = _language,
                CheckIntervalHours = _checkIntervalHours,
                IsActive = _isActive,
                ChannelHandle = ExtractChannelHandle(_channelUrl),
                UserProfileId = owner
            };
            var added = await OffUiAsync(() => _import.AddChannelAsync(owner, channel, token));
            if (_disposed || !TryOwner(out _))
                return (YouTubeChannelOutcome.None, null);
            if (!added.MetadataResolved)
            {
                _resolutionError = added.ResolutionError ?? "Unknown error";
                _messages = _messages.Add(NativeMessage.Error("YouTubeChannel_SavedUnresolved",
                    _resolutionError));
                return (YouTubeChannelOutcome.EditSaved, added.Channel.Id);
            }
            _messages = _messages.Add(NativeMessage.Success("ChannelDetail_ChannelAdded",
                added.Channel.ChannelName ?? added.Channel.ChannelHandle ?? string.Empty));
            return (YouTubeChannelOutcome.Saved, added.Channel.Id);
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException
            && (ex is not OperationCanceledException || !_disposed))
        {
            _logger.LogError(ex, "Failed to add channel");
            _error = NativeMessage.Error("YouTubeChannel_AddFailed");
            return (YouTubeChannelOutcome.None, null);
        }
        finally
        {
            _isResolving = false;
        }
    }

    /// <summary>
    /// Source SaveChannel for an existing channel: resolution is tried again while its YouTube id
    /// is missing; an unresolved channel is saved and the page stays.
    /// </summary>
    private async Task<(YouTubeChannelOutcome, string?)> UpdateExistingAsync(string owner,
        CancellationToken token)
    {
        if (_channel is not { } channel)
            return (YouTubeChannelOutcome.None, null);
        channel.ChannelUrl = _channelUrl;
        channel.Language = _language;
        channel.CheckIntervalHours = _checkIntervalHours;
        channel.IsActive = _isActive;
        if (string.IsNullOrEmpty(channel.YouTubeChannelId) && !string.IsNullOrWhiteSpace(_channelUrl))
        {
            _isResolving = true;
            Notify();
            var (resolved, resolveError) = await OffUiAsync(() =>
                _import.ResolveChannelAsync(owner, channel, token));
            _isResolving = false;
            if (_disposed || !TryOwner(out _))
                return (YouTubeChannelOutcome.None, null);
            if (!resolved)
            {
                _resolutionError = resolveError ?? "Unknown error";
                _messages = _messages.Add(NativeMessage.Error("YouTubeChannel_UpdatedUnresolved",
                    _resolutionError));
                await OffUiAsync(async () =>
                {
                    await _import.UpdateChannelAsync(owner, channel, token);
                    return true;
                });
                return (YouTubeChannelOutcome.None, null);
            }
        }
        await OffUiAsync(async () =>
        {
            await _import.UpdateChannelAsync(owner, channel, token);
            return true;
        });
        if (_disposed || !TryOwner(out _))
            return (YouTubeChannelOutcome.None, null);
        _messages = _messages.Add(NativeMessage.Success("ChannelDetail_ChannelUpdated"));
        return (YouTubeChannelOutcome.Saved, channel.Id);
    }

    // ---- Video discovery (source ChannelDetail.razor:448-654) ----

    /// <summary>Source CheckForNewVideos: the recent uploads with their latest import status.</summary>
    public async Task CheckForNewVideosAsync()
    {
        if (!_open || _blocked || _disposed || _channel is not { } channel || _isCheckingVideos
            || string.IsNullOrEmpty(channel.YouTubeChannelId) || !TryOwner(out var owner))
            return;
        _isCheckingVideos = true;
        _discoveryError = null;
        _videos = [];
        _selected.Clear();
        _messages = [];
        Notify();
        var token = _lifetime.Token;
        try
        {
            var videos = await OffUiAsync(() =>
                _import.GetRecentVideosAsync(owner, channel, DiscoveryCount, token));
            // Source GetVideoImportStatusAsync: each video's latest import, however old (not
            // only those among the history's newest rows).
            var ids = videos.Select(video => video.VideoId).ToList();
            var imports = await OffUiAsync(() => _import.ListLatestImportsForVideosAsync(owner, ids, token));
            if (_disposed || !TryOwner(out _))
                return;
            var latest = LatestStatusByVideo(imports);
            _videos = videos.Select(video => new YouTubeDiscoveredVideo(video.VideoId, video.Title, video.Url,
                latest.TryGetValue(video.VideoId, out var status) ? status : null)).ToList();
            foreach (var video in _videos.Where(video => video.CanSelect && !video.IsFailed))
                _selected.Add(video.VideoId);
            _hasCheckedVideos = true;
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for new videos");
            _discoveryError = NativeMessage.Error("YouTubeChannel_CheckFailed", ex.Message);
        }
        finally
        {
            if (!_disposed)
            {
                _isCheckingVideos = false;
                Notify();
            }
        }
    }

    /// <summary>A video's checkbox (only a selectable video).</summary>
    public void ToggleVideo(string videoId)
    {
        if (!_open || _blocked || _isImportingVideos
            || _videos.FirstOrDefault(video => video.VideoId == videoId) is not { CanSelect: true })
            return;
        if (!_selected.Remove(videoId))
            _selected.Add(videoId);
        Notify();
    }

    /// <summary>Source ToggleSelectAll.</summary>
    public void ToggleSelectAll()
    {
        if (!_open || _blocked || _isImportingVideos)
            return;
        var selectable = _videos.Where(video => video.CanSelect).Select(video => video.VideoId).ToList();
        if (_selected.Count == selectable.Count)
            _selected.Clear();
        else
            _selected = new HashSet<string>(selectable, StringComparer.Ordinal);
        Notify();
    }

    /// <summary>
    /// Source ImportSelectedVideos: each selected video starts an import on the server, linked to
    /// this channel and naming its video (source MonitoredChannelId, VideoId, VideoTitle), then
    /// the list is read again.
    /// </summary>
    public async Task ImportSelectedAsync()
    {
        if (!_open || _blocked || _disposed || _channel is not { } channel || _isImportingVideos
            || _selected.Count == 0 || !TryOwner(out var owner))
            return;
        _isImportingVideos = true;
        _discoveryError = null;
        _messages = [];
        Notify();
        var token = _lifetime.Token;
        var started = false;
        var failed = false;
        try
        {
            var selected = _videos.Where(video => _selected.Contains(video.VideoId)).ToList();
            var count = 0;
            foreach (var video in selected)
            {
                await OffUiAsync(async () =>
                {
                    await _import.StartImportAsync(owner, new NativeYouTubeImportRequest(
                        video.Url, channel.Language, channel.Id, video.VideoId, video.Title), token);
                    return true;
                });
                count++;
                // Started: no longer selected, so a later tap does not import it again.
                _selected.Remove(video.VideoId);
                if (_disposed || !TryOwner(out _))
                    return;
            }
            _messages = _messages.Add(NativeMessage.Success("ChannelDetail_ImportStarted",
                count.ToString(System.Globalization.CultureInfo.CurrentCulture)));
            _selected.Clear();
            started = true;
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import selected videos");
            _discoveryError = NativeMessage.Error("YouTubeChannel_ImportFailed", ex.Message);
            failed = true;
        }
        finally
        {
            if (!_disposed)
            {
                _isImportingVideos = false;
                Notify();
            }
        }
        // Some videos may have started (or the failed request may have reached the server):
        // their statuses are read again; the error stays.
        if (failed)
            await RefreshStatusesAsync();
        if (started)
        {
            var messages = _messages;
            await CheckForNewVideosAsync();
            // The refresh starts from no messages; the import's toast stays.
            if (!_disposed && _messages.IsEmpty)
            {
                _messages = messages;
                Notify();
            }
        }
    }

    /// <summary>Source RetryResolveMetadata: the link as typed is resolved again, then saved.</summary>
    public async Task RetryResolveAsync()
    {
        if (!_open || _blocked || _disposed || _channel is not { } channel || _isRetryingResolve
            || !TryOwner(out var owner))
            return;
        _isRetryingResolve = true;
        _resolutionError = null;
        _messages = [];
        Notify();
        var token = _lifetime.Token;
        try
        {
            channel.ChannelUrl = _channelUrl;
            var (resolved, error) = await OffUiAsync(() => _import.ResolveChannelAsync(owner, channel, token));
            if (_disposed || !TryOwner(out _))
                return;
            if (resolved)
            {
                await OffUiAsync(async () =>
                {
                    await _import.UpdateChannelAsync(owner, channel, token);
                    return true;
                });
                if (_disposed || !TryOwner(out _))
                    return;
                _channelUrl = channel.ChannelUrl ?? _channelUrl;
                _messages = _messages.Add(NativeMessage.Success("YouTubeChannel_Resolved",
                    $"{channel.ChannelName} ({channel.YouTubeChannelId})"));
            }
            else
            {
                _resolutionError = error ?? "Unknown error resolving channel";
                _messages = _messages.Add(NativeMessage.Error("YouTubeChannel_ResolutionFailed", _resolutionError));
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry resolve failed");
            _resolutionError = ex.Message;
            _messages = _messages.Add(NativeMessage.Error("YouTubeChannel_ResolutionFailed", ex.Message));
        }
        finally
        {
            if (!_disposed)
            {
                _isRetryingResolve = false;
                Notify();
            }
        }
    }

    /// <summary>
    /// Source RetryFailedVideo: the video's latest failed import runs again on the server, which
    /// keeps the import's own channel link (the retry names only the import).
    /// </summary>
    public async Task RetryFailedVideoAsync(string videoId)
    {
        if (!_open || _blocked || _disposed || _isRetryingImport
            || _videos.FirstOrDefault(video => video.VideoId == videoId) is not { IsFailed: true } video
            || !TryOwner(out var owner))
            return;
        _isRetryingImport = true;
        _retryingVideoId = videoId;
        _messages = [];
        Notify();
        var token = _lifetime.Token;
        var refresh = false;
        try
        {
            var imports = await OffUiAsync(() =>
                _import.ListLatestImportsForVideosAsync(owner, [videoId], token));
            if (_disposed || !TryOwner(out _))
                return;
            var failed = imports.FirstOrDefault(import => import.VideoId == videoId
                && import.Status == VideoImportStatus.Failed);
            if (failed is null)
            {
                _messages = _messages.Add(NativeMessage.Error("YouTubeChannel_FailedImportNotFound"));
                return;
            }
            await OffUiAsync(async () =>
            {
                await _import.RetryImportAsync(owner, failed.Id, token);
                return true;
            });
            if (_disposed || !TryOwner(out _))
                return;
            _messages = _messages.Add(NativeMessage.Info("YouTubeChannel_RetryingVideo", video.Title));
            refresh = true;
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry failed for video {Title}", video.Title);
            _messages = _messages.Add(NativeMessage.Error("YouTubeImport_RetryFailed", ex.Message));
        }
        finally
        {
            if (!_disposed)
            {
                _isRetryingImport = false;
                _retryingVideoId = null;
                Notify();
            }
        }
        if (!refresh)
            return;
        // Source: a second later, the discovery list is read again; the toast stays.
        try
        {
            await Task.Delay(_retryRefreshDelay, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var messages = _messages;
        await CheckForNewVideosAsync();
        if (!_disposed && _messages.IsEmpty)
        {
            _messages = messages;
            Notify();
        }
    }

    /// <summary>
    /// The listed videos' import statuses read again (after Import Selected failed partway); the
    /// list, the error and the selection of what can still be selected stay.
    /// </summary>
    private async Task RefreshStatusesAsync()
    {
        if (!_open || _blocked || _disposed || _videos.Count == 0 || !TryOwner(out var owner))
            return;
        var token = _lifetime.Token;
        try
        {
            var ids = _videos.Select(video => video.VideoId).ToList();
            var imports = await OffUiAsync(() => _import.ListLatestImportsForVideosAsync(owner, ids, token));
            if (_disposed || !TryOwner(out _))
                return;
            var latest = LatestStatusByVideo(imports);
            _videos = _videos.Select(video => video with
            {
                ImportStatus = latest.TryGetValue(video.VideoId, out var status) ? status : null
            }).ToList();
            _selected.RemoveWhere(id => _videos.FirstOrDefault(video => video.VideoId == id) is not { CanSelect: true });
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex);
        }
        catch (Exception ex)
        {
            // The import's error is already shown; the statuses stay as they were read last.
            _logger.LogError(ex, "Failed to read the videos' import statuses again");
        }
        finally
        {
            if (!_disposed)
                Notify();
        }
    }

    /// <summary>Each video's latest import status (one row per video; else the newest listed first).</summary>
    private static Dictionary<string, VideoImportStatus> LatestStatusByVideo(
        IEnumerable<NativeYouTubeImportRecord> imports) => imports
        .Where(import => !string.IsNullOrEmpty(import.VideoId))
        .GroupBy(import => import.VideoId!, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Status, StringComparer.Ordinal);

    /// <summary>Messages carried over from the page this one replaced (an unresolved new channel).</summary>
    public void ShowMessages(IEnumerable<NativeMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        // Also before the page has finished opening: the messages wait for its first draw.
        if (_disposed)
            return;
        _messages = _messages.AddRange(messages);
        Notify();
    }

    public void DismissMessages()
    {
        if (_messages.IsEmpty)
            return;
        _messages = [];
        Notify();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _open = false;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Source ExtractChannelHandle: "@handle" from a /@handle link, else empty.</summary>
    internal static string ExtractChannelHandle(string url)
    {
        if (url.Contains("/@", StringComparison.Ordinal))
        {
            var parts = url.Split("/@");
            if (parts.Length > 1)
                return "@" + parts[1].Split('/')[0].Split('?')[0];
        }
        return string.Empty;
    }

    private static Task<T> OffUiAsync<T>(Func<Task<T>> work) => Task.Run(work);

    private bool CanEdit() => _open && !_blocked && !_disposed && !_isSaving;

    private bool TryOwner(out string owner)
    {
        owner = _owner ?? string.Empty;
        if (_disposed || !_open || _blocked || string.IsNullOrWhiteSpace(_owner))
            return false;
        if (_userScope.IsCurrent(_ownerStamp)
            && _userScope.TryGetUserProfileId(out var active) && active == _owner)
            return true;
        _logger.LogWarning("YouTube channel page stopped: the device owner changed.");
        _blocked = true;
        Notify();
        return false;
    }

    private void OnOwnerRefused(UnauthorizedAccessException ex)
    {
        _logger.LogWarning(ex, "YouTube channel page was refused for an absent or changed owner.");
        if (_disposed)
            return;
        _blocked = true;
        Notify();
    }

    private void Notify()
    {
        if (_disposed)
            return;
        Version++;
        Changed?.Invoke();
    }
}
