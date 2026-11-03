using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Minimal Pairs (source UI/Pages/MinimalPairs.razor, MinimalPairCreate.razor,
/// MinimalPairSession.razor; activities.md 15). Three pages inside one
/// activity: the pair list (/minimal-pairs, Back to the Dashboard), the create
/// page and the listening session; create and session go Back to the list,
/// which reloads. Focus practises one selected pair for 10 trials, Mixed every
/// pair for 20. Each trial speaks one word of a random pair (audio only,
/// through the native TTS cache) and the learner picks the matching tile; an
/// attempt row is stored per answer and the session row is ended after the
/// last trial. No timer, plan item, vocabulary progress or UserActivity, as the
/// source. Unlike the source (hard-coded user "1"), every read and write is
/// bound to the launch owner and refused when that owner is absent or changes.
/// Speech runs outside the presenter lock; leaving a page cancels it and the
/// pending auto-advance, and a late result is discarded.
/// </summary>
public sealed class NativeMinimalPairsPresenter(
    NativeActivityOwner owner,
    NativeMinimalPairStore store,
    UserProfileRepository profiles,
    INativeShadowingSpeech speech,
    INativeShadowingPlayer player,
    ILogger<NativeMinimalPairsPresenter> logger,
    TimeProvider? clock = null) : IDisposable
{
    public const int FocusTrials = 10;
    public const int MixedTrials = 20;

    /// <summary>Source: the first prompt plays 500 ms after the session starts.</summary>
    internal TimeSpan FirstPromptDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Source: the next trial (and its audio) follows 1500 ms after Check Answer.</summary>
    internal TimeSpan AdvanceDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    internal Random Random { get; set; } = new();

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeActivityExit _exit = new();
    private readonly object _operationSync = new();
    private CancellationTokenSource _operation = new();
    private NativeActivityLaunch? _launch;
    private NativeMinimalPairsState _state = new(NativePhase.Idle);
    private NativeMinimalPairsPage _page = NativeMinimalPairsPage.List;
    private NativeMinimalPairsListState _list = new(true, []);
    private NativeMinimalPairsCreateState _create = new(true, []);
    private ImmutableArray<NativeMessage> _notices = [];
    // Session
    private NativeMinimalPairsSessionState _session = new(true);
    private List<MinimalPair> _pairs = [];
    private MinimalPair? _currentPair;
    private VocabularyWord? _promptWord;
    private string? _sessionId;
    private DateTimeOffset _sessionStarted;
    private string _language = "Korean";
    private long _generation;
    private long _audioVersion;
    private bool _exited;
    private bool _disposed;

    /// <summary>The Back/Exit fence (tests force continuation ordering through it).</summary>
    internal NativeActivityExit ExitFence => _exit;

    public NativeMinimalPairsState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeMinimalPairsText.Unavailable)
            : _state;

    public event Action<NativeMinimalPairsState>? StateChanged;

    public async Task<NativeMinimalPairsState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null)
                return State;
            _launch = launch;
            if (launch.Route != NativeActivityCatalog.MinimalPairs || !owner.IsActive(launch.UserId))
                return Refuse();
            Publish(new(NativePhase.Loading));
            await LoadPairsCoreAsync(cancellationToken);
            return Active() ? PublishReady() : Refuse();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- List ----

    /// <summary>Source SetMode: Mixed clears the Focus selection.</summary>
    public Task<NativeMinimalPairsState> SetModeAsync(NativeMinimalPairsMode mode,
        CancellationToken cancellationToken = default) =>
        ListCommandAsync(() => _list = _list with
        {
            Mode = mode,
            SelectedPairId = mode == NativeMinimalPairsMode.Mixed ? null : _list.SelectedPairId,
        }, cancellationToken);

    /// <summary>Source SelectPair: only Focus mode selects a card.</summary>
    public Task<NativeMinimalPairsState> SelectPairAsync(string pairId,
        CancellationToken cancellationToken = default) =>
        ListCommandAsync(() =>
        {
            if (_list.Mode == NativeMinimalPairsMode.Focus && _list.Pairs.Any(pair => pair.Id == pairId))
                _list = _list with { SelectedPairId = pairId };
        }, cancellationToken);

    /// <summary>Source RequestDelete: opens the "Delete A vs B?" confirmation.</summary>
    public Task<NativeMinimalPairsState> RequestDeleteAsync(string pairId,
        CancellationToken cancellationToken = default) =>
        ListCommandAsync(() =>
        {
            if (!_list.IsDeleting && _list.Pairs.FirstOrDefault(pair => pair.Id == pairId) is { } pair)
                _list = _list with { PendingDelete = pair };
        }, cancellationToken);

    public Task<NativeMinimalPairsState> CancelDeleteAsync(CancellationToken cancellationToken = default) =>
        ListCommandAsync(() =>
        {
            if (!_list.IsDeleting)
                _list = _list with { PendingDelete = null };
        }, cancellationToken);

    /// <summary>Source ConfirmDelete: deletes the owner's pair, reloads the list and shows "Pair deleted."</summary>
    public async Task<NativeMinimalPairsState> ConfirmDeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(NativeMinimalPairsPage.List, out var refused))
                return refused;
            if (_list.PendingDelete is not { } pair || _list.IsDeleting)
                return State;
            _list = _list with { IsDeleting = true };
            PublishReady();
            var deleted = false;
            try
            {
                deleted = await store.DeletePairAsync(_launch!.UserId, pair.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Minimal Pairs could not delete a pair.");
                AddNotice(NativeMessageKind.Error, NativeMinimalPairsText.DeleteFailed);
            }
            if (!Active())
                return Refuse();
            _list = _list with
            {
                IsDeleting = false,
                PendingDelete = null,
                SelectedPairId = _list.SelectedPairId == pair.Id ? null : _list.SelectedPairId,
            };
            if (deleted)
            {
                await LoadPairsCoreAsync(CancellationToken.None);
                AddNotice(NativeMessageKind.Success, NativeMinimalPairsText.Deleted);
            }
            return Active() ? PublishReady() : Refuse();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Create ----

    /// <summary>Source CreatePair button: /minimal-pairs/create loads the owner's vocabulary.</summary>
    public async Task<NativeMinimalPairsState> OpenCreateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(NativeMinimalPairsPage.List, out var refused))
                return refused;
            if (_list.IsLoading || _list.IsDeleting)
                return State;
            BeginPage(NativeMinimalPairsPage.Create);
            _create = new(true, []);
            PublishReady();
            ImmutableArray<NativeMinimalPairWordOption> words = [];
            try
            {
                var rows = await store.ListWordsAsync(_launch!.UserId, cancellationToken);
                words = [.. rows.Select(word => new NativeMinimalPairWordOption(word.Id,
                    word.TargetLanguageTerm ?? string.Empty, word.NativeLanguageTerm ?? string.Empty))];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Minimal Pairs could not load vocabulary.");
                AddNotice(NativeMessageKind.Error, NativeMinimalPairsText.VocabularyLoadFailed);
            }
            if (!Active())
                return Refuse();
            _create = new(false, words);
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Typing a search stores it without publishing, so a re-render never
    /// interrupts IME composition; heads refresh the word rows from the result.
    /// </summary>
    public NativeMinimalPairsCreateState? SetSearch(char slot, string? value)
    {
        if (!CanEditCreate())
            return null;
        _create = char.ToUpperInvariant(slot) == 'A'
            ? _create with { SearchA = value ?? string.Empty }
            : _create with { SearchB = value ?? string.Empty };
        _state = _state with { Create = _create };
        return _create;
    }

    /// <summary>Stores the optional contrast label without publishing (IME-safe).</summary>
    public NativeMinimalPairsCreateState? SetContrastLabel(string? value)
    {
        if (!CanEditCreate())
            return null;
        _create = _create with { ContrastLabel = value ?? string.Empty };
        _state = _state with { Create = _create };
        return _create;
    }

    /// <summary>Source select binding for word A or B; only one of the owner's listed words.</summary>
    public async Task<NativeMinimalPairsState> SelectWordAsync(char slot, string wordId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(NativeMinimalPairsPage.Create, out var refused))
                return refused;
            if (_create.IsLoading || _create.IsSaving || _create.Words.All(word => word.Id != wordId))
                return State;
            _create = char.ToUpperInvariant(slot) == 'A'
                ? _create with { WordAId = wordId }
                : _create with { WordBId = wordId };
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source CreatePair: both words required and different; a duplicate (or a
    /// refused word) shows "Failed to create pair"; success returns to the
    /// reloaded list with "Minimal pair created!".
    /// </summary>
    public async Task<NativeMinimalPairsState> CreatePairAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(NativeMinimalPairsPage.Create, out var refused))
                return refused;
            if (_create.IsLoading || _create.IsSaving)
                return State;
            _create = _create with { ErrorKey = null };
            if (_create.WordAId is null || _create.WordBId is null)
            {
                _create = _create with { ErrorKey = NativeMinimalPairsText.SelectBothWords };
                return PublishReady();
            }
            if (_create.WordAId == _create.WordBId)
            {
                _create = _create with { ErrorKey = NativeMinimalPairsText.SelectDifferent };
                return PublishReady();
            }
            _create = _create with { IsSaving = true };
            PublishReady();
            MinimalPair? created = null;
            try
            {
                created = await store.CreatePairAsync(_launch!.UserId, _create.WordAId, _create.WordBId,
                    _create.ContrastLabel, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Minimal Pairs could not create a pair.");
            }
            if (!Active())
                return Refuse();
            if (created is null)
            {
                _create = _create with { IsSaving = false, ErrorKey = NativeMinimalPairsText.FailedToCreate };
                return PublishReady();
            }
            BeginPage(NativeMinimalPairsPage.List);
            _list = _list with { IsLoading = true };
            PublishReady();
            await LoadPairsCoreAsync(CancellationToken.None);
            AddNotice(NativeMessageKind.Success, NativeMinimalPairsText.Created);
            return Active() ? PublishReady() : Refuse();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Session ----

    /// <summary>
    /// Source StartSession: Focus needs a selected pair ("Please select a pair to
    /// practice in Focus mode."); Focus runs 10 trials of that pair, Mixed 20
    /// trials over every pair. Opens the session page, stores the session row,
    /// deals the first trial and plays its audio after 500 ms.
    /// </summary>
    public async Task<NativeMinimalPairsState> StartSessionAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(NativeMinimalPairsPage.List, out var refused))
                return refused;
            if (_list.IsLoading || _list.IsDeleting)
                return State;
            _notices = [];
            if (_list.Mode == NativeMinimalPairsMode.Focus && _list.SelectedPairId is null)
            {
                AddNotice(NativeMessageKind.Error, NativeMinimalPairsText.SelectPairFirst);
                return PublishReady();
            }
            if (_list.Pairs.IsDefaultOrEmpty)
            {
                AddNotice(NativeMessageKind.Error, NativeMinimalPairsText.NoPairsToStart);
                return PublishReady();
            }
            var mode = _list.Mode;
            string[] ids = mode == NativeMinimalPairsMode.Focus
                ? [_list.SelectedPairId!]
                : [.. _list.Pairs.Select(pair => pair.Id)];
            var trials = mode == NativeMinimalPairsMode.Focus ? FocusTrials : MixedTrials;
            BeginPage(NativeMinimalPairsPage.Session);
            _session = new(true, Mode: mode, TotalTrials: trials);
            _pairs = [];
            _sessionId = null;
            _currentPair = null;
            _promptWord = null;
            PublishReady();

            var userId = _launch!.UserId;
            try
            {
                _language = await LanguageAsync(userId, cancellationToken);
                _pairs = await store.GetPairsAsync(userId, ids, cancellationToken);
                if (!Active())
                    return Refuse();
                if (_pairs.Count == 0)
                {
                    _session = _session with { IsInitializing = false, IsEmpty = true };
                    return PublishReady();
                }
                var row = await store.StartSessionAsync(userId, mode.ToString(), trials, cancellationToken);
                _sessionId = row.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Source InitializeSession logs and falls through to the empty state.
                logger.LogError(ex, "Minimal Pairs could not start a session.");
                if (!Active())
                    return Refuse();
                _pairs = [];
                _session = _session with { IsInitializing = false, IsEmpty = true };
                return PublishReady();
            }
            if (!Active())
                return Refuse();
            _sessionStarted = _clock.GetUtcNow();
            _session = _session with { IsInitializing = false };
            DealTrialCore();
            generation = _generation;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }
        _ = PlayAfterAsync(FirstPromptDelay, generation);
        return State;
    }

    /// <summary>Source SelectWord: ignored after checking or while debouncing.</summary>
    public async Task<NativeMinimalPairsState> SelectAnswerAsync(string wordId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(NativeMinimalPairsPage.Session, out var refused))
                return refused;
            if (!_session.CanSelect || (_session.Left?.WordId != wordId && _session.Right?.WordId != wordId))
                return State;
            _session = _session with { SelectedWordId = wordId };
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source CheckAnswer: scores the selection against the spoken word, stores
    /// the attempt, reveals the answer, then after 1500 ms deals the next trial
    /// and plays it (or ends the session after the last trial).
    /// </summary>
    public async Task<NativeMinimalPairsState> CheckAnswerAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(NativeMinimalPairsPage.Session, out var refused))
                return refused;
            if (!_session.CanCheck || _promptWord is null || _currentPair is null)
                return State;
            var selected = _session.SelectedWordId!;
            var correct = selected == _promptWord.Id;
            _session = _session with
            {
                HasChecked = true,
                WasCorrect = correct,
                AnswerWordId = _promptWord.Id,
                IsAdvancing = true,
                CorrectCount = _session.CorrectCount + (correct ? 1 : 0),
                IncorrectCount = _session.IncorrectCount + (correct ? 0 : 1),
            };
            if (_sessionId is { } sessionId)
            {
                try
                {
                    var stored = await store.RecordAttemptAsync(_launch!.UserId, sessionId, _currentPair.Id,
                        _promptWord.Id, selected, correct, CancellationToken.None);
                    if (stored is null)
                        AddNotice(NativeMessageKind.Error, NativeMinimalPairsText.SaveFailed);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Minimal Pairs could not store an attempt.");
                    AddNotice(NativeMessageKind.Error, NativeMinimalPairsText.SaveFailed);
                }
                if (!Active())
                    return Refuse();
            }
            // A clip still being prepared for this trial is discarded; one already playing finishes.
            _audioVersion++;
            _session = _session with { TrialIndex = _session.TrialIndex + 1, IsPlayingAudio = false };
            generation = _generation;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }
        _ = AdvanceAfterAsync(generation);
        return State;
    }

    /// <summary>Source ReplayAudio: only before checking and while no clip is being prepared.</summary>
    public async Task<NativeMinimalPairsState> ReplayAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(NativeMinimalPairsPage.Session, out var refused))
                return refused;
            if (!_session.CanReplay)
                return State;
            generation = _generation;
        }
        finally
        {
            _gate.Release();
        }
        return await PlayPromptAsync(generation);
    }

    // ---- Navigation ----

    public async Task<NativeMinimalPairsState> DismissNoticesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            _notices = [];
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Create/session Back and the summary's Done (source NavigateTo("/minimal-pairs")):
    /// cancels speech and the pending advance, then reloads the list. Leaving
    /// mid-session leaves the session row open, as the source does. On the list
    /// this does nothing; its Back is <see cref="ExitAsync"/>.
    /// </summary>
    public async Task<NativeMinimalPairsState> BackToListAsync(CancellationToken cancellationToken = default)
    {
        CancelOperation();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_page == NativeMinimalPairsPage.List)
                return State;
            BeginPage(NativeMinimalPairsPage.List);
            _list = _list with { IsLoading = true, PendingDelete = null, IsDeleting = false };
            PublishReady();
            await LoadPairsCoreAsync(cancellationToken);
            return Active() ? PublishReady() : Refuse();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>List Back / Go Back: stops audio and returns to the Dashboard at once, even while speech hangs.</summary>
    public async Task<NativeMinimalPairsState> ExitAsync(CancellationToken cancellationToken = default)
    {
        var exited = false;
        _exit.Begin();
        try
        {
            CancelOperation();
            await _exit.AfterCancelAsync();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // A command that held the gate may have started a new page operation
                // after the cancel above; cancel again under the gate.
                CancelOperation();
                if (_exited)
                {
                    exited = true;
                    return State;
                }
                _exited = exited = true;
                _generation++;
                StopAudioCore();
                return Publish(new(NativePhase.Ready, NativeDestination.TodaysPlan, _page, Notices: _notices));
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

    // ---- Internals ----

    private async Task<NativeMinimalPairsState> ListCommandAsync(Action command,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanCommand(NativeMinimalPairsPage.List, out var refused))
                return refused;
            if (_list.IsLoading)
                return State;
            // A message stays until the learner's next action (Captain, 2026-11-02).
            _notices = [];
            command();
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source NextTrial: random pair, random spoken side, tiles in random order.</summary>
    private void DealTrialCore()
    {
        var pair = _pairs[Random.Next(_pairs.Count)];
        var promptIsA = Random.Next(2) == 0;
        var prompt = promptIsA ? pair.VocabularyWordA! : pair.VocabularyWordB!;
        var other = promptIsA ? pair.VocabularyWordB! : pair.VocabularyWordA!;
        var left = Random.Next(2) == 0 ? prompt : other;
        var right = left.Id == prompt.Id ? other : prompt;
        _currentPair = pair;
        _promptWord = prompt;
        _session = _session with
        {
            Left = Tile(left),
            Right = Tile(right),
            SelectedWordId = null,
            HasChecked = false,
            WasCorrect = null,
            AnswerWordId = null,
            IsAdvancing = false,
            IsPlayingAudio = false,
            AudioNoticeKey = null,
        };
    }

    private static NativeMinimalPairTile Tile(VocabularyWord word) =>
        new(word.Id, word.TargetLanguageTerm ?? string.Empty);

    /// <summary>Source summary state; the duration is frozen when the last trial ends.</summary>
    private void EndSessionCore()
    {
        _currentPair = null;
        _promptWord = null;
        _session = _session with
        {
            ShowSummary = true,
            IsAdvancing = false,
            IsPlayingAudio = false,
            DurationMinutes = (_clock.GetUtcNow() - _sessionStarted).TotalMinutes,
        };
    }

    private async Task PlayAfterAsync(TimeSpan delay, long generation)
    {
        try
        {
            await Task.Delay(delay, _clock, OperationToken());
            await PlayPromptAsync(generation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Minimal Pairs prompt audio failed.");
        }
    }

    private async Task AdvanceAfterAsync(long generation)
    {
        try
        {
            await Task.Delay(AdvanceDelay, _clock, OperationToken());
            bool play;
            if (!await TryEnterAsync())
                return;
            try
            {
                if (generation != _generation || Exited || _page != NativeMinimalPairsPage.Session)
                    return;
                if (!Active())
                {
                    Refuse();
                    return;
                }
                if (_session.TrialIndex >= _session.TotalTrials)
                    await EndSessionAsync();
                else
                    DealTrialCore();
                play = !_session.ShowSummary;
                PublishReady();
            }
            finally
            {
                _gate.Release();
            }
            if (play)
                await PlayPromptAsync(generation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Minimal Pairs could not advance to the next trial.");
        }
    }

    /// <summary>Source EndSession: stamps EndedAt on the owner's session row, then shows the summary.</summary>
    private async Task EndSessionAsync()
    {
        if (_sessionId is { } sessionId)
        {
            try
            {
                if (!await store.EndSessionAsync(_launch!.UserId, sessionId, CancellationToken.None))
                    AddNotice(NativeMessageKind.Error, NativeMinimalPairsText.SaveFailed);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Minimal Pairs could not end the session.");
                AddNotice(NativeMessageKind.Error, NativeMinimalPairsText.SaveFailed);
            }
        }
        _sessionId = null;
        EndSessionCore();
    }

    /// <summary>
    /// Source PlayPromptAudio: the spoken word through the owner's TTS cache,
    /// else synthesis; offline and uncached shows "Audio unavailable offline.",
    /// a failure "Audio playback error.". Prepared outside the lock; applied
    /// only while the same trial is showing.
    /// </summary>
    private async Task<NativeMinimalPairsState> PlayPromptAsync(long generation)
    {
        string userId;
        string text;
        long audio;
        CancellationToken token;
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            if (generation != _generation || Exited || _page != NativeMinimalPairsPage.Session
                || !_session.HasTrial || _session.HasChecked || _session.IsPlayingAudio
                || _promptWord is null)
                return State;
            if (!Active())
                return Refuse();
            userId = _launch!.UserId;
            text = _promptWord.TargetLanguageTerm ?? string.Empty;
            audio = ++_audioVersion;
            token = OperationToken();
            _session = _session with { IsPlayingAudio = true, AudioNoticeKey = null };
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        NativeSpeechClip clip;
        try
        {
            clip = await speech.GetAsync(userId, text, _language, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            clip = new(NativeSpeechClipStatus.Failed);
        }
        catch (Exception ex)
        {
            // The word is learner content; only the failure type is logged.
            logger.LogError("Minimal Pairs speech failed: {Failure}.", ex.GetType().Name);
            clip = new(NativeSpeechClipStatus.Failed);
        }

        if (!await TryEnterAsync())
            return _state;
        try
        {
            if (generation != _generation || audio != _audioVersion || Exited
                || _page != NativeMinimalPairsPage.Session || token.IsCancellationRequested)
                return State;
            _session = _session with { IsPlayingAudio = false };
            if (!Active())
                return Refuse();
            if (_session.HasChecked || !_session.HasTrial)
                return PublishReady();
            switch (clip.Status)
            {
                case NativeSpeechClipStatus.Offline:
                    _session = _session with { AudioNoticeKey = NativeMinimalPairsText.AudioOffline };
                    break;
                case NativeSpeechClipStatus.Ready when clip.Audio is { Length: > 0 } bytes:
                    player.Stop();
                    if (!player.Load(bytes) || !player.Play(1.0f))
                    {
                        player.Stop();
                        _session = _session with { AudioNoticeKey = NativeMinimalPairsText.PlaybackError };
                    }
                    break;
                default:
                    _session = _session with { AudioNoticeKey = NativeMinimalPairsText.PlaybackError };
                    break;
            }
            return PublishReady();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadPairsCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await store.ListPairsAsync(_launch!.UserId, cancellationToken);
            var pairs = rows.Select(pair => new NativeMinimalPairItem(pair.Id,
                pair.VocabularyWordA?.TargetLanguageTerm ?? string.Empty,
                pair.VocabularyWordB?.TargetLanguageTerm ?? string.Empty,
                string.IsNullOrEmpty(pair.ContrastLabel) ? null : pair.ContrastLabel)).ToImmutableArray();
            var selected = _list.SelectedPairId is { } id && pairs.Any(pair => pair.Id == id) ? _list.SelectedPairId : null;
            _list = _list with { IsLoading = false, Pairs = pairs, SelectedPairId = selected };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Minimal Pairs could not load pairs.");
            _list = _list with { IsLoading = false, Pairs = [], SelectedPairId = null };
            AddNotice(NativeMessageKind.Error, NativeMinimalPairsText.LoadFailed);
        }
    }

    private async Task<string> LanguageAsync(string userId, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetByIdAsync(userId, cancellationToken);
        return string.IsNullOrWhiteSpace(profile?.TargetLanguage) ? "Korean" : profile.TargetLanguage;
    }

    private bool CanEditCreate() =>
        !Exited && _page == NativeMinimalPairsPage.Create && !_create.IsLoading && !_create.IsSaving && Active();

    private bool CanCommand(NativeMinimalPairsPage page, out NativeMinimalPairsState state)
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
        return _state.Phase == NativePhase.Ready && _page == page;
    }

    private void BeginPage(NativeMinimalPairsPage page)
    {
        _generation++;
        lock (_operationSync)
        {
            var previous = _operation;
            _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            // Cancelled before disposal, so work still holding its token sees cancellation.
            CancelSource(previous);
            previous.Dispose();
        }
        StopAudioCore();
        _notices = [];
        _page = page;
    }

    private void StopAudioCore()
    {
        _audioVersion++;
        player.Stop();
        if (_session.IsPlayingAudio)
            _session = _session with { IsPlayingAudio = false };
    }

    private void AddNotice(NativeMessageKind kind, string key) =>
        _notices = _notices.Add(new(kind, key));

    /// <summary>The current page operation's token (read under a lock: BeginPage swaps and disposes it).</summary>
    private CancellationToken OperationToken()
    {
        lock (_operationSync)
            return _disposed ? new CancellationToken(canceled: true) : _operation.Token;
    }

    private void CancelOperation()
    {
        lock (_operationSync)
            CancelSource(_operation);
    }

    private static void CancelSource(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // A cancellation callback failed; the operation is abandoned either way.
        }
    }

    /// <summary>
    /// Re-takes the gate for a continuation. False (gate not held) once Exit has
    /// begun or the presenter was disposed: the caller returns <see cref="_state"/>,
    /// which is then the state Exit published, never the pre-Exit one.
    /// </summary>
    private async Task<bool> TryEnterAsync()
    {
        if (await _exit.EnterAsync(_gate))
            return true;
        _gate.Release();
        return false;
    }

    private bool Exited => _exited || _exit.HasBegun || _state.Destination != NativeDestination.Activity;

    private bool Active() => _launch is not null && owner.IsActive(_launch.UserId);

    private NativeMinimalPairsState PublishReady() => Publish(new(
        NativePhase.Ready,
        Page: _page,
        List: _list,
        Create: _page == NativeMinimalPairsPage.Create ? _create : null,
        Session: _page == NativeMinimalPairsPage.Session ? _session : null,
        Notices: _notices));

    private NativeMinimalPairsState Refuse()
    {
        _generation++;
        CancelOperation();
        StopAudioCore();
        if (_exited)
            return State;
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeMinimalPairsText.Unavailable));
    }

    private NativeMinimalPairsState Publish(NativeMinimalPairsState state)
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
        CancelOperation();
        _lifetime.Cancel();
        player.Stop();
        player.Dispose();
        lock (_operationSync)
            _operation.Dispose();

    }
}
