using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Video Watching (source UI/Pages/VideoWatching.razor, activities.md 11):
/// the owned plan resource's YouTube video (id extracted by the shared
/// <see cref="VideoWatchingService"/>, exactly as the source) with its title,
/// "Open in YouTube" and a collapsible transcript. The source starts the
/// activity timer on open and stops it on Back; it records no progress and
/// no UserActivity. Unlike the source, the resource is loaded only for the
/// launch owner and the page is refused when that owner is absent or changes.
/// </summary>
public sealed class NativeVideoWatchingPresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    LearningResourceRepository resources,
    VideoWatchingService videos,
    NativePendingWrites pendingWrites,
    ILogger<NativeVideoWatchingPresenter> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private NativeActivityLaunch? _launch;
    private NativeVideoWatchingState _state = new(NativePhase.Idle);
    private bool _exited;
    private bool _disposed;

    public NativeVideoWatchingState State
    {
        get
        {
            lock (_sync)
                return _launch is not null && !owner.IsActive(_launch.UserId)
                    && _state.Destination != NativeDestination.TodaysPlan
                    ? new(NativePhase.Blocked, ErrorKey: NativeVideoWatchingText.Unavailable)
                    : _state;
        }
    }

    public event Action<NativeVideoWatchingState>? StateChanged;

    public async Task<NativeVideoWatchingState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null || _exited)
                return State;
            _launch = launch;
            Set(new(NativePhase.Loading));
            // Source OnInitializedAsync starts the timer before loading the video.
            if (launch.Route != NativeActivityCatalog.VideoWatching
                || !owner.IsActive(launch.UserId)
                || !await timer.StartAsync(launch, cancellationToken))
                return Set(new(NativePhase.Blocked, ErrorKey: NativeVideoWatchingText.Unavailable));
            return Set(await LoadAsync(launch));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source LoadVideo, with the resource bound to the launch owner.</summary>
    private async Task<NativeVideoWatchingState> LoadAsync(NativeActivityLaunch launch)
    {
        var resourceId = launch.ResourceIds.IsDefaultOrEmpty ? null : launch.ResourceIds[0];
        if (string.IsNullOrEmpty(resourceId))
            return Failure(NativeVideoWatchingText.NoResource);
        try
        {
            var resource = await resources.GetResourceAsync(resourceId, launch.UserId);
            if (!owner.IsActive(launch.UserId))
                return new(NativePhase.Blocked, ErrorKey: NativeVideoWatchingText.Unavailable);
            if (resource is null || resource.UserProfileId != launch.UserId)
                return Failure(NativeVideoWatchingText.ResourceNotFound);
            var videoId = videos.ExtractYouTubeVideoId(resource.MediaUrl);
            if (string.IsNullOrEmpty(videoId))
                return Failure(NativeVideoWatchingText.NoVideoId);
            return new(NativePhase.Ready,
                ResourceTitle: resource.Title,
                VideoId: videoId,
                MediaUrl: resource.MediaUrl,
                Transcript: resource.Transcript,
                TranscriptLanguageTag: NativeReadingPresenter.PassageLanguageTag(
                    resource.Language, resource.Transcript));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Video Watching could not load its resource.");
            return Failure(NativeVideoWatchingText.LoadError, ex.Message);
        }
    }

    private static NativeVideoWatchingState Failure(string key, string? argument = null) =>
        new(NativePhase.Error, ErrorKey: key, ErrorArgument: argument);

    /// <summary>Source ToggleTranscript (collapsed by default).</summary>
    public async Task<NativeVideoWatchingState> ToggleTranscriptAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_exited || _launch is null || _state.Phase != NativePhase.Ready
                || !_state.HasTranscript)
                return State;
            if (!owner.IsActive(_launch.UserId))
                return Set(new(NativePhase.Blocked, ErrorKey: NativeVideoWatchingText.Unavailable));
            return Set(_state with { ShowTranscript = !_state.ShowTranscript });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Go Back: stop and save the timer and return to the Dashboard.</summary>
    public async Task<NativeVideoWatchingState> ExitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_exited)
                return State;
            _exited = true;
            string? errorKey = null;
            try
            {
                if (await timer.StopAsync(cancellationToken) == NativeActivityStop.Failed)
                    errorKey = NativeText.SaveFailed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Video Watching could not save its activity minutes.");
                errorKey = NativeText.SaveFailed;
            }
            return Set(_state with
            {
                Destination = NativeDestination.TodaysPlan,
                ErrorKey = errorKey ?? (_state.Phase == NativePhase.Ready ? null : _state.ErrorKey),
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private NativeVideoWatchingState Set(NativeVideoWatchingState next)
    {
        NativeVideoWatchingState state;
        lock (_sync)
        {
            _state = next with
            {
                HasPendingSync = _launch is not null && pendingWrites.IsPending(_launch.UserId)
            };
            state = State;
        }
        StateChanged?.Invoke(state);
        return state;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        // Navigation away without Back: the timer pauses (saving a changed
        // whole minute) and stops, like the source DisposeAsync.
        timer.Dispose();
    }
}
