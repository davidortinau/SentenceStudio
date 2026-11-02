using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>
/// Reading (source UI/Pages/Reading.razor + UI/Shared/InteractiveText.razor,
/// activities.md 10). The owned resource transcript is split into sentences
/// and tokenized into tappable words; resource vocabulary and plan focus words
/// are highlighted. Timestamped TTS for the whole transcript loads in the
/// background and drives the current-sentence highlight. Word taps open the
/// lookup sheet (owned vocabulary first, then the source Translate prompt) and
/// can save the word to the resource. Back stops audio and the timer and
/// records a "Reading session" UserActivity. Unlike the source, every read and
/// write is bound to the launch owner and refused when that owner is absent or
/// no longer active; the source's unowned, inverted "translation cache" word
/// (TranslationService.TranslateAsync) is not written.
/// </summary>
public sealed class NativeReadingPresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    LearningResourceRepository resources,
    UserActivityRepository activities,
    IPreferencesService preferences,
    INativeReadingAudioSource audioSource,
    IAudioPlayerFactory players,
    NativePendingWrites pendingWrites,
    ILoggerFactory loggers,
    IOwnerBoundNativeAiGateway? ai = null) : IDisposable
{
    public const int DefaultFontSize = 20;
    public const int MinimumFontSize = 14;
    public const int MaximumFontSize = 40;
    public const string FontSizeKey = "ReadingActivity_FontSize";
    public const string PlaybackSpeedKey = "ReadingActivity_PlaybackSpeed";
    public const string JumpHintKey = "ReadingActivity_HasShownJumpHint";
    /// <summary>Source sentinel for a failed lookup; heads show it localized.</summary>
    public const string TranslationUnavailableMarker = "(translation unavailable)";
    public static ImmutableArray<float> PlaybackSpeeds { get; } = [0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f];

    private const string TranslateTemplate = "SentenceStudio.Native.Prompts.Translate.scriban-txt";
    private static readonly char[] TrailingPunctuation = ['.', ',', '!', '?', ':', ';', '"', '\''];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeActivityExit _exit = new();
    private readonly ILogger<NativeReadingPresenter> _logger = loggers.CreateLogger<NativeReadingPresenter>();
    private NativeActivityLaunch? _launch;
    private LearningResource? _resource;
    private List<VocabularyWord> _vocabulary = [];
    private HashSet<string> _focusIds = new(StringComparer.Ordinal);
    private List<string> _sentenceTexts = [];
    private ImmutableArray<NativeReadingSentence> _sentences = [];
    private NativePhase _phase = NativePhase.Idle;
    private NativeDestination _destination = NativeDestination.Activity;
    private string? _errorKey;
    private NativeMessage? _notice;
    private NativeReadingWordPanel? _panel;
    private long _lookup;
    private int _current = -1;
    private bool _generating;
    private string? _audioStatusKey;
    private string? _audioStatusDetail;
    private double _audioProgress;
    private bool _audioLoaded;
    private bool _playing;
    private double _time;
    private double _duration;
    private int _fontSize = DefaultFontSize;
    private float _speed = 1.0f;
    private bool _hasShownJumpHint;
    private TimestampedAudioManager? _audio;
    private Task? _audioTask;
    private bool _exited;
    private bool _disposed;
    private NativeReadingState _state = new(NativePhase.Idle);

    /// <summary>The Back/Exit fence (tests force continuation ordering through it).</summary>
    internal NativeActivityExit ExitFence => _exit;

    public NativeReadingState State
    {
        get
        {
            lock (_sync)
                return _launch is not null && !owner.IsActive(_launch.UserId)
                    && _state.Destination != NativeDestination.TodaysPlan
                    ? new(NativePhase.Blocked, ErrorKey: NativeReadingText.Unavailable)
                    : _state;
        }
    }

    public event Action<NativeReadingState>? StateChanged;

    /// <summary>The background audio generation, for tests and teardown.</summary>
    public Task AudioTask => _audioTask ?? Task.CompletedTask;

    public async Task<NativeReadingState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null)
                return State;
            _launch = launch;
            _fontSize = ClampFontSize((int)preferences.Get(FontSizeKey, (double)DefaultFontSize));
            _speed = preferences.Get(PlaybackSpeedKey, 1.0f);
            _hasShownJumpHint = preferences.Get(JumpHintKey, false);
            SetPhase(NativePhase.Loading);
            if (launch.Route != NativeActivityCatalog.Reading
                || !owner.IsActive(launch.UserId)
                || !await timer.StartAsync(launch, cancellationToken))
                return Refuse();
            var loaded = await LoadCoreAsync(launch, cancellationToken);
            if (loaded)
                _audioTask = InitializeAudioAsync(launch.UserId, _resource!, _lifetime.Token);
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> LoadCoreAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        var userId = launch.UserId;
        var resourceId = launch.ResourceIds.IsDefaultOrEmpty ? null : launch.ResourceIds[0];
        // Source LoadResource returns silently without a resource id: the
        // page then shows "No content" with Go Back.
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            SetPhase(NativePhase.Empty, NativeReadingText.NoContent);
            return false;
        }
        try
        {
            var resource = await resources.GetResourceAsync(resourceId, userId);
            if (!owner.IsActive(userId))
            {
                Refuse();
                return false;
            }
            if (resource is null || resource.UserProfileId != userId)
            {
                SetPhase(NativePhase.Error, NativeReadingText.ResourceNotFound);
                return false;
            }
            lock (_sync)
            {
                _resource = resource;
                _vocabulary = resource.Vocabulary?.ToList() ?? [];
            }
            if (launch.FocusVocabularyIds.IsDefaultOrEmpty)
                _logger.LogInformation("Reading launched without FocusVocabularyIds; no focus highlight.");
            else
                lock (_sync)
                    _focusIds = FocusVocabularySelection
                        .SelectFocusWords(_vocabulary, launch.FocusVocabularyIds)
                        .Select(word => word.Id).ToHashSet(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(resource.Transcript))
            {
                SetPhase(NativePhase.Error, NativeReadingText.NoTranscript);
                return false;
            }
            var sentences = SentenceTimingCalculator.SplitIntoSentences(resource.Transcript)
                .Where(sentence => sentence != "PARAGRAPH_BREAK").ToList();
            if (sentences.Count == 0)
            {
                SetPhase(NativePhase.Empty, NativeReadingText.NoContent);
                return false;
            }
            lock (_sync)
            {
                _sentenceTexts = sentences;
                RebuildTokens();
                _phase = NativePhase.Ready;
                _errorKey = null;
            }
            Publish();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reading could not load its resource.");
            SetPhase(NativePhase.Error, NativeReadingText.LoadFailed);
            return false;
        }
    }

    private async Task InitializeAudioAsync(string userId, LearningResource resource, CancellationToken cancellationToken)
    {
        await Task.Yield();
        UpdateAudioStatus(NativeReadingText.AudioInitializing, 0.1, generating: true);
        TimestampedAudioManager? manager = null;
        try
        {
            UpdateAudioStatus(NativeReadingText.AudioGenerating, 0.3, generating: true);
            var audio = await audioSource.GetAsync(userId, resource, cancellationToken);
            if (!owner.IsActive(userId) || cancellationToken.IsCancellationRequested)
                return;
            if (audio is null || audio.AudioData.Length == 0)
            {
                _logger.LogWarning("Reading timestamped audio was unavailable.");
                FinishAudio(NativeReadingText.AudioFailed, null);
                return;
            }
            UpdateAudioStatus(NativeReadingText.AudioProcessing, 0.7, generating: true);
            manager = new TimestampedAudioManager(
                new SentenceTimingCalculator(loggers.CreateLogger<SentenceTimingCalculator>()),
                players, loggers.CreateLogger<TimestampedAudioManager>());
            UpdateAudioStatus(NativeReadingText.AudioLoading, 0.9, generating: true);
            await manager.LoadAudioAsync(audio);
            if (!manager.HasNativePlayer)
            {
                manager.Dispose();
                manager = null;
                FinishAudio(NativeReadingText.AudioFailed, null);
                return;
            }
            lock (_sync)
            {
                if (_disposed || _exited || !owner.IsActive(userId))
                {
                    manager.Dispose();
                    return;
                }
                _audio = manager;
                manager.SentenceChanged += OnSentenceChanged;
                manager.ProgressUpdated += OnProgressUpdated;
                manager.PlaybackEnded += OnPlaybackEnded;
                var audioSentences = manager.GetSentences()
                    .Where(sentence => sentence != "PARAGRAPH_BREAK").ToList();
                if (audioSentences.Count > 0 && !audioSentences.SequenceEqual(_sentenceTexts))
                {
                    _sentenceTexts = audioSentences;
                    RebuildTokens();
                }
                _duration = audio.Duration;
                _audioLoaded = true;
                _generating = false;
                _audioProgress = 0;
                _audioStatusKey = null;
                _audioStatusDetail = null;
                manager = null;
            }
            _logger.LogInformation("Reading audio loaded: {Duration:F1}s.", audio.Duration);
            Publish();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading could not initialize its audio.");
            manager?.Dispose();
            FinishAudio(NativeReadingText.AudioError, ex.Message);
        }
    }

    private void UpdateAudioStatus(string key, double progress, bool generating)
    {
        lock (_sync)
        {
            _generating = generating;
            _audioStatusKey = key;
            _audioStatusDetail = null;
            _audioProgress = progress;
        }
        Publish();
    }

    /// <summary>
    /// The source keeps its failure text in the banner, which is hidden once
    /// generation stops, so a failure was never visible. Native shows it as a
    /// status line; reading still works without audio.
    /// </summary>
    private void FinishAudio(string key, string? detail)
    {
        lock (_sync)
        {
            _generating = false;
            _audioProgress = 0;
            _audioStatusKey = key;
            _audioStatusDetail = detail;
        }
        Publish();
    }

    private void OnSentenceChanged(object? sender, int index)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _audio) || _exited || index == _current
                || index < 0 || index >= _sentenceTexts.Count)
                return;
            _current = index;
        }
        Publish();
    }

    private void OnProgressUpdated(object? sender, double time)
    {
        bool changed;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _audio) || _exited)
                return;
            // The display shows whole seconds: publish only when that changes.
            changed = Math.Floor(time) != Math.Floor(_time);
            _time = time;
        }
        if (changed)
            Publish();
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _audio))
                return;
            _playing = false;
            _current = -1;
        }
        Publish();
    }

    public Task<NativeReadingState> TogglePlaybackAsync(CancellationToken cancellationToken = default) =>
        CommandAsync(async () =>
        {
            TimestampedAudioManager audio;
            lock (_sync)
            {
                if (_generating)
                {
                    _notice = NativeMessage.Error(NativeReadingText.AudioStillLoading);
                    return;
                }
                if (!_audioLoaded || _audio is null)
                {
                    _notice = NativeMessage.Error(NativeReadingText.NoAudio);
                    return;
                }
                audio = _audio;
                if (_playing)
                {
                    audio.Pause();
                    _playing = false;
                    return;
                }
            }
            int start;
            lock (_sync)
                start = _current >= 0 ? _current : 0;
            await audio.PlayFromSentenceAsync(start);
            lock (_sync)
            {
                _playing = true;
                _current = start;
            }
        }, cancellationToken);

    public Task<NativeReadingState> PreviousSentenceAsync(CancellationToken cancellationToken = default) =>
        MoveAsync(-1, cancellationToken);

    public Task<NativeReadingState> NextSentenceAsync(CancellationToken cancellationToken = default) =>
        MoveAsync(1, cancellationToken);

    private Task<NativeReadingState> MoveAsync(int delta, CancellationToken cancellationToken) =>
        CommandAsync(async () =>
        {
            TimestampedAudioManager? audio;
            int target;
            lock (_sync)
            {
                if (_audio is null || !_audioLoaded)
                    return;
                target = _current + delta;
                if (delta < 0 ? _current <= 0 : _current >= _sentenceTexts.Count - 1)
                    return;
                _current = target;
                audio = _playing ? _audio : null;
            }
            if (audio is not null)
                await audio.PlayFromSentenceAsync(target);
        }, cancellationToken);

    /// <summary>
    /// Source double-click on a sentence: play from it, with a one-time hint.
    /// </summary>
    public Task<NativeReadingState> JumpToSentenceAsync(int sentenceIndex, CancellationToken cancellationToken = default) =>
        CommandAsync(async () =>
        {
            TimestampedAudioManager audio;
            lock (_sync)
            {
                if (_audio is null || !_audioLoaded
                    || sentenceIndex < 0 || sentenceIndex >= _sentenceTexts.Count)
                    return;
                if (!_hasShownJumpHint)
                {
                    _notice = NativeMessage.Success(NativeReadingText.JumpHint);
                    _hasShownJumpHint = true;
                    preferences.Set(JumpHintKey, true);
                }
                _current = sentenceIndex;
                audio = _audio;
            }
            await audio.PlayFromSentenceAsync(sentenceIndex);
            lock (_sync)
                _playing = true;
        }, cancellationToken);

    /// <summary>Source font slider: 14..40 step 2, persisted as a double.</summary>
    public Task<NativeReadingState> SetFontSizeAsync(int size, CancellationToken cancellationToken = default) =>
        CommandAsync(() =>
        {
            lock (_sync)
                _fontSize = ClampFontSize(size);
            preferences.Set(FontSizeKey, (double)_fontSize);
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>
    /// Source speed select. The source persists the value but never applies it
    /// to playback (activities.md 19.5); native keeps that behavior.
    /// </summary>
    public Task<NativeReadingState> SetPlaybackSpeedAsync(float speed, CancellationToken cancellationToken = default) =>
        CommandAsync(() =>
        {
            if (!PlaybackSpeeds.Contains(speed))
                return Task.CompletedTask;
            lock (_sync)
                _speed = speed;
            preferences.Set(PlaybackSpeedKey, speed);
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<NativeReadingState> DismissNoticeAsync(CancellationToken cancellationToken = default) =>
        CommandAsync(() => Task.CompletedTask, cancellationToken);

    public Task<NativeReadingState> CloseWordPanelAsync(CancellationToken cancellationToken = default) =>
        CommandAsync(() =>
        {
            lock (_sync)
            {
                _panel = null;
                _lookup++;
            }
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>InteractiveText HandleWordClick: a vocabulary word shows its meaning; another word is looked up.</summary>
    public async Task<NativeReadingState> TapWordAsync(int tokenId, CancellationToken cancellationToken = default)
    {
        string userId;
        string cleanWord;
        string? context;
        long lookup;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryBegin(out userId))
                return State;
            var token = _sentences.SelectMany(sentence => sentence.Tokens)
                .FirstOrDefault(item => item.Id == tokenId);
            if (_phase != NativePhase.Ready || token is null || !token.IsWord)
                return State;
            VocabularyWord? local;
            lock (_sync)
            {
                if (token.IsVocabulary && token.VocabularyWordId is { } wordId
                    && _vocabulary.FirstOrDefault(word => word.Id == wordId) is { } vocabularyWord)
                {
                    _lookup++;
                    _panel = new(vocabularyWord.TargetLanguageTerm ?? string.Empty,
                        vocabularyWord.NativeLanguageTerm ?? string.Empty,
                        IsTranslating: false, IsInVocabulary: true, IsSaving: false);
                    return PublishState();
                }
                cleanWord = token.Text.Trim().TrimEnd(TrailingPunctuation);
                local = _vocabulary.FirstOrDefault(word =>
                    string.Equals(word.TargetLanguageTerm, cleanWord, StringComparison.OrdinalIgnoreCase));
                lookup = ++_lookup;
                if (local is not null && !string.IsNullOrEmpty(local.NativeLanguageTerm))
                {
                    _panel = new(cleanWord, local.NativeLanguageTerm, false, true, false);
                    return PublishState();
                }
                // Source context: the current sentence, when one is selected.
                context = _current >= 0 && _current < _sentenceTexts.Count
                    ? _sentenceTexts[_current] : null;
                _panel = new(cleanWord, string.Empty, IsTranslating: true, false, false);
            }
            Publish();
        }
        finally
        {
            _gate.Release();
        }

        string definition;
        var translated = false;
        try
        {
            definition = await TranslateAsync(userId, cleanWord, context, cancellationToken);
            translated = true;
            if (string.IsNullOrEmpty(definition))
                definition = TranslationUnavailableMarker;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            return await SettledStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading word lookup failed.");
            definition = TranslationUnavailableMarker;
        }

        // Source HandleWordClicked records every lookup whose translation
        // returned, even after the panel closed or another word was tapped; it
        // is not recorded after Back or for another owner, nor when the
        // translation failed or was cancelled.
        if (translated && !_exit.HasBegun && !IsExited())
        {
            try
            {
                await RecordActivityAsync(userId, $"Dictionary lookup: {cleanWord}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reading could not record a dictionary lookup.");
            }
        }

        // A later tap, close, exit or owner change supersedes the shown definition.
        bool show;
        lock (_sync)
        {
            show = lookup == _lookup && !_exited && !_exit.HasBegun && owner.IsActive(userId)
                && _panel is not null;
            if (show)
                _panel = _panel! with { Definition = definition, IsTranslating = false };
        }
        if (show)
            Publish();
        return await SettledStateAsync();
    }

    private bool IsExited()
    {
        lock (_sync)
            return _exited;
    }

    /// <summary>
    /// The state a lookup returns: once Back has begun, the state Back settles
    /// (never the pre-Back one); otherwise the current state.
    /// </summary>
    private async Task<NativeReadingState> SettledStateAsync() =>
        _exit.HasBegun ? await _exit.StateAsync(_gate, () => State) : State;


    /// <summary>
    /// Source TranslationService.TranslateAsync, owner-scoped: an owned word by
    /// target term, then by native term, then the source Translate prompt
    /// through the owner-bound AI gateway.
    /// </summary>
    private async Task<string> TranslateAsync(
        string userId, string text, string? context, CancellationToken cancellationToken)
    {
        var byTarget = await resources.GetWordByTargetTermAsync(text, userId);
        if (!string.IsNullOrEmpty(byTarget?.NativeLanguageTerm))
            return byTarget.NativeLanguageTerm;
        var byNative = await resources.GetWordByNativeTermAsync(text, userId);
        if (!string.IsNullOrEmpty(byNative?.TargetLanguageTerm))
            return byNative.TargetLanguageTerm;
        if (ai is null || !owner.IsActive(userId))
            return string.Empty;
        using var stream = typeof(NativeReadingPresenter).Assembly
            .GetManifestResourceStream(TranslateTemplate)
            ?? throw new InvalidOperationException("The source Translate template is missing.");
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException("The source Translate template is invalid.");
        var prompt = await template.RenderAsync(new
        {
            user_input = text,
            context = context ?? string.Empty
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        return (await ai.SendPromptAsync<string>(userId, prompt, linked.Token))?.Trim() ?? string.Empty;
    }

    /// <summary>"Remember this word": create (or reuse) the owner's word and attach it to the resource.</summary>
    public async Task<NativeReadingState> SaveWordAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryBegin(out var userId))
                return State;
            NativeReadingWordPanel panel;
            LearningResource resource;
            lock (_sync)
            {
                if (_panel is not { CanSave: true } current || _resource is null)
                    return PublishState();
                panel = current;
                resource = _resource;
                _panel = current with { IsSaving = true };
            }
            Publish();
            try
            {
                var status = await resources.CreateAndAttachOwnedVocabularyAsync(
                    resource.Id,
                    new VocabularyWord
                    {
                        TargetLanguageTerm = panel.Word,
                        NativeLanguageTerm = panel.Definition
                    },
                    string.IsNullOrWhiteSpace(resource.Language) ? null! : resource.Language,
                    userId, cancellationToken);
                if (!owner.IsActive(userId))
                    return Refuse();
                if (status != ResourceVocabularyWriteStatus.Success)
                {
                    _logger.LogWarning("Reading could not save a word: {Status}.", status);
                    lock (_sync)
                    {
                        _panel = _panel is null ? null : _panel with { IsSaving = false };
                        _notice = NativeMessage.Error(NativeReadingText.CouldNotSaveWord);
                    }
                    return Publish();
                }
                pendingWrites.MarkPending(userId);
                var reloaded = await resources.GetResourceAsync(resource.Id, userId);
                lock (_sync)
                {
                    if (reloaded?.Vocabulary is { } vocabulary)
                        _vocabulary = vocabulary.ToList();
                    RebuildTokens();
                    _panel = _panel is null ? null
                        : _panel with { IsSaving = false, IsInVocabulary = true };
                    _notice = NativeMessage.Success(NativeReadingText.WordSaved, panel.Word);
                }
                Publish();
                await RecordActivityAsync(userId, $"Added vocabulary: {panel.Word}");
                return State;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Reading failed to save a word.");
                lock (_sync)
                {
                    _panel = _panel is null ? null : _panel with { IsSaving = false };
                    _notice = NativeMessage.Error(NativeReadingText.ErrorSavingWord);
                }
                return Publish();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Go Back: stop audio and the timer, record the session, return to the Dashboard.</summary>
    public async Task<NativeReadingState> ExitAsync(CancellationToken cancellationToken = default)
    {
        var exited = false;
        _exit.Begin();
        try
        {
            await _exit.AfterCancelAsync();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_exited)
                {
                    exited = true;
                    return State;
                }
                LearningResource? resource;
                lock (_sync)
                {
                    _exited = exited = true;
                    _lookup++;
                    _audio?.Stop();
                    _playing = false;
                    resource = _resource;
                }
                string? errorKey = null;
                try
                {
                    if (await timer.StopAsync(cancellationToken) == NativeActivityStop.Failed)
                        errorKey = NativeText.SaveFailed;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Reading could not save its activity minutes.");
                    errorKey = NativeText.SaveFailed;
                }
                if (resource is not null && _launch is { } launch && owner.IsActive(launch.UserId)
                    && !await RecordActivityAsync(launch.UserId, $"Reading session: {resource.Title}"))
                    errorKey ??= NativeText.SaveFailed;
                lock (_sync)
                {
                    _destination = NativeDestination.TodaysPlan;
                    _phase = NativePhase.Ready;
                    _panel = null;
                    _notice = null;
                    _errorKey = errorKey;
                }
                return Publish();
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _exit.End(exited);
        }
    }

    private async Task<bool> RecordActivityAsync(string userId, string input)
    {
        if (!owner.IsActive(userId))
            return false;
        var saved = await activities.SaveAsync(NativeUserActivity.Create(
            userId, Activity.Reading, input, accuracy: 100, fluency: 100));
        if (saved > 0)
        {
            pendingWrites.MarkPending(userId);
            return true;
        }
        _logger.LogWarning("Reading could not record a UserActivity row.");
        return false;
    }

    private async Task<NativeReadingState> CommandAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryBegin(out _))
                return State;
            if (_phase != NativePhase.Ready)
                return PublishState();
            await action();
            return Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears the previous toast and refuses an exited or unowned session.</summary>
    private bool TryBegin(out string userId)
    {
        userId = string.Empty;
        if (_exited || _launch is null)
            return false;
        if (!owner.IsActive(_launch.UserId))
        {
            Refuse();
            return false;
        }
        userId = _launch.UserId;
        lock (_sync)
            _notice = null;
        return true;
    }

    private NativeReadingState PublishState()
    {
        Publish();
        return State;
    }

    private void RebuildTokens()
    {
        var byTerm = new Dictionary<string, VocabularyWord>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in _vocabulary)
            if (!string.IsNullOrEmpty(word.TargetLanguageTerm))
                byTerm[word.TargetLanguageTerm] = word;
        var id = 0;
        var sentences = ImmutableArray.CreateBuilder<NativeReadingSentence>(_sentenceTexts.Count);
        for (var index = 0; index < _sentenceTexts.Count; index++)
        {
            var tokens = ImmutableArray.CreateBuilder<NativeReadingToken>();
            foreach (var text in Tokenize(_sentenceTexts[index]))
            {
                var isWord = !string.IsNullOrWhiteSpace(text) && text.Any(char.IsLetterOrDigit);
                var vocabulary = isWord && byTerm.TryGetValue(text.Trim(), out var word) ? word : null;
                tokens.Add(new(id++, index, text, isWord, vocabulary is not null,
                    vocabulary is not null && _focusIds.Contains(vocabulary.Id), vocabulary?.Id));
            }
            sentences.Add(new(index, _sentenceTexts[index], tokens.ToImmutable()));
        }
        _sentences = sentences.ToImmutable();
    }

    /// <summary>Source InteractiveText.TokenizeSentence: word runs (letters, digits, apostrophes) and the text between them.</summary>
    public static List<string> Tokenize(string sentence)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        foreach (var c in sentence)
        {
            var isWordChar = char.IsLetterOrDigit(c) || c == '\'' || c == '’';
            if (isWordChar != inWord && current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
            inWord = isWordChar;
            current.Append(c);
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// BCP 47 tag the passage is spoken in by VoiceOver, from the resource's
    /// language name; null keeps the system language.
    /// </summary>
    public static string? SpeechLanguageTag(string? language) => language?.Trim() switch
    {
        "English" => "en-US",
        "Korean" => "ko-KR",
        "French" => "fr-FR",
        "German" => "de-DE",
        "Spanish" => "es-ES",
        "Japanese" => "ja-JP",
        "Chinese" => "zh-CN",
        _ => null
    };

    /// <summary>
    /// The language VoiceOver speaks the passage in. Hangul is written only in
    /// Korean, so a Hangul passage is Korean even when its resource language
    /// is mislabeled (the Reading audio voice is Korean as well); otherwise the
    /// resource language decides.
    /// </summary>
    public static string? PassageLanguageTag(string? resourceLanguage, string? passage)
    {
        if (!string.IsNullOrEmpty(passage) && passage.Any(character => character is >= '\uAC00' and <= '\uD7A3'))
            return "ko-KR";
        return SpeechLanguageTag(resourceLanguage);
    }

    public static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static int ClampFontSize(int size)
    {
        size = Math.Clamp(size, MinimumFontSize, MaximumFontSize);
        return size - (size - MinimumFontSize) % 2;
    }

    private void SetPhase(NativePhase phase, string? errorKey = null)
    {
        lock (_sync)
        {
            _phase = phase;
            _errorKey = errorKey;
        }
        Publish();
    }

    private NativeReadingState Refuse()
    {
        lock (_sync)
        {
            _lookup++;
            _audio?.Stop();
            _playing = false;
            _panel = null;
            _phase = NativePhase.Blocked;
            _errorKey = NativeReadingText.Unavailable;
        }
        return Publish();
    }

    private NativeReadingState Publish()
    {
        NativeReadingState state;
        lock (_sync)
        {
            _state = new(
                _phase, _destination, _resource?.Title, _sentences, _current,
                _generating, _audioStatusKey, _audioStatusDetail, _audioProgress,
                _audioLoaded, _playing, _time, _duration, _fontSize, _speed,
                _panel, _notice, _errorKey,
                _launch is not null && pendingWrites.IsPending(_launch.UserId),
                PassageLanguageTag(_resource?.Language, _resource?.Transcript));
            state = State;
        }
        // Once Back has begun, background audio and lookups publish nothing;
        // only Back's own destination state reaches the screen.
        if (_exit.HasBegun && state.Destination != NativeDestination.TodaysPlan)
            return state;
        StateChanged?.Invoke(state);
        return state;
    }

    public void Dispose()
    {
        TimestampedAudioManager? audio;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            audio = _audio;
            _audio = null;
        }
        _exit.Complete();
        _lifetime.Cancel();

        if (audio is not null)
        {
            audio.SentenceChanged -= OnSentenceChanged;
            audio.ProgressUpdated -= OnProgressUpdated;
            audio.PlaybackEnded -= OnPlaybackEnded;
            audio.Stop();
            audio.Dispose();
        }
        timer.Dispose();
    }
}
