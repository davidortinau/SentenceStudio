using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Word Association (source UI/Pages/WordAssociation.razor and
/// WordAssociationService, activities.md 14). A round is five random words
/// from every selected resource (grace-period Familiar words excluded). The
/// learner writes clues for each word, grades them with the source
/// GradeWordAssociation prompt (Reasoning tier), moves between words, and
/// finishes with a scored summary and the all-time / weekly high-score
/// banner. Each grade records one vocabulary attempt (WordAssociation, Text,
/// Association, weight 1.0, correct when at least half of the word's graded
/// clues are related) and a WordAssociation UserActivity. Choose My Own only:
/// the source starts no activity timer, so none is started here.
/// Unlike the source, every read and write is bound to the launch owner and
/// refused when that owner is absent or no longer active, and a grade that
/// returns no verdict writes nothing.
/// </summary>
public sealed class NativeWordAssociationPresenter(
    NativeActivityOwner owner,
    LearningResourceRepository resources,
    INativeWordAssociationAi ai,
    NativeWordAssociationScores scores,
    IServiceScopeFactory scopes,
    NativePendingWrites pendingWrites,
    ILogger<NativeWordAssociationPresenter> logger) : IDisposable
{
    public const int RoundSize = 5;
    public const string AttemptActivity = "WordAssociation";
    public const string AttemptInputMode = "Text";
    public const string AttemptContext = "Association";
    public const float DifficultyWeight = 1.0f;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>Cancelled by Exit / Play again so an in-flight grade stops at once.</summary>
    private CancellationTokenSource _inflight = new();
    private readonly List<VocabularyWord> _words = [];
    private readonly Dictionary<int, List<Clue>> _clues = [];
    private NativeActivityLaunch? _launch;
    private NativeWordAssociationState _state = new(NativePhase.Idle);
    private ImmutableArray<NativeWordAssociationNotice> _notices = [];
    private string _draft = string.Empty;
    private int _index;
    private bool _grading;
    private long _generation;
    private bool _disposed;
    private bool _exited;
    private readonly NativeActivityExit _exit = new();

    public NativeWordAssociationState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeWordAssociationText.Unavailable)
            : _state;

    /// <summary>The Back/Exit fence (tests force continuation ordering through it).</summary>
    internal NativeActivityExit ExitFence => _exit;

    public event Action<NativeWordAssociationState>? StateChanged;

    public async Task<NativeWordAssociationState> OpenAsync(
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
            if (launch.Route != NativeActivityCatalog.WordAssociation
                || !owner.IsActive(launch.UserId))
                return Refuse();
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Typing updates the draft without publishing, so a re-render never
    /// interrupts IME composition.
    /// </summary>
    public void SetDraft(string? value)
    {
        _draft = value ?? string.Empty;
        _state = _state with { Draft = _draft };
    }

    /// <summary>Source AddClue (submit or Enter): one trimmed clue; refused while grading.</summary>
    public Task<NativeWordAssociationState> AddClueAsync(CancellationToken cancellationToken = default) =>
        CommandAsync(() =>
        {
            if (_state.Phase != NativePhase.Ready || _grading || string.IsNullOrWhiteSpace(_draft))
                return State;
            Current.Add(new Clue(_draft.Trim()));
            _draft = string.Empty;
            return PublishReady();
        }, cancellationToken);

    /// <summary>Source RemoveClue: only ungraded clues, and not while they are being graded.</summary>
    public Task<NativeWordAssociationState> RemoveClueAsync(
        int index, CancellationToken cancellationToken = default) =>
        CommandAsync(() =>
        {
            var clues = Current;
            if (_state.Phase != NativePhase.Ready || _grading
                || index < 0 || index >= clues.Count || clues[index].Graded)
                return State;
            clues.RemoveAt(index);
            return PublishReady();
        }, cancellationToken);

    public Task<NativeWordAssociationState> PreviousAsync(CancellationToken cancellationToken = default) =>
        CommandAsync(() =>
        {
            if (!_state.CanPrevious)
                return State;
            _index--;
            _draft = string.Empty;
            return PublishReady();
        }, cancellationToken);

    /// <summary>Source NextWord: enabled only when every clue of the word is graded.</summary>
    public Task<NativeWordAssociationState> NextAsync(CancellationToken cancellationToken = default) =>
        CommandAsync(() =>
        {
            if (!_state.CanAdvance || _state.IsLastWord)
                return State;
            _index++;
            _draft = string.Empty;
            return PublishReady();
        }, cancellationToken);

    public Task<NativeWordAssociationState> DismissNoticesAsync(CancellationToken cancellationToken = default) =>
        CommandAsync(() =>
        {
            _notices = [];
            return Publish(_state with { Notices = _notices });
        }, cancellationToken);

    /// <summary>
    /// Source GradeCurrentWord. The AI call runs outside the gate so Back
    /// stays responsive; an exit or owner change while it runs discards the
    /// result without writing anything.
    /// </summary>
    public async Task<NativeWordAssociationState> GradeAsync(CancellationToken cancellationToken = default)
    {
        List<Clue> pending;
        VocabularyWord word;
        string userId;
        long generation;
        CancellationToken inflight;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_state.Phase != NativePhase.Ready || _grading)
                return State;
            pending = Current.Where(clue => !clue.Graded).ToList();
            if (pending.Count == 0)
                return State;
            _grading = true;
            _notices = [];
            word = _words[_index];
            userId = _launch!.UserId;
            generation = _generation;
            inflight = _inflight.Token;
            PublishReady();
        }
        finally
        {
            _gate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token, inflight);
        var texts = pending.Select(clue => clue.Text).ToList();
        WordAssociationGradeResponse? response = null;
        Exception? failure = null;
        try
        {
            var (target, native) = await LanguagesAsync(userId);
            response = await ai.GradeAsync(userId, word.TargetLanguageTerm ?? string.Empty,
                word.NativeLanguageTerm, texts, target, native, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Back begins its exit before it takes the gate; a continuation that
        // resumes meanwhile waits for Back and returns the state Back published.
        var proceed = await _exit.EnterAsync(_gate);
        try
        {
            _grading = false;
            if (!proceed || generation != _generation || Exited)
                return State;
            if (!Active())
                return Refuse();
            if (linked.IsCancellationRequested)
                return PublishReady();
            var entries = response?.Entries ?? [];
            if (failure is not null || entries.Count == 0)
            {
                if (failure is not null)
                    logger.LogError(failure, "Word Association could not grade clues.");
                else
                    logger.LogWarning("Word Association grading returned no verdicts.");
                _notices = [new(NativeWordAssociationNoticeKind.Error, NativeWordAssociationText.GradeFailed)];
                return PublishReady();
            }
            for (var i = 0; i < pending.Count && i < entries.Count; i++)
                pending[i].Apply(entries[i]);
            return await RecordAsync(userId, word, texts);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source FinishRound: totals over every graded clue, the high-score check
    /// against earlier rounds, then the round row. Enabled like Next.
    /// </summary>
    public async Task<NativeWordAssociationState> FinishAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (!_state.CanAdvance || !_state.IsLastWord)
                return State;
            var graded = _clues.Values.SelectMany(list => list).Where(clue => clue.Graded).ToList();
            var related = graded.Count(clue => clue.Related);
            var score = graded.Where(clue => clue.Related).Sum(clue => clue.IsCloze ? 2 : 1);
            var userId = _launch!.UserId;
            var high = NativeWordAssociationHighScore.None;
            try
            {
                high = await scores.CheckHighScoreAsync(userId, score, DateTime.Now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Word Association could not read earlier high scores.");
            }
            if (!owner.IsActive(userId))
                return Refuse();
            var saved = await scores.SaveAsync(userId, score, graded.Count,
                _words.Select(word => word.Id).ToList(), cancellationToken);
            if (!owner.IsActive(userId))
                return Refuse();
            _generation++;
            _notices = saved ? [] : [new(NativeWordAssociationNoticeKind.Error, NativeText.SaveFailed)];
            return Publish(new(NativePhase.Summary,
                WordCount: _words.Count,
                TotalRelated: related,
                TotalClues: graded.Count,
                TotalScore: score,
                HighScore: high,
                Notices: _notices,
                HasPendingSync: pendingWrites.IsPending(userId)));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source PlayAgain: clear the round and load five new words.</summary>
    public async Task<NativeWordAssociationState> PlayAgainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_state.Phase != NativePhase.Summary)
                return State;
            _generation++;
            CancelInflight();
            Publish(new(NativePhase.Loading));
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Done / Go Back: return to the Dashboard (no timer to stop).</summary>
    public async Task<NativeWordAssociationState> ExitAsync(CancellationToken cancellationToken = default)
    {
        // The AI call runs outside the gate; cancel it first so Back never waits on it.
        var exited = false;
        _exit.Begin();
        try
        {
            CancelInflight();
            await _exit.AfterCancelAsync();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _generation++;
                _exited = exited = true;
                return Publish(new(NativePhase.Ready, NativeDestination.TodaysPlan,
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

    private async Task<NativeWordAssociationState> RecordAsync(
        string userId, VocabularyWord word, IReadOnlyList<string> texts)
    {
        var graded = Current.Where(clue => clue.Graded).ToList();
        var related = graded.Count(clue => clue.Related);
        var wasCorrect = graded.Count > 0 && (float)related / graded.Count >= 0.5f;
        var notices = new List<NativeWordAssociationNotice>();
        var saveFailed = false;
        using (var scope = scopes.CreateScope())
        {
            try
            {
                var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
                var updated = await progress.RecordAttemptAsync(new VocabularyAttempt
                {
                    VocabularyWordId = word.Id,
                    UserId = userId,
                    Activity = AttemptActivity,
                    InputMode = AttemptInputMode,
                    WasCorrect = wasCorrect,
                    DifficultyWeight = DifficultyWeight,
                    ContextType = AttemptContext
                });
                if (updated.IsFamiliar && owner.IsActive(userId))
                {
                    await progress.HandleVerificationProbeResultAsync(word.Id, userId, wasCorrect);
                    notices.Add(wasCorrect
                        ? new(NativeWordAssociationNoticeKind.Success, NativeWordAssociationText.ProbeConfirmed)
                        : new(NativeWordAssociationNoticeKind.Info, NativeWordAssociationText.ProbeTricky));
                }
                pendingWrites.MarkPending(userId);
                scope.ServiceProvider.GetService<ProgressCacheService>()?.InvalidateVocabSummary();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Word Association could not record a vocabulary attempt.");
                saveFailed = true;
            }

            if (owner.IsActive(userId))
            {
                try
                {
                    var activities = scope.ServiceProvider.GetRequiredService<UserActivityRepository>();
                    // UserActivity.CreatedAt is a UTC instant (ProgressService.ComputeStreak).
                    var now = DateTime.UtcNow;
                    var saved = await activities.SaveAsync(new UserActivity
                    {
                        Activity = AttemptActivity,
                        Input = string.Join("; ", texts),
                        Accuracy = graded.Count > 0 ? (double)related / graded.Count * 100 : 0,
                        UserProfileId = userId,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    if (saved < 0)
                    {
                        logger.LogError("Word Association could not save its UserActivity row.");
                        saveFailed = true;
                    }
                    else
                        pendingWrites.MarkPending(userId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Word Association could not save its UserActivity row.");
                    saveFailed = true;
                }
            }
        }

        if (!owner.IsActive(userId))
            return Refuse();
        if (saveFailed)
            notices.Add(new(NativeWordAssociationNoticeKind.Error, NativeText.SaveFailed));
        _notices = [.. notices];
        return PublishReady();
    }

    private async Task<(string Target, string Native)> LanguagesAsync(string userId)
    {
        using var scope = scopes.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<UserProfileRepository>()
            .GetByIdAsync(userId);
        return (string.IsNullOrWhiteSpace(profile?.TargetLanguage) ? "Korean" : profile.TargetLanguage,
            string.IsNullOrWhiteSpace(profile?.NativeLanguage) ? "English" : profile.NativeLanguage);
    }

    /// <summary>Source LoadWords / GetRoundWordsAsync, bound to the launch owner.</summary>
    private async Task<NativeWordAssociationState> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var userId = _launch!.UserId;
        _words.Clear();
        _clues.Clear();
        _notices = [];
        _draft = string.Empty;
        _index = 0;
        try
        {
            var all = new List<VocabularyWord>();
            foreach (var id in _launch.ResourceIds.IsDefault ? [] : _launch.ResourceIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resource = await resources.GetResourceAsync(id, userId);
                if (!owner.IsActive(userId))
                    return Refuse();
                if (resource?.UserProfileId == userId && resource.Vocabulary is { } vocabulary)
                    all.AddRange(vocabulary);
            }
            // A word without a target term would leave nothing to associate with.
            var distinct = all
                .Where(word => !string.IsNullOrWhiteSpace(word.TargetLanguageTerm))
                .GroupBy(word => word.Id).Select(group => group.First()).ToList();
            if (distinct.Count > 0)
            {
                using var scope = scopes.CreateScope();
                var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
                var byWord = await progress.GetProgressForWordsAsync(
                    distinct.Select(word => word.Id).ToList(), userId);
                if (!owner.IsActive(userId))
                    return Refuse();
                _words.AddRange(distinct
                    .Where(word => !(byWord.GetValueOrDefault(word.Id)?.IsInGracePeriod ?? false))
                    .OrderBy(_ => Random.Shared.Next())
                    .Take(RoundSize));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_words.Count == 0)
                return Empty(new(NativeWordAssociationNoticeKind.Warning,
                    NativeWordAssociationText.NoResourceVocabulary));
            for (var i = 0; i < _words.Count; i++)
                _clues[i] = [];
            return PublishReady();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Word Association could not load vocabulary.");
            _words.Clear();
            _clues.Clear();
            return Empty(new(NativeWordAssociationNoticeKind.Error, NativeWordAssociationText.LoadFailed));
        }
    }

    private List<Clue> Current =>
        _clues.TryGetValue(_index, out var list) ? list : _clues[_index] = [];

    private async Task<NativeWordAssociationState> CommandAsync(
        Func<NativeWordAssociationState> command, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            return command();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CancelInflight()
    {
        var previous = Interlocked.Exchange(ref _inflight, new CancellationTokenSource());
        // Not disposed: a grade may still be linking to its token.
        previous.Cancel();
    }

    /// <summary>True once Back has begun (commands then refuse) or the activity left.</summary>
    private bool Exited => _exited || _exit.HasBegun || _state.Destination != NativeDestination.Activity;

    private bool Active() => _launch is not null && owner.IsActive(_launch.UserId);

    private NativeWordAssociationState Empty(NativeWordAssociationNotice notice)
    {
        _notices = [notice];
        return Publish(new(NativePhase.Empty, Notices: _notices,
            ErrorKey: NativeWordAssociationText.NoVocabulary));
    }

    private NativeWordAssociationState PublishReady()
    {
        var word = _words[_index];
        return Publish(new(NativePhase.Ready,
            Index: _index,
            WordCount: _words.Count,
            TargetTerm: word.TargetLanguageTerm ?? string.Empty,
            NativeTerm: string.IsNullOrEmpty(word.NativeLanguageTerm) ? null : word.NativeLanguageTerm,
            Clues: Current.Select((clue, index) => clue.Snapshot(index)).ToImmutableArray(),
            Draft: _draft,
            IsGrading: _grading,
            Notices: _notices,
            HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId)));
    }

    private NativeWordAssociationState Refuse()
    {
        _generation++;
        if (_exited)
            return State;
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeWordAssociationText.Unavailable));
    }

    private NativeWordAssociationState Publish(NativeWordAssociationState state)
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
        _exited = true;

        _lifetime.Cancel();
        _inflight.Cancel();
    }

    private sealed class Clue(string text)
    {
        public string Text { get; } = text;
        public bool Graded { get; private set; }
        public bool Related { get; private set; }
        public bool IsCloze { get; private set; }
        public string? Explanation { get; private set; }
        public string? CorrectedText { get; private set; }

        public void Apply(ClueGrade grade)
        {
            Graded = true;
            Related = grade.Related;
            IsCloze = grade.IsCloze;
            Explanation = grade.Explanation;
            CorrectedText = grade.CorrectedText;
        }

        public NativeWordAssociationClue Snapshot(int index) =>
            new(index, Text, Graded, Related, IsCloze, Explanation, CorrectedText);
    }
}
