using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>
/// Cloze (source UI/Pages/Cloze.razor, activities.md 4). Eight AI sentences
/// with the target word blanked and the native translation shown; the learner
/// types the missing word (lenient on Korean particles) or picks one of five
/// target-language choices. Correct answers reveal the sentence and advance
/// after <see cref="AdvanceDelay"/>; a wrong answer locks input until the
/// learner navigates. Each answer writes a Clozure UserActivity and, for a
/// linked word, a weight 1.2 Sentence attempt. Unlike the source, every read
/// and write is bound to the launch owner and refused when that owner is
/// absent or changed, and an AI failure is shown as an error, not an empty list.
/// </summary>
public sealed class NativeClozePresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    NativeClozeSentenceSource sentences,
    UserActivityRepository activities,
    IServiceScopeFactory scopes,
    NativePendingWrites pendingWrites,
    ILogger<NativeClozePresenter> logger) : IDisposable
{
    public const int SentenceCount = 8;
    public const string AttemptActivity = "Clozure";
    public const float DifficultyWeight = 1.2f;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private List<NativeClozeChallenge> _sentences = [];
    private NativeActivityLaunch? _launch;
    private NativeClozeState _state = new(NativePhase.Idle);
    private string _mode = NativeClozeInputMode.Text;
    private int _index;
    private int _generation;
    private ImmutableArray<string> _options = [];
    private string _typed = string.Empty;
    private string? _selected;
    private bool _graded;
    private bool? _lastCorrect;
    private bool _revealed;
    private int _correct;
    private int _total;
    private string? _noticeKey;
    private bool? _noticeSuccess;
    private string? _errorKey;
    private bool _disposed;
    private bool _exited;

    /// <summary>Source GradeAnswer waits 1200 ms after a correct answer before advancing.</summary>
    public TimeSpan AdvanceDelay { get; set; } = TimeSpan.FromMilliseconds(1200);

    public NativeClozeState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeClozeText.Unavailable)
            : _state;

    public event Action<NativeClozeState>? StateChanged;

    public async Task<NativeClozeState> OpenAsync(
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
            if (launch.Route != NativeActivityCatalog.Cloze
                || !owner.IsActive(launch.UserId)
                || !await timer.StartAsync(launch, cancellationToken))
                return Refuse();
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Typing does not publish; the value is kept for Go and cleared when the sentence changes.</summary>
    public void SetTypedAnswer(string? value)
    {
        if (!_graded)
            _typed = value ?? string.Empty;
    }

    /// <summary>Go: grade the typed answer (Type mode) or the selected option (Choices mode).</summary>
    public Task<NativeClozeState> SubmitAsync(CancellationToken cancellationToken = default) =>
        GradeAsync(null, cancellationToken);

    /// <summary>Tapping a choice selects and grades it immediately.</summary>
    public Task<NativeClozeState> ChooseAsync(
        string option, CancellationToken cancellationToken = default) =>
        GradeAsync(option ?? string.Empty, cancellationToken);

    /// <summary>Type / Choices toggle: not persisted; allowed mid-sentence.</summary>
    public Task<NativeClozeState> ToggleModeAsync(CancellationToken cancellationToken = default) =>
        WithGameAsync(() =>
        {
            _mode = _mode == NativeClozeInputMode.Text
                ? NativeClozeInputMode.MultipleChoice : NativeClozeInputMode.Text;
            return PublishGame();
        }, cancellationToken);

    /// <summary>Next chevron; on the last sentence it opens the summary even if some were skipped.</summary>
    public Task<NativeClozeState> NextAsync(CancellationToken cancellationToken = default) =>
        WithGameAsync(NextCore, cancellationToken);

    public Task<NativeClozeState> PreviousAsync(CancellationToken cancellationToken = default) =>
        WithGameAsync(() =>
        {
            if (_index > 0)
                JumpTo(_index - 1);
            return PublishGame();
        }, cancellationToken);

    /// <summary>Summary "Continue practice": counters reset and eight new sentences load; the timer keeps running.</summary>
    public async Task<NativeClozeState> ContinuePracticeAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited || _state.Phase != NativePhase.Summary
                || _state.Destination != NativeDestination.Activity)
                return State;
            _correct = 0;
            _total = 0;
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Done / Go Back: stop and save the timer, then return to the Dashboard.</summary>
    public async Task<NativeClozeState> ExitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _generation++;
            _exited = true;
            string? errorKey = null;
            try
            {
                if (await timer.StopAsync(cancellationToken) == NativeActivityStop.Failed)
                    errorKey = NativeText.SaveFailed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Cloze could not save its activity minutes.");
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

    private async Task<NativeClozeState> WithGameAsync(
        Func<NativeClozeState> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited || _state.Phase != NativePhase.Ready
                || _state.Destination != NativeDestination.Activity
                || _sentences.Count == 0)
                return State;
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeClozeState> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var launch = _launch!;
        _generation++;
        _sentences = [];
        _index = 0;
        _errorKey = null;
        Publish(new(NativePhase.Loading, InputMode: _mode, Generation: _generation));
        try
        {
            var loaded = await sentences.GetSentencesAsync(launch, SentenceCount, cancellationToken);
            if (!owner.IsActive(launch.UserId))
                return Refuse();
            if (loaded.Count == 0)
                return Publish(new(NativePhase.Empty, InputMode: _mode,
                    ErrorKey: NativeClozeText.NoSentences, Generation: _generation));
            _sentences = loaded.ToList();
            JumpTo(0);
            return PublishGame();
        }
        catch (UnauthorizedAccessException)
        {
            return Refuse();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cloze could not generate sentences.");
            return Publish(new(NativePhase.Error, InputMode: _mode,
                ErrorKey: NativeClozeText.GenerationFailed, Generation: _generation));
        }
    }

    private async Task<NativeClozeState> GradeAsync(
        string? chosen, CancellationToken cancellationToken)
    {
        int advance;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            if (_exited || _state.Phase != NativePhase.Ready || _graded || _sentences.Count == 0
                || _state.Destination != NativeDestination.Activity)
                return State;
            if (chosen is not null)
            {
                if (_mode != NativeClozeInputMode.MultipleChoice || !_options.Contains(chosen))
                    return State;
                _selected = chosen;
            }
            var answer = _mode == NativeClozeInputMode.MultipleChoice ? _selected : _typed;
            if (string.IsNullOrWhiteSpace(answer))
                return State;
            var correct = await GradeCoreAsync(_sentences[_index], answer, cancellationToken);
            if (_exited || _state.Destination != NativeDestination.Activity
                || _state.Phase == NativePhase.Blocked)
                return State;
            if (!correct)
                return State;
            advance = _generation;
        }
        finally
        {
            _gate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        try
        {
            if (AdvanceDelay > TimeSpan.Zero)
                await Task.Delay(AdvanceDelay, linked.Token);
            await _gate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposal or a cancelled caller abandons only the auto-advance; the
            // graded sentence keeps its enabled chevrons, so nothing stays locked.
            return State;
        }
        try
        {
            // A manual next/previous, reload, or exit during the delay supersedes the advance.
            if (_exited || advance != _generation || _state.Phase != NativePhase.Ready
                || _state.Destination != NativeDestination.Activity)
                return State;
            if (_launch is null || !owner.IsActive(_launch.UserId))
                return Refuse();
            return NextCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> GradeCoreAsync(
        NativeClozeChallenge current, string answer, CancellationToken cancellationToken)
    {
        var userId = _launch!.UserId;
        // Choices are explicit, so they match strictly; typed answers are
        // lenient on Korean particles (ClozureAnswerHelper).
        var correct = _mode == NativeClozeInputMode.MultipleChoice
            ? string.Equals(answer.Trim(), current.Answer.Trim(), StringComparison.OrdinalIgnoreCase)
            : ClozureAnswerHelper.AreAnswersEquivalent(answer, current.Answer);
        _graded = true;
        _lastCorrect = correct;
        _revealed = correct;
        _total++;
        if (correct)
            _correct++;
        _noticeKey = null;
        _noticeSuccess = null;
        _errorKey = null;
        PublishGame();

        try
        {
            var saved = await activities.SaveAsync(NativeUserActivity.Create(
                userId, Activity.Clozure, answer, correct ? 100 : 0));
            if (saved < 0)
                _errorKey = NativeText.SaveFailed;
            else
                pendingWrites.MarkPending(userId);

            if (current.Word is { } word && owner.IsActive(userId))
            {
                using var scope = scopes.CreateScope();
                var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
                var updated = await progress.RecordAttemptAsync(new VocabularyAttempt
                {
                    VocabularyWordId = word.Id,
                    UserId = userId,
                    Activity = AttemptActivity,
                    InputMode = _mode,
                    WasCorrect = correct,
                    DifficultyWeight = DifficultyWeight,
                    ContextType = "Sentence",
                    UserInput = answer,
                    ExpectedAnswer = current.Answer
                });
                if (updated.IsFamiliar && owner.IsActive(userId))
                {
                    await progress.HandleVerificationProbeResultAsync(word.Id, userId, correct);
                    _noticeKey = correct
                        ? NativeClozeText.FamiliarConfirmed : NativeClozeText.FamiliarTricky;
                    _noticeSuccess = correct;
                }
                scope.ServiceProvider.GetService<ProgressCacheService>()?.InvalidateVocabSummary();
                pendingWrites.MarkPending(userId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cloze could not record an answer.");
            _errorKey = NativeText.SaveFailed;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsActive(userId))
        {
            Refuse();
            return false;
        }
        PublishGame();
        return correct;
    }

    private NativeClozeState NextCore()
    {
        if (_index >= _sentences.Count - 1)
        {
            _generation++;
            return Publish(new(NativePhase.Summary, InputMode: _mode,
                Index: _index, Total: _sentences.Count,
                SessionCorrect: _correct, SessionTotal: _total, ErrorKey: _errorKey,
                HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId),
                Generation: _generation));
        }
        JumpTo(_index + 1);
        return PublishGame();
    }

    /// <summary>Source SetCurrentChallenge: reshuffles choices and clears the answer, feedback and grading.</summary>
    private void JumpTo(int index)
    {
        _index = index;
        _generation++;
        _options = _sentences[index].Options
            .Select(option => option.Trim())
            .Where(option => option.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(_ => Random.Shared.Next())
            .ToImmutableArray();
        _typed = string.Empty;
        _selected = null;
        _graded = false;
        _lastCorrect = null;
        _revealed = false;
        _noticeKey = null;
        _noticeSuccess = null;
        _errorKey = null;
    }

    private NativeClozeState GameState()
    {
        var current = _sentences[_index];
        return new(
            NativePhase.Ready,
            DisplaySentence: _revealed ? current.SentenceText : current.BlankedSentence ?? current.SentenceText,
            Translation: current.Translation,
            Index: _index, Total: _sentences.Count, InputMode: _mode,
            Options: _options, TypedAnswer: _typed, SelectedOption: _selected,
            CorrectAnswer: _graded ? current.Answer : null,
            IsGraded: _graded, LastWasCorrect: _lastCorrect,
            SessionCorrect: _correct, SessionTotal: _total,
            NoticeKey: _noticeKey, NoticeIsSuccess: _noticeSuccess, ErrorKey: _errorKey,
            HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId),
            Generation: _generation);
    }

    private NativeClozeState PublishGame() => Publish(GameState());

    private NativeClozeState Refuse()
    {
        _generation++;
        return Publish(new(NativePhase.Blocked, InputMode: _mode,
            ErrorKey: NativeClozeText.Unavailable, Generation: _generation));
    }

    private NativeClozeState Publish(NativeClozeState state)
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
        timer.Dispose();
    }
}
