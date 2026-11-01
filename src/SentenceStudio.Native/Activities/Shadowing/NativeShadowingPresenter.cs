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
    private Task? _audioTask;
    private readonly NativeActivityLoad _load = new();
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
        NativeActivityLoadTicket ticket;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null || _exited)
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
            ticket = _load.Begin(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        // The AI sentence load runs off the gate so Back can stop and save at once.
        NativeShadowingContentResult? result = null;
        Exception? failure = null;
        try
        {
            result = await content.LoadAsync(launch.UserId,
                launch.ResourceIds.IsDefaultOrEmpty ? null : launch.ResourceIds[0],
                ticket.Token);
        }
        catch (OperationCanceledException) when (ticket.Token.IsCancellationRequested)
        {
            return State;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_exited || !_load.IsCurrent(ticket))
                return State;
            if (failure is not null || result is null)
            {
                logger.LogError(failure, "Shadowing could not load its sentences.");
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
            // Source: the first sentence's audio is prepared right after loading,
            // without autoplay; navigation and Back stay available meanwhile.
            return StartAudioCore(autoplay: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The current audio preparation (completed when none is running); tests await it.</summary>
    public Task AudioTask => _audioTask ?? Task.CompletedTask;

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
            return StartAudioCore(autoplay: true);
        }
        finally
        {
            _gate.Release();
        }
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
            return StartAudioCore(autoplay: true);
        }
        finally
        {
            _gate.Release();
        }
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
        // A sentence load in flight never holds the gate; cancel it so Back works at once.
        _load.Cancel();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _load.Cancel();
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
            return StartAudioCore(autoplay: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source GenerateAudioForCurrent: buffering disables Slow/Play/Normal while
    /// the owner's cached clip or a synthesized clip is fetched. Called under the
    /// gate; the fetch runs in the background (<see cref="AudioTask"/>) so the
    /// command returns at once and Next/Previous/Back are never held by it. A
    /// newer sentence, speed change or exit supersedes an older fetch.
    /// </summary>
    private NativeShadowingState StartAudioCore(bool autoplay)
    {
        if (!CanCommand(out var refused))
            return refused;
        _audioLoad?.Cancel();
        _audioLoad?.Dispose();
        // Only the presenter lifetime: a command's caller token must not abandon
        // the fetch half way and leave the controls buffering.
        var load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _audioLoad = load;
        var version = ++_audioVersion;
        var userId = _launch!.UserId;
        var text = _sentences[_index].TargetText;
        _buffering = true;
        _issue = NativeShadowingAudioIssue.None;
        var state = PublishReady();
        _audioTask = FinishAudioAsync(autoplay, version, userId, text, load.Token);
        return state;
    }

    private async Task FinishAudioAsync(
        bool autoplay, long version, string userId, string text, CancellationToken token)
    {
        try
        {
            await PrepareAudioAsync(autoplay, version, userId, text, token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Shadowing audio preparation failed unexpectedly.");
        }
    }

    private async Task<NativeShadowingState> PrepareAudioAsync(
        bool autoplay, long version, string userId, string text, CancellationToken token)
    {
        // Leave the gate held by StartAudioCore before fetching.
        await Task.Yield();
        NativeSpeechClip clip;
        try
        {
            clip = await speech.GetAsync(userId, text, _language, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return State;
        }
        catch (Exception ex)
        {
            logger.LogError("Shadowing audio could not be prepared: {Failure}.", ex.GetType().Name);
            clip = new(NativeSpeechClipStatus.Failed);
        }

        // Never the caller's token: an abandoned wait would leave the controls buffering.
        await _gate.WaitAsync(CancellationToken.None);
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
        _load.Dispose();
        if (_subscribed)
            player.Ended -= OnPlayerEnded;
        StopTicker();
        _audioLoad?.Dispose();
        player.Stop();
        player.Dispose();
        timer.Dispose();
    }
}
