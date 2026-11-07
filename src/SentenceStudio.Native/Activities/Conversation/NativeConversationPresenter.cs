using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Conversation (source UI/Pages/Conversation.razor, activities.md 9). Scenario
/// selector (restarts the chat), page-local target-language switch (restarts,
/// not persisted), AI persona opening line, typed turns with the source
/// ContinueConversation reply (grammar corrections and comprehension on the
/// learner's bubble), Listen TTS on persona bubbles with Stop, eight Korean
/// quick phrases, and Back. Each reply scores the owner's vocabulary through
/// ExtractAndScoreVocabularyAsync (weight 1.2, PenaltyOverride 0.8). As in
/// the source, no UserActivity is written and the transcript is not saved.
///
/// Unlike the source, every read and write is bound to the launch owner, AI
/// failures surface as the source error toasts instead of a silent empty
/// bubble, and no AI or speech call holds the presenter lock: Back cancels them
/// and always stops and saves the timer at once. Late results apply only while
/// their generation is current.
///
/// Voice mode (NativeConversationPresenter.Voice.cs) is offered when the head registers the
/// platform audio and the API is configured.
/// </summary>
public sealed partial class NativeConversationPresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    LearningResourceRepository resources,
    INativeConversationAi ai,
    INativeConversationScenarios scenarioStore,
    INativeShadowingSpeech speech,
    INativeShadowingPlayer player,
    IServiceScopeFactory scopes,
    NativePendingWrites pendingWrites,
    ILogger<NativeConversationPresenter> logger,
    INativeConversationVoice? voice = null,
    INativeVoiceAudio? voiceAudio = null,
    SentenceStudio.Abstractions.IPreferencesService? preferences = null) : IDisposable
{
    private readonly INativeConversationVoice? _voiceService = voice;
    private readonly INativeVoiceAudio? _voiceAudio = voiceAudio;
    private readonly SentenceStudio.Abstractions.IPreferencesService? _preferences = preferences;
    public const string ScoringActivity = "Conversation";
    public const float DifficultyWeight = 1.2f;
    public const float PenaltyOverride = 0.8f;
    public const string UserAuthor = "Me";
    public const string FallbackPersona = "AI";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _exitCancel = new();
    private readonly NativeActivityExit _exit = new();
    private readonly List<ConversationChunk> _chunks = [];
    private readonly List<VocabularyWord> _vocabulary = [];
    private List<ConversationScenario> _scenarios = [];
    private ConversationScenario? _active;
    private NativeActivityLaunch? _launch;
    private NativeConversationState _state = new(NativePhase.Idle);
    private ImmutableArray<NativeMessage> _notices = [];
    private CancellationTokenSource? _audioLoad;
    private string _language = "Korean";
    private string _draft = string.Empty;
    private int _draftVersion;
    private bool _starting;
    private bool _waiting;
    private bool _playing;
    private bool _showPhrases;
    private long _generation;
    private long _audioVersion;
    private bool _subscribed;
    private bool _disposed;
    private bool _exited;

    /// <summary>The Back/Exit fence (tests force continuation ordering through it).</summary>
    internal NativeActivityExit ExitFence => _exit;

    public NativeConversationState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeConversationText.Unavailable)
            : _state;

    public event Action<NativeConversationState>? StateChanged;

    /// <summary>The opening-line request started by <see cref="OpenAsync"/> (for tests and diagnostics).</summary>
    internal Task<NativeConversationState> Opening { get; private set; } =
        Task.FromResult(new NativeConversationState(NativePhase.Idle));

    /// <summary>
    /// Starts the timer and loads the profile language, scenarios and vocabulary,
    /// then returns while the opening line is requested in the background (the
    /// result arrives through <see cref="StateChanged"/>), so Back stays usable.
    /// </summary>
    public async Task<NativeConversationState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        OpeningRequest? opening = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null)
                return State;
            _launch = launch;
            _starting = true;
            Publish(Snapshot(NativePhase.Loading));
            if (launch.Route != NativeActivityCatalog.Conversation
                || !owner.IsActive(launch.UserId)
                || !await timer.StartAsync(launch, cancellationToken))
                return Refuse();
            if (!_subscribed)
            {
                player.Ended += OnPlayerEnded;
                _subscribed = true;
            }
            try
            {
                await LoadAsync(launch.UserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Source: "Could not start conversation." and no opening request.
                logger.LogError(ex, "Conversation could not load its scenarios or vocabulary.");
                _starting = false;
                _notices = [new(NativeMessageKind.Error, NativeConversationText.LoadFailed)];
                return PublishReady();
            }
            if (!owner.IsActive(launch.UserId))
                return Refuse();
            opening = BeginOpeningLocked();
            PublishCurrent();
        }
        finally
        {
            _gate.Release();
        }
        if (opening is not null)
            Opening = CompleteOpeningAsync(opening);
        return State;
    }

    /// <summary>Typing updates the draft without publishing, so a re-render never interrupts IME composition.</summary>
    public void SetDraft(string? value)
    {
        _draft = value ?? string.Empty;
        _state = _state with { Draft = _draft };
    }

    /// <summary>Source OnScenarioChanged: switch persona and restart the chat.</summary>
    public async Task<NativeConversationState> SelectScenarioAsync(
        string scenarioId, CancellationToken cancellationToken = default)
    {
        OpeningRequest opening;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_active?.Id == scenarioId)
                return State;
            var scenario = _scenarios.FirstOrDefault(item => item.Id == scenarioId);
            if (scenario is null)
                return State;
            _active = scenario;
            opening = BeginOpeningLocked();
            PublishCurrent();
        }
        finally
        {
            _gate.Release();
        }
        return await CompleteOpeningAsync(opening);
    }

    /// <summary>Source ChangeLanguage: page-local target language (not saved to the profile); restarts the chat.</summary>
    public async Task<NativeConversationState> SelectLanguageAsync(
        string language, CancellationToken cancellationToken = default)
    {
        OpeningRequest opening;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (string.IsNullOrWhiteSpace(language)
                || string.Equals(language, _language, StringComparison.Ordinal)
                || !NativeConversationState.Languages.Contains(language))
                return State;
            _language = language;
            opening = BeginOpeningLocked();
            PublishCurrent();
        }
        finally
        {
            _gate.Release();
        }
        return await CompleteOpeningAsync(opening);
    }

    /// <summary>Source SendMessage (Send button or Enter).</summary>
    public async Task<NativeConversationState> SendAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        string userId;
        ConversationChunk userChunk;
        List<ConversationChunk> history;
        ConversationScenario? scenario;
        string language;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_state.Phase != NativePhase.Ready || _waiting || _voice != NativeConversationVoice.Off
                || string.IsNullOrWhiteSpace(_draft))
                return State;
            var message = _draft.Trim();
            _draft = string.Empty;
            _draftVersion++;
            _notices = [];
            userChunk = new ConversationChunk
            {
                Text = message,
                Author = UserAuthor,
                Role = ConversationRole.User,
                SentTime = DateTime.UtcNow
            };
            _chunks.Add(userChunk);
            _waiting = true;
            generation = _generation;
            userId = _launch!.UserId;
            history = [.. _chunks];
            scenario = _active;
            language = _language;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token, _exitCancel.Token);
        Reply? reply = null;
        Exception? failure = null;
        try
        {
            reply = await ai.ContinueAsync(userId, history, scenario, language, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (!await EnterAsync())
            return _state;
        try
        {
            if (generation != _generation || Exited)
                return State;
            _waiting = false;
            if (!Active())
                return Refuse();
            if (linked.IsCancellationRequested)
                return PublishReady();
            if (failure is not null || string.IsNullOrWhiteSpace(reply?.Message))
            {
                if (failure is not null)
                    logger.LogError(failure, "Conversation could not get a reply.");
                else
                    logger.LogWarning("Conversation reply was empty.");
                _notices = [new(NativeMessageKind.Error, NativeConversationText.ReplyFailed)];
                return PublishReady();
            }
            if (reply!.GrammarCorrections?.Count > 0)
                userChunk.GrammarCorrections = reply.GrammarCorrections;
            userChunk.Comprehension = Reply.NormalizeComprehension(reply.Comprehension);
            _chunks.Add(new ConversationChunk
            {
                Text = reply.Message,
                Author = Persona(scenario),
                Role = ConversationRole.Assistant,
                SentTime = DateTime.UtcNow
            });
            // Still current and owned (checked above): score before publishing.
            if (await ScoreAsync(userId, reply.VocabularyAnalysis) is false)
                _notices = [new(NativeMessageKind.Error, NativeText.SaveFailed)];
            if (!Active())
                return Refuse();
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeConversationState> ToggleQuickPhrasesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_state.Phase != NativePhase.Ready || _waiting || _voice != NativeConversationVoice.Off)
                return State;
            _showPhrases = !_showPhrases;
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source UseQuickPhrase: replaces the draft and hides the phrases.</summary>
    public async Task<NativeConversationState> UseQuickPhraseAsync(
        int index, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_state.Phase != NativePhase.Ready || _waiting || _voice != NativeConversationVoice.Off
                || index < 0 || index >= NativeConversationState.QuickPhrases.Length)
                return State;
            _draft = NativeConversationState.QuickPhrases[index];
            _draftVersion++;
            _showPhrases = false;
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeConversationState> DismissNoticesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            _notices = [];
            return PublishCurrent();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source PlayChunkAudio on a persona bubble: stops any clip, then plays
    /// the target-language reply through the native TTS path with the voice
    /// for the selected language.
    /// </summary>
    public async Task<NativeConversationState> ListenAsync(
        int index, CancellationToken cancellationToken = default)
    {
        long version;
        string userId;
        string text;
        string language;
        CancellationTokenSource load;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            if (_state.Phase != NativePhase.Ready || _voice != NativeConversationVoice.Off
                || index < 0 || index >= _chunks.Count
                || _chunks[index].Role != ConversationRole.Assistant
                || _voiceChunks.Contains(_chunks[index])
                || string.IsNullOrEmpty(_chunks[index].Text))
                return State;
            StopAudioCore();
            _notices = [];
            load = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token, _exitCancel.Token);
            _audioLoad = load;
            version = _audioVersion;
            userId = _launch!.UserId;
            text = _chunks[index].Text!;
            language = _language;
            _playing = true;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        NativeSpeechClip clip;
        try
        {
            clip = await speech.GetAsync(userId, text, language, load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return await _exit.StateAsync(_gate, () => State);
        }
        catch (Exception ex)
        {
            logger.LogError("Conversation audio could not be prepared: {Failure}.", ex.GetType().Name);
            clip = new(NativeSpeechClipStatus.Failed);
        }

        if (!await EnterAsync())
            return _state;
        try
        {
            if (version != _audioVersion || Exited)
                return State;
            if (!Active())
                return Refuse();
            if (clip.Status == NativeSpeechClipStatus.Ready && clip.Audio is { } audio
                && player.Load(audio) && player.Play(1f))
                return PublishReady();
            player.Stop();
            _playing = false;
            _notices = [new(NativeMessageKind.Error,
                clip.Status == NativeSpeechClipStatus.Offline
                    ? NativeConversationText.AudioOffline : NativeConversationText.AudioFailed)];
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source StopAudio.</summary>
    public async Task<NativeConversationState> StopAudioAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(out var refused))
                return refused;
            StopAudioCore();
            return PublishCurrent();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Back: cancels any AI or speech request, stops audio, stops and saves the
    /// timer, then returns to the Dashboard. Never waits for the network.
    /// </summary>
    public async Task<NativeConversationState> ExitAsync(CancellationToken cancellationToken = default)
    {
        var exited = false;
        _exit.Begin();
        try
        {
            _exitCancel.Cancel();
            await _exit.AfterCancelAsync();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _generation++;
                _exited = exited = true;
                StopVoiceCore();
                StopAudioCore();
                string? errorKey = null;
                try
                {
                    if (await timer.StopAsync(cancellationToken) == NativeActivityStop.Failed)
                        errorKey = NativeText.SaveFailed;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Conversation could not save its activity minutes.");
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
        finally
        {
            _exit.End(exited);
        }
    }

    private sealed record OpeningRequest(long Generation, string UserId,
        ConversationScenario? Scenario, string Language);

    /// <summary>Source StartConversation: clear the chat and show the spinner. Caller holds the gate.</summary>
    private OpeningRequest BeginOpeningLocked()
    {
        _generation++;
        StopVoiceCore();
        _chunks.Clear();
        ForgetVoiceTurns();
        _waiting = false;
        _starting = true;
        _showPhrases = false;
        _notices = [];
        return new(_generation, _launch!.UserId, _active, _language);
    }

    private async Task<NativeConversationState> CompleteOpeningAsync(OpeningRequest request)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _exitCancel.Token);
        string? opening = null;
        Exception? failure = null;
        try
        {
            opening = await ai.StartAsync(request.UserId, request.Scenario, request.Language, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (!await EnterAsync())
            return _state;
        try
        {
            if (request.Generation != _generation || Exited)
                return State;
            _starting = false;
            if (!Active())
                return Refuse();
            if (linked.IsCancellationRequested)
                return PublishReady();
            if (failure is not null || string.IsNullOrWhiteSpace(opening))
            {
                if (failure is not null)
                    logger.LogError(failure, "Conversation could not get its opening line.");
                else
                    logger.LogWarning("Conversation opening line was empty.");
                // Source toast "Error starting conversation."; the source shows an
                // empty chat without it when the service swallows the failure.
                _notices = [new(NativeMessageKind.Error, NativeConversationText.StartFailed)];
                return PublishReady();
            }
            _chunks.Add(new ConversationChunk
            {
                Text = opening,
                Author = Persona(request.Scenario),
                Role = ConversationRole.Assistant,
                SentTime = DateTime.UtcNow
            });
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadAsync(string userId, CancellationToken cancellationToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var profile = await scope.ServiceProvider.GetRequiredService<UserProfileRepository>()
                .GetByIdAsync(userId);
            _language = string.IsNullOrWhiteSpace(profile?.TargetLanguage) ? "Korean" : profile.TargetLanguage;
        }
        LoadVoicePace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        _scenarios = [.. await scenarioStore.GetAllAsync(userId, cancellationToken)];
        // Source: ?scenarioId when present (no native launch carries one), else the first scenario.
        _active = _scenarios.FirstOrDefault();
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsActive(userId))
            return;
        _vocabulary.Clear();
        _vocabulary.AddRange(await resources.GetAllVocabularyWordsWithResourcesAsync(userId));
    }

    /// <summary>True when scored or nothing to score; false when the progress write failed.</summary>
    private async Task<bool> ScoreAsync(string userId, List<VocabularyAnalysis>? analysis)
    {
        if (_vocabulary.Count == 0 || analysis is not { Count: > 0 } || !owner.IsActive(userId))
            return true;
        try
        {
            using var scope = scopes.CreateScope();
            var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
            var results = await progress.ExtractAndScoreVocabularyAsync(
                analysis, [.. _vocabulary], userId, ScoringActivity, DifficultyWeight, PenaltyOverride);
            if (results.Count > 0)
                pendingWrites.MarkPending(userId);
            scope.ServiceProvider.GetService<ProgressCacheService>()?.InvalidateVocabSummary();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Conversation could not score vocabulary for a reply.");
            return false;
        }
    }

    private static string Persona(ConversationScenario? scenario) =>
        string.IsNullOrWhiteSpace(scenario?.PersonaName) ? FallbackPersona : scenario.PersonaName;

    private void StopAudioCore()
    {
        _audioVersion++;
        _audioLoad?.Cancel();
        _audioLoad?.Dispose();
        _audioLoad = null;
        player.Stop();
        _playing = false;
    }

    private void OnPlayerEnded() => _ = HandleEndedAsync();

    private async Task HandleEndedAsync()
    {
        if (!await EnterAsync())
            return;
        try
        {
            if (!_playing || Exited)
                return;
            _playing = false;
            PublishCurrent();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-takes the gate for a continuation. False (gate not held) once Back has
    /// begun or the presenter was disposed: the caller returns <see cref="_state"/>,
    /// which is then the state Back published, never the pre-Back one.
    /// </summary>
    private async Task<bool> EnterAsync()
    {
        if (await _exit.EnterAsync(_gate))
            return true;
        _gate.Release();
        return false;
    }

    private bool CanCommand(out NativeConversationState state)
    {
        if (Exited)
        {
            state = State;
            return false;
        }
        if (!Active())
        {
            state = Refuse();
            return false;
        }
        state = State;
        return true;
    }

    private bool Exited => _exited || _exit.HasBegun || _state.Destination != NativeDestination.Activity;

    private bool Active() => _launch is not null && owner.IsActive(_launch.UserId);

    private NativeConversationState Snapshot(NativePhase phase) => new(
        phase,
        Scenarios: _scenarios.Select(scenario =>
                new NativeConversationScenarioOption(scenario.Id, scenario.DisplayName, scenario.PersonaName))
            .ToImmutableArray(),
        ActiveScenarioId: _active?.Id,
        TargetLanguage: _language,
        Messages: _chunks.Select((chunk, index) => Message(chunk, index)).ToImmutableArray(),
        IsStarting: _starting,
        IsWaitingForReply: _waiting,
        IsPlayingAudio: _playing,
        ShowQuickPhrases: _showPhrases,
        Draft: _draft,
        DraftVersion: _draftVersion,
        Notices: _notices,
        HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId),
        Voice: _voice,
        IsVoiceAvailable: VoiceAvailable,
        MicLevel: _micLevel,
        PartnerName: PartnerName,
        VoiceSpeedPercent: _voiceSpeed,
        VoicePauseMilliseconds: _voicePause);

    /// <summary>Source: the spinner page shows while starting with no messages; otherwise the chat.</summary>
    private NativeConversationState PublishCurrent() =>
        Publish(Snapshot(_starting && _chunks.Count == 0 ? NativePhase.Loading : NativePhase.Ready));

    private NativeConversationState PublishReady() => PublishCurrent();

    private NativeConversationState Refuse()
    {
        _generation++;
        if (_exited)
            return State;
        StopVoiceCore();
        StopAudioCore();
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeConversationText.Unavailable));
    }

    private NativeConversationState Publish(NativeConversationState state)
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
        _exit.Complete();
        _exitCancel.Cancel();
        _lifetime.Cancel();
        if (_subscribed)
            player.Ended -= OnPlayerEnded;
        _audioLoad?.Cancel();
        Interlocked.Increment(ref _voiceGeneration);
        ReleaseVoice();
        player.Stop();
        player.Dispose();
        timer.Dispose();
    }

}
