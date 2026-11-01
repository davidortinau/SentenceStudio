using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Native;

/// <summary>
/// Shadowing (source UI/Pages/Shadowing.razor, activities.md 8); also serves
/// Today's Plan type Listening. Listen-and-repeat: the target sentence is
/// always shown, the native translation only on request (hidden again on
/// every sentence change), and target-language audio is prepared for each
/// sentence at the chosen speed (Slow 0.6 / Normal 1.0). Changing speed
/// reloads and autoplays; Next/Previous prepare without autoplay. No
/// vocabulary progress or UserActivity is written; only plan minutes through
/// the shared timer. Unlike the source, every read, AI call and audio request
/// is bound to the launch owner and refused when that owner changes.
/// </summary>
public sealed class NativeShadowingPresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    NativeShadowingContent content,
    INativeShadowingSpeech speech,
    INativeShadowingPlayer player,
    NativePendingWrites pendingWrites,
    ILogger<NativeShadowingPresenter> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private NativeActivityLaunch? _launch;
    private NativeShadowingState _state = new(NativePhase.Idle);
    private ImmutableArray<NativeShadowingSentence> _sentences = [];
    private string _language = "Korean";
    private int _index;
    private bool _showTranslation;
    private float _speed = NativeShadowingSpeed.Normal;
    private bool _buffering;
    private bool _playing;
    private double _position;
    private NativeShadowingAudioIssue _issue;
    private long _audioVersion;
    private CancellationTokenSource? _audioLoad;
    private CancellationTokenSource? _ticker;
    private bool _subscribed;
    private bool _exited;
    private bool _disposed;

    /// <summary>Source playback timer interval.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public NativeShadowingState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeShadowingText.Unavailable)
            : _state;

    public event Action<NativeShadowingState>? StateChanged;

    public async Task<NativeShadowingState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null)
                return State;
            _launch = launch;
            Publish(new(NativePhase.Loading));
            if (launch.Route != NativeActivityCatalog.Shadowing
                || !owner.IsActive(launch.UserId)
                || !await timer.StartAsync(launch, cancellationToken))
                return Refuse();
            if (!_subscribed)
            {
                player.Ended += OnPlayerEnded;
                _subscribed = true;
            }

            NativeShadowingContentResult result;
            try
            {
                result = await content.LoadAsync(launch.UserId,
                    launch.ResourceIds.IsDefaultOrEmpty ? null : launch.ResourceIds[0],
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Shadowing could not load its sentences.");
                return Publish(new(NativePhase.Error, ErrorKey: NativeShadowingText.LoadFailed));
            }
            switch (result.Status)
            {
                case NativeShadowingContentStatus.Refused:
                    return Refuse();
                case NativeShadowingContentStatus.Empty:
                    return Publish(new(NativePhase.Empty, ErrorKey: NativeShadowingText.NoSentences));
                case NativeShadowingContentStatus.Failed:
                    return Publish(new(NativePhase.Error, ErrorKey: NativeShadowingText.LoadFailed));
            }
            if (!owner.IsActive(launch.UserId))
                return Refuse();
            _sentences = result.Sentences;
            _language = result.TargetLanguage;
            _index = 0;
            ResetSentenceCore();
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }
        // Source: the first sentence's audio is prepared right after loading, without autoplay.
        return await PrepareAudioAsync(autoplay: false, cancellationToken);
    }

    /// <summary>Play / pause. Without prepared audio this prepares it and plays; a finished clip replays from the start.</summary>
    public async Task<NativeShadowingState> TogglePlaybackAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_buffering)
                return State;
            if (player.IsLoaded)
            {
                if (_playing)
                {
                    player.Pause();
                    _playing = false;
                    StopTicker();
                }
                else
                {
                    var duration = player.DurationSeconds;
                    if (duration > 0 && player.PositionSeconds >= duration - 0.1)
                    {
                        player.Seek(0);
                        _position = 0;
                    }
                    StartPlaybackCore();
                }
                return PublishReady();
            }
        }
        finally
        {
            _gate.Release();
        }
        return await PrepareAudioAsync(autoplay: true, cancellationToken);
    }

    /// <summary>Source SetPlaybackSpeed: stop, prepare the sentence again at the new speed, then play.</summary>
    public async Task<NativeShadowingState> SetSpeedAsync(
        float speed, CancellationToken cancellationToken = default)
    {
        if (speed is not (NativeShadowingSpeed.Slow or NativeShadowingSpeed.Normal))
            throw new ArgumentOutOfRangeException(nameof(speed));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_buffering)
                return State;
            _speed = speed;
            StopAudioCore();
            _position = 0;
        }
        finally
        {
            _gate.Release();
        }
        return await PrepareAudioAsync(autoplay: true, cancellationToken);
    }

    /// <summary>Source waveform seek (native player only).</summary>
    public async Task<NativeShadowingState> SeekAsync(
        double fraction, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (!player.IsLoaded || !double.IsFinite(fraction))
                return State;
            _position = Math.Clamp(fraction, 0, 1);
            player.Seek(_position);
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<NativeShadowingState> NextAsync(CancellationToken cancellationToken = default) =>
        MoveAsync(+1, cancellationToken);

    public Task<NativeShadowingState> PreviousAsync(CancellationToken cancellationToken = default) =>
        MoveAsync(-1, cancellationToken);

    public async Task<NativeShadowingState> ToggleTranslationAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_sentences[_index].HasTranslation)
                _showTranslation = !_showTranslation;
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Go Back: stop audio, stop and save the timer, then return to the Dashboard.</summary>
    public async Task<NativeShadowingState> ExitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _exited = true;
            StopAudioCore();
            string? errorKey = null;
            try
            {
                if (await timer.StopAsync(cancellationToken) == NativeActivityStop.Failed)
                    errorKey = NativeText.SaveFailed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Shadowing could not save its activity minutes.");
                errorKey = NativeText.SaveFailed;
            }
            return Publish(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                ErrorKey: errorKey,
                HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId)));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeShadowingState> MoveAsync(
        int step, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            var next = _index + step;
            if (next < 0 || next >= _sentences.Length)
                return State;
            StopAudioCore();
            _index = next;
            ResetSentenceCore();
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }
        return await PrepareAudioAsync(autoplay: false, cancellationToken);
    }

    /// <summary>
    /// Source GenerateAudioForCurrent: buffering disables Slow/Play/Normal while
    /// the owner's cached clip or a synthesized clip is fetched. A newer sentence,
    /// speed change or exit supersedes an older fetch.
    /// </summary>
    private async Task<NativeShadowingState> PrepareAudioAsync(
        bool autoplay, CancellationToken cancellationToken)
    {
        long version;
        string userId;
        string text;
        CancellationTokenSource load;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            _audioLoad?.Cancel();
            _audioLoad?.Dispose();
            load = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
            _audioLoad = load;
            version = ++_audioVersion;
            userId = _launch!.UserId;
            text = _sentences[_index].TargetText;
            _buffering = true;
            _issue = NativeShadowingAudioIssue.None;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        NativeSpeechClip clip;
        try
        {
            clip = await speech.GetAsync(userId, text, _language, load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return State;
        }
        catch (Exception ex)
        {
            logger.LogError("Shadowing audio could not be prepared: {Failure}.", ex.GetType().Name);
            clip = new(NativeSpeechClipStatus.Failed);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (version != _audioVersion || _state.Destination != NativeDestination.Activity
                || _state.Phase != NativePhase.Ready)
                return State;
            _buffering = false;
            if (!owner.IsActive(userId))
                return Refuse();
            if (clip.Status == NativeSpeechClipStatus.Ready && clip.Audio is { } audio
                && player.Load(audio))
            {
                _position = 0;
                if (autoplay)
                    StartPlaybackCore();
            }
            else
            {
                // Source logs the failure; Play then tries to prepare the audio again.
                player.Stop();
                _issue = clip.Status == NativeSpeechClipStatus.Offline
                    ? NativeShadowingAudioIssue.Offline : NativeShadowingAudioIssue.Failed;
            }
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartPlaybackCore()
    {
        if (!player.Play(_speed))
        {
            _playing = false;
            _issue = NativeShadowingAudioIssue.Failed;
            return;
        }
        _playing = true;
        _issue = NativeShadowingAudioIssue.None;
        StartTicker();
    }

    private void StartTicker()
    {
        StopTicker();
        var ticker = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ticker = ticker;
        _ = TickAsync(ticker.Token);
    }

    private void StopTicker()
    {
        _ticker?.Cancel();
        _ticker?.Dispose();
        _ticker = null;
    }

    private async Task TickAsync(CancellationToken token)
    {
        try
        {
            using var periodic = new PeriodicTimer(TickInterval);
            while (await periodic.WaitForNextTickAsync(token))
            {
                await _gate.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested || !_playing)
                        return;
                    if (_launch is null || !owner.IsActive(_launch.UserId))
                    {
                        Refuse();
                        return;
                    }
                    var duration = player.DurationSeconds;
                    if (duration > 0)
                    {
                        var current = player.PositionSeconds;
                        _position = Math.Clamp(current / duration, 0, 1);
                        // Source auto-stops when playback reaches the end.
                        if (current >= duration - 0.05 || !player.IsPlaying)
                        {
                            _playing = false;
                            _position = 1;
                            PublishReady();
                            return;
                        }
                    }
                    PublishReady();
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnPlayerEnded() => _ = HandleEndedAsync();

    private async Task HandleEndedAsync()
    {
        try
        {
            await _gate.WaitAsync(_lifetime.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (!_playing || _state.Phase != NativePhase.Ready
                || _state.Destination != NativeDestination.Activity)
                return;
            _playing = false;
            _position = 1;
            StopTicker();
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool CanCommand(out NativeShadowingState state)
    {
        if (_exited)
        {
            state = State;
            return false;
        }
        if (_launch is null || !owner.IsActive(_launch.UserId))
        {
            state = Refuse();
            return false;
        }
        state = State;
        return _state.Phase == NativePhase.Ready
            && _state.Destination == NativeDestination.Activity
            && !_sentences.IsDefaultOrEmpty;
    }

    private void ResetSentenceCore()
    {
        _showTranslation = false;
        _position = 0;
        _issue = NativeShadowingAudioIssue.None;
    }

    private void StopAudioCore()
    {
        _audioVersion++;
        _audioLoad?.Cancel();
        _audioLoad?.Dispose();
        _audioLoad = null;
        StopTicker();
        player.Stop();
        _playing = false;
        _buffering = false;
    }

    private NativeShadowingState PublishReady() => Publish(new(
        NativePhase.Ready,
        Sentences: _sentences,
        Index: _index,
        ShowTranslation: _showTranslation && _sentences[_index].HasTranslation,
        Speed: _speed,
        IsBuffering: _buffering,
        IsPlaying: _playing,
        HasAudio: player.IsLoaded,
        Position: _position,
        DurationSeconds: player.IsLoaded ? player.DurationSeconds : 0,
        AudioIssue: _issue,
        HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId)));

    private NativeShadowingState Refuse()
    {
        StopAudioCore();
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeShadowingText.Unavailable));
    }

    private NativeShadowingState Publish(NativeShadowingState state)
    {
        _state = state;
        StateChanged?.Invoke(State);
        return State;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        if (_subscribed)
            player.Ended -= OnPlayerEnded;
        StopTicker();
        _audioLoad?.Dispose();
        player.Stop();
        player.Dispose();
        timer.Dispose();
    }
}
