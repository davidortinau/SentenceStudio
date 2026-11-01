using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// How Do You Say (source UI/Pages/HowDoYouSay.razor, activities.md 13): type a
/// target-language phrase and hear it spoken. Choose My Own only; the source
/// starts no timer and writes no plan, progress or UserActivity row, so none is
/// written here. Speech uses the native TTS path (owner's StreamHistory cache
/// keyed by phrase and voice, else /api/v1/speech/synthesize, cached for next
/// time). The voice list comes from the API for the profile's target language
/// and a selection is saved immediately per language (SpeechVoicePreferences).
/// History lists the owner's StreamHistory rows newest first with play/pause
/// and delete. Unlike the source, every read, write and request is bound to the
/// launch owner and refused when that owner changes.
/// </summary>
public sealed class NativeHowDoYouSayPresenter(
    NativeActivityOwner owner,
    UserProfileRepository profiles,
    StreamHistoryRepository history,
    SpeechVoicePreferences voicePreferences,
    IFileSystemService files,
    INativeShadowingSpeech speech,
    INativeShadowingPlayer player,
    ILogger<NativeHowDoYouSayPresenter> logger,
    INativeQuizSpeechSource? voiceSource = null) : IDisposable
{
    private const int MaxAudioBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private NativeActivityLaunch? _launch;
    private NativeHowDoYouSayState _state = new(NativePhase.Idle);
    private ImmutableArray<StreamHistory> _rows = [];
    private ImmutableArray<NativeHowDoYouSayVoice> _voices = [];
    private string _language = "Korean";
    private string _input = string.Empty;
    private string _voiceId = string.Empty;
    private bool _voicesLoading;
    private bool _historyLoading;
    private bool _busy;
    private int? _playingId;
    private NativeHowDoYouSayNotice? _notice;
    private bool _subscribed;
    private bool _exited;
    private bool _disposed;

    public NativeHowDoYouSayState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeHowDoYouSayText.Unavailable)
            : _state;

    public event Action<NativeHowDoYouSayState>? StateChanged;

    public async Task<NativeHowDoYouSayState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        string userId;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null)
                return State;
            _launch = launch;
            userId = launch.UserId;
            Publish(new(NativePhase.Loading));
            if (launch.Route != NativeActivityCatalog.HowDoYouSay || !owner.IsActive(userId))
                return Refuse();
            if (!_subscribed)
            {
                player.Ended += OnPlayerEnded;
                _subscribed = true;
            }
            var profile = await profiles.GetByIdAsync(userId, cancellationToken);
            if (!owner.IsActive(userId))
                return Refuse();
            _language = string.IsNullOrWhiteSpace(profile?.TargetLanguage)
                ? "Korean" : profile!.TargetLanguage!;
            _voicesLoading = true;
            _historyLoading = true;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        // Source OnInitializedAsync: voices and history load together, outside the
        // lock and cancelled by Back so a hanging voice request never delays Exit.
        using var load = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        var voices = LoadVoicesAsync(userId, load.Token);
        var rows = LoadHistoryAsync(load.Token);
        try
        {
            await Task.WhenAll(voices, rows);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return State;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            (_voices, _voiceId) = voices.Result;
            _rows = rows.Result;
            _voicesLoading = false;
            _historyLoading = false;
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stores typed text without publishing, so a re-render cannot interrupt IME composition.</summary>
    public void SetInput(string? value)
    {
        if (_state.Phase == NativePhase.Ready && !_exited)
            _input = value ?? string.Empty;
    }

    /// <summary>Source OnVoiceChanged: the selection is saved immediately for the target language.</summary>
    public async Task<NativeHowDoYouSayState> SelectVoiceAsync(
        string voiceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (string.IsNullOrWhiteSpace(voiceId) || !_voices.Any(voice => voice.Id == voiceId))
                return State;
            _voiceId = voiceId;
            voicePreferences.SetVoiceForLanguage(_language, voiceId);
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source SubmitPhrase (Speak button or Enter): needs a voice, speaks the phrase,
    /// plays it, adds it to the top of the history and clears the input.
    /// </summary>
    public async Task<NativeHowDoYouSayState> SpeakAsync(CancellationToken cancellationToken = default)
    {
        string userId;
        string phrase;
        string voiceId;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_busy || string.IsNullOrWhiteSpace(_input))
                return State;
            _notice = null;
            if (string.IsNullOrWhiteSpace(_voiceId))
            {
                _notice = new(NativeHowDoYouSayNoticeKind.Warning, NativeHowDoYouSayText.SelectVoice);
                return PublishReady();
            }
            userId = _launch!.UserId;
            phrase = _input.Trim();
            voiceId = _voiceId;
            _busy = true;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        NativeSpeechClip clip;
        StreamHistory? row = null;
        ImmutableArray<StreamHistory>? rows = null;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        try
        {
            // The speech path speaks with the language's saved voice, which is the selection.
            clip = await speech.GetAsync(userId, phrase, _language, request.Token);
            if (clip.Status == NativeSpeechClipStatus.Ready && owner.IsActive(userId))
            {
                row = await history.GetStreamHistoryByPhraseAndVoiceAsync(phrase, voiceId);
                rows = await LoadHistoryAsync(request.Token);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            _busy = false;
            return State;
        }
        catch (Exception ex)
        {
            // The phrase is learner content; only the failure type is logged.
            logger.LogError("How Do You Say speech failed: {Failure}.", ex.GetType().Name);
            clip = new(NativeSpeechClipStatus.Failed);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _busy = false;
            if (!CanCommand(out var refused))
                return refused;
            switch (clip.Status)
            {
                case NativeSpeechClipStatus.Offline:
                    _notice = new(NativeHowDoYouSayNoticeKind.Warning, NativeHowDoYouSayText.AudioOffline);
                    return PublishReady();
                case NativeSpeechClipStatus.Failed:
                    _notice = new(NativeHowDoYouSayNoticeKind.Error, NativeHowDoYouSayText.GenerateFailed);
                    return PublishReady();
            }
            if (rows is { } loaded)
                _rows = loaded;
            StopAudioCore();
            if (clip.Audio is { Length: > 0 } audio && player.Load(audio) && player.Play(1.0f))
            {
                _playingId = row?.Id;
                _notice = new(NativeHowDoYouSayNoticeKind.Success, NativeHowDoYouSayText.Generated);
            }
            else
            {
                player.Stop();
                _notice = new(NativeHowDoYouSayNoticeKind.Error, NativeHowDoYouSayText.PlaybackError);
            }
            _input = string.Empty;
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source PlayHistoryItem: the playing row pauses (stops); any other row plays from its cached file.</summary>
    public async Task<NativeHowDoYouSayState> TogglePlayAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            _notice = null;
            if (_playingId == id && player.IsPlaying)
            {
                StopAudioCore();
                return PublishReady();
            }
            StopAudioCore();
            var row = _rows.FirstOrDefault(item => item.Id == id);
            if (row is null)
                return PublishReady();
            if (CachedAudioPath(row.AudioFilePath) is not { } path)
            {
                _notice = new(NativeHowDoYouSayNoticeKind.Warning, NativeHowDoYouSayText.FileNotFound);
                return PublishReady();
            }
            try
            {
                var audio = await ReadLimitedAsync(path, cancellationToken);
                if (!owner.IsActive(_launch!.UserId))
                    return Refuse();
                if (audio is { Length: > 0 } && player.Load(audio) && player.Play(1.0f))
                    _playingId = id;
                else
                {
                    player.Stop();
                    _notice = new(NativeHowDoYouSayNoticeKind.Error, NativeHowDoYouSayText.PlaybackError);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("How Do You Say could not read a cached clip: {Failure}.", ex.GetType().Name);
                _notice = new(NativeHowDoYouSayNoticeKind.Error, NativeHowDoYouSayText.PlaybackError);
            }
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source DeleteHistoryItem: removes the owner's row and then its cached file.
    /// The file is removed only inside AudioCache and only when no other owned row uses it.
    /// </summary>
    public async Task<NativeHowDoYouSayState> DeleteAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            _notice = null;
            var row = _rows.FirstOrDefault(item => item.Id == id);
            if (row is null)
                return PublishReady();
            if (_playingId == id)
                StopAudioCore();
            int deleted;
            try
            {
                deleted = await history.DeleteStreamHistoryAsync(row);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("How Do You Say could not delete a history row: {Failure}.", ex.GetType().Name);
                deleted = -1;
            }
            if (!owner.IsActive(_launch!.UserId))
                return Refuse();
            if (deleted <= 0)
            {
                _notice = new(NativeHowDoYouSayNoticeKind.Error, NativeHowDoYouSayText.DeleteFailed);
                return PublishReady();
            }
            _rows = _rows.Remove(row);
            var path = CachedAudioPath(row.AudioFilePath);
            var name = path is null ? null : Path.GetFileName(path);
            if (path is not null && !_rows.Any(other =>
                    string.Equals(Path.GetFileName(other.AudioFilePath ?? string.Empty), name,
                        StringComparison.Ordinal)))
            {
                // Cleanup is separate from the completed delete (source ignores file errors).
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("How Do You Say could not remove a cached clip: {Failure}.", ex.GetType().Name);
                }
            }
            _notice = new(NativeHowDoYouSayNoticeKind.Success, NativeHowDoYouSayText.Deleted);
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Go Back: stop audio and return to the Dashboard. No timer runs.</summary>
    public async Task<NativeHowDoYouSayState> ExitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _exited = true;
            _lifetime.Cancel();
            StopAudioCore();
            return Publish(new(NativePhase.Ready, NativeDestination.TodaysPlan));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(ImmutableArray<NativeHowDoYouSayVoice>, string)> LoadVoicesAsync(
        string userId, CancellationToken cancellationToken)
    {
        ImmutableArray<NativeHowDoYouSayVoice> options = [];
        try
        {
            if (voiceSource is not null)
            {
                var found = await voiceSource.GetVoicesAsync(userId, _language, cancellationToken);
                options = found
                    .Where(voice => !string.IsNullOrWhiteSpace(voice.Id))
                    .Select(voice => new NativeHowDoYouSayVoice(voice.Id,
                        string.IsNullOrWhiteSpace(voice.Gender)
                            ? voice.Name : $"{voice.Name} ({voice.Gender})"))
                    .ToImmutableArray();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            // Source logs and shows no dropdown; the saved voice still speaks.
            logger.LogWarning("How Do You Say could not load voices: {Failure}.", ex.GetType().Name);
        }
        var selected = voicePreferences.GetVoiceForLanguage(_language);
        if (string.IsNullOrEmpty(selected) && !options.IsDefaultOrEmpty)
            selected = options[0].Id;
        return (options, selected);
    }

    private async Task<ImmutableArray<StreamHistory>> LoadHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Owner-scoped: rows cached by every activity for this owner, newest first.
            var rows = await history.GetAllStreamHistoryAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.Phrase))
                .OrderByDescending(row => row.CreatedAt)
                .ToImmutableArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("How Do You Say could not load history: {Failure}.", ex.GetType().Name);
            return [];
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
            if (_playingId is null || _exited || _state.Phase != NativePhase.Ready)
                return;
            _playingId = null;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The cached clip inside today's AudioCache. iOS moves the sandbox between
    /// installs, so a stored absolute path is resolved by file name; nothing
    /// outside that directory is read or removed.
    /// </summary>
    private string? CachedAudioPath(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        try
        {
            var directory = Path.GetFullPath(Path.Combine(files.AppDataDirectory, "AudioCache"));
            var name = Path.GetFileName(stored);
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var path = Path.GetFullPath(Path.Combine(directory, name));
            return path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && File.Exists(path)
                && new FileInfo(path).Length is > 0 and <= MaxAudioBytes
                    ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxAudioBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private bool CanCommand(out NativeHowDoYouSayState state)
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
            && _state.Destination == NativeDestination.Activity;
    }

    private void StopAudioCore()
    {
        player.Stop();
        _playingId = null;
    }

    private NativeHowDoYouSayState PublishReady() => Publish(new(
        NativePhase.Ready,
        TargetLanguage: _language,
        Input: _input,
        VoicesLoading: _voicesLoading,
        Voices: _voices,
        SelectedVoiceId: _voiceId,
        IsBusy: _busy,
        HistoryLoading: _historyLoading,
        History: _rows.Select(row => new NativeHowDoYouSayEntry(
            row.Id, row.Phrase ?? string.Empty,
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc).ToLocalTime())).ToImmutableArray(),
        PlayingId: _playingId,
        Notice: _notice));

    private NativeHowDoYouSayState Refuse()
    {
        StopAudioCore();
        _busy = false;
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeHowDoYouSayText.Unavailable));
    }

    private NativeHowDoYouSayState Publish(NativeHowDoYouSayState state)
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
        player.Stop();
        player.Dispose();
    }
}
