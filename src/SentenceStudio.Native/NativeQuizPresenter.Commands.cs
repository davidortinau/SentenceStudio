using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

public sealed partial class NativeQuizPresenter
{
    public TimeSpan AutoAdvanceDelay =>
        TimeSpan.FromMilliseconds(quizPreferences.AutoAdvanceDuration);

    public async Task<NativeQuizState> PlayPromptAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Phase != NativePhase.Ready || !_audioAvailable)
                return RefuseInvalidAction();

            var word = _round[_turnIndex].Word;
            var text = _promptUsesNative ? word.NativeLanguageTerm! : word.TargetLanguageTerm!;
            var language = _promptUsesNative
                ? _profile!.NativeLanguage : _profile!.TargetLanguage;
            bool heard;
            string? errorKey = null;
            try
            {
                heard = await media.PlayPromptAsync(text, language, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Quiz prompt audio playback is offline.");
                heard = false;
                errorKey = NativeText.ConnectionError;
            }
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();
            if (!heard)
            {
                logger.LogWarning("Quiz prompt audio failed; restoring written cue.");
                _audioAvailable = false;
                errorKey ??= NativeText.QuizUnavailable;
            }
            _audioHeard = heard;
            return SetState(QuestionState(graded: false,
                errorKey: errorKey));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> SetPhotoVisibleAsync(
        bool visible, string turnId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_turnId != turnId || _turnIndex >= _round.Count
                || State.Phase is not (NativePhase.Ready or NativePhase.Feedback))
                return State;
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Overlay != NativeQuizOverlay.None || !_photoLoaded)
                return RefuseInvalidAction();
            _photoVisible = visible;
            return SetState(QuestionState(graded: State.Phase == NativePhase.Feedback));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> PhotoFailedAsync(
        string turnId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_turnId != turnId || _turnIndex >= _round.Count
                || State.Phase is not (NativePhase.Ready or NativePhase.Feedback))
                return State;
            if (_launch is null || !HasOwner(_launch.UserId))
                return RefuseInvalidAction();
            _photoLoaded = false;
            _photoVisible = false;
            logger.LogWarning("Quiz photo failed to load; switching to available text/audio cues.");
            return SetState(QuestionState(graded: State.Phase == NativePhase.Feedback,
                errorKey: NativeText.QuizUnavailable));
        }
        finally
        {
            _gate.Release();
        }
    }

    public NativeQuizState OpenDetails()
    {
        if (State.Phase is not (NativePhase.Ready or NativePhase.Feedback))
            return RefuseInvalidAction();
        return SetState(State with { Overlay = NativeQuizOverlay.LearningDetails });
    }

    public NativeQuizState OpenPhoto()
    {
        if (State.Phase is not (NativePhase.Ready or NativePhase.Feedback)
            || State.Prompt?.ShowPhoto != true)
            return RefuseInvalidAction();
        return SetState(State with { Overlay = NativeQuizOverlay.FullscreenPhoto });
    }

    public NativeQuizState DismissOverlay()
    {
        if (State.Overlay == NativeQuizOverlay.None)
            return RefuseInvalidAction();
        return SetState(State with { Overlay = NativeQuizOverlay.None });
    }

    public async Task<NativeQuizState> AnswerAsync(
        string answer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Phase != NativePhase.Ready || _pending is not null
                || State.Overlay != NativeQuizOverlay.None || State.Sentence is not null)
                return RefuseInvalidAction();

            if (string.IsNullOrWhiteSpace(answer))
            {
                logger.LogWarning("Quiz answer refused empty input.");
                return SetState(State with { ErrorKey = NativeText.AnswerRequired });
            }
            if (!State.CanSubmit)
            {
                logger.LogWarning("Quiz answer refused until a target-language cue or response is engaged.");
                return SetState(State with { ErrorKey = NativeText.QuizUnavailable });
            }

            var word = _round[_turnIndex].Word;
            var expected = _promptUsesNative
                ? word.TargetLanguageTerm! : word.NativeLanguageTerm!;
            if (_mode == "MultipleChoice" && !_choices.Any(option =>
                    string.Equals(option.Text, answer, StringComparison.Ordinal)))
            {
                logger.LogWarning("Quiz answer refused an option outside the visible choice set.");
                return SetState(State with { ErrorKey = NativeText.QuizUnavailable });
            }

            FuzzyMatchResult grade;
            try
            {
                grade = await grader.EvaluateAsync(
                    _launch.UserId, _mode, answer, expected, cancellationToken)
                    ?? throw new InvalidOperationException("Quiz grader returned no verdict.");
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Native quiz grading needs the shared source rule.");
                return SetState(State with {
                    Phase = NativePhase.Blocked,
                    ErrorKey = NativeText.QuizUnavailable,
                    CanSubmit = false });
            }
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();
            var correct = grade.IsCorrect;
            var item = _round[_turnIndex];
            if (correct)
            {
                item.SessionCorrectCount++;
                if (_mode == "MultipleChoice")
                {
                    item.SessionMCCorrect++;
                    item.PendingRecognitionCheck = false;
                }
                else
                {
                    item.SessionTextCorrect++;
                }
                item.WasCorrectThisSession = true;
            }
            else if (_mode == "Text")
            {
                item.PendingRecognitionCheck = true;
            }

            _pending = new VocabularyAttempt
            {
                VocabularyWordId = word.Id,
                UserId = _launch.UserId,
                Activity = "VocabularyQuiz",
                InputMode = _mode,
                WasCorrect = correct,
                DifficultyWeight = _mode == "Text" ? 1.5f : 1.0f,
                ContextType = "Isolated",
                UserInput = answer,
                ExpectedAnswer = expected,
                ResponseTimeMs = (int)Math.Clamp(
                    (_clock.Elapsed - _questionStartedAt).TotalMilliseconds, 0, int.MaxValue)
            };
            _totalTurns++;
            if (correct)
                _correctCount++;
            _requiresCorrection = !correct && _mode == "Text";

            try
            {
                // Q25a: no attempt write yet. Feedback is visible only after
                // the pending turn and its stable identity are durable.
                await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
                return SetState(QuestionState(graded: true)
                    with { HasPendingSync = _pendingSync });
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Failed to save graded quiz turn; it remains pending.");
                return SetState(State with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed,
                    CanSubmit = false, HasPendingSync = _pendingSync });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> RetryPendingSaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || _pending is null || State.Phase != NativePhase.Error
                || _overrideRetryPending || _correctionRetryPending)
                return RefuseInvalidAction();
            await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
            return SetState(QuestionState(graded: true)
                with { HasPendingSync = _pendingSync });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> OverrideAsCorrectAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Phase != NativePhase.Feedback
                    && !(State.Phase == NativePhase.Error && _overrideRetryPending)
                || _pending is null || _pending.WasCorrect)
                return RefuseInvalidAction();

            var item = _round[_turnIndex];
            var previousCorrect = item.SessionCorrectCount;
            var previousMultipleChoice = item.SessionMCCorrect;
            var previousText = item.SessionTextCorrect;
            var previousRecognitionCheck = item.PendingRecognitionCheck;
            var previousWasCorrect = item.WasCorrectThisSession;
            var previousCorrection = _requiresCorrection;
            item.ApplyOverrideAsCorrect(_mode);
            _pending.WasCorrect = true;
            _correctCount++;
            _requiresCorrection = false;
            try
            {
                await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
                _overrideRetryPending = false;
                return SetState(QuestionState(graded: true)
                    with { HasPendingSync = _pendingSync });
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Quiz override was not saved; the previous graded result remains pending.");
                item.SessionCorrectCount = previousCorrect;
                item.SessionMCCorrect = previousMultipleChoice;
                item.SessionTextCorrect = previousText;
                item.PendingRecognitionCheck = previousRecognitionCheck;
                item.WasCorrectThisSession = previousWasCorrect;
                _pending.WasCorrect = false;
                _correctCount--;
                _requiresCorrection = previousCorrection;
                _overrideRetryPending = true;
                return SetState(State with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed,
                    CanSubmit = false });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public NativeQuizState OpenSentenceShortcut()
    {
        if (_launch is null || !HasOwner(_launch.UserId)
            || State.Phase != NativePhase.Ready || _pending is not null)
            return RefuseInvalidAction();
        var word = _round[_turnIndex].Word;
        return SetState(State with {
            Mode = "Sentence",
            Sentence = NativeQuizModality.RenderSentence(
                word, _profile!.TargetLanguage),
            CanSubmit = false
        });
    }

    public NativeQuizState CancelSentenceShortcut()
    {
        if (State.Sentence is null)
            return RefuseInvalidAction();
        return SetState(QuestionState(graded: false));
    }

    public NativeQuizState SubmitSentence(string sentence)
    {
        if (_launch is null || !HasOwner(_launch.UserId)
            || State.Phase != NativePhase.Ready || State.Sentence is null
            || string.IsNullOrWhiteSpace(sentence))
            return RefuseInvalidAction();

        // The source target-ID/UsageCorrect matching and atomic sentence
        // attempt write still live in Razor. Do not interpret an AI grade
        // here or claim L2 production was credited before Shared owns it.
        logger.LogWarning("Native sentence submission blocked until shared target-use grading is available.");
        return SetState(State with {
            Phase = NativePhase.Blocked, ErrorKey = NativeText.QuizUnavailable });
    }

    public async Task<NativeQuizState> SubmitCorrectionAsync(
        string answer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Phase != NativePhase.Feedback
                    && !(State.Phase == NativePhase.Error && _correctionRetryPending)
                || _pending is null || !_requiresCorrection)
                return RefuseInvalidAction();
            if (!string.Equals(
                answer?.Trim(), _pending.ExpectedAnswer?.Trim(),
                StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Quiz correction did not match the shown target answer.");
                return SetState(State with { ErrorKey = NativeText.CorrectionRequired });
            }

            _requiresCorrection = false;
            try
            {
                await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
                _correctionRetryPending = false;
                return SetState(QuestionState(graded: true)
                    with { HasPendingSync = _pendingSync });
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Quiz correction confirmation was not saved.");
                _requiresCorrection = true;
                _correctionRetryPending = true;
                return SetState(State with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed,
                    CanSubmit = false });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Heads await this after displaying feedback. It never advances a
    /// different turn if the learner already pressed Next or navigated Back.
    /// </summary>
    public async Task<NativeQuizState> AutoAdvanceAsync(
        CancellationToken cancellationToken = default)
    {
        if (State.Phase != NativePhase.Feedback || _pending is null || _requiresCorrection)
            return State;

        var expectedTurnId = _turnId;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        try
        {
            await Task.Delay(AutoAdvanceDelay, linked.Token);
            return await NextCoreAsync(expectedTurnId, linked.Token);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return State;
        }
    }

    public Task<NativeQuizState> NextAsync(CancellationToken cancellationToken = default) =>
        NextCoreAsync(expectedTurnId: null, cancellationToken);

    private async Task<NativeQuizState> NextCoreAsync(
        string? expectedTurnId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (expectedTurnId is not null
                && (_turnId != expectedTurnId || State.Phase != NativePhase.Feedback))
                return State;
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Phase != NativePhase.Feedback || _pending is null
                || _requiresCorrection || State.Overlay != NativeQuizOverlay.None)
                return RefuseInvalidAction();

            try
            {
                var receipt = await committer.FindAsync(
                    _launch.UserId, _contextKey!, _turnId, cancellationToken);
                if (receipt is null)
                {
                    receipt = await committer.CommitAsync(
                        _launch.UserId, _contextKey!, _turnId, _pending, cancellationToken);
                    // A sync that finishes before this durable commit cannot
                    // acknowledge the new attempt. Mark it only afterwards.
                    pendingWrites.MarkPending(_launch.UserId);
                    _pendingSync = true;
                }
                else if (!pendingWrites.IsPending(_launch.UserId))
                {
                    pendingWrites.MarkPending(_launch.UserId);
                    _pendingSync = true;
                }
                if (!HasOwner(_launch.UserId))
                    return RefuseLaunch();
                return await AdvanceCommittedAsync(receipt, cancellationToken);
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Quiz scoring requires the shared atomic turn receipt.");
                return SetState(State with {
                    Phase = NativePhase.Blocked, ErrorKey = NativeText.QuizUnavailable });
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Quiz attempt write failed; question and pending attempt retained.");
                _needsResumeAfterCommitFailure = _pending is null;
                return SetState(State with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed,
                    HasPendingSync = _pendingSync });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeQuizState> AdvanceCommittedAsync(
        NativeQuizCommitReceipt receipt, CancellationToken cancellationToken)
    {
        if (_launch is null || _pending is null || receipt.TurnId != _turnId
            || receipt.Progress.UserId != _launch.UserId
            || receipt.Progress.VocabularyWordId != _pending.VocabularyWordId)
            throw new InvalidOperationException("Quiz attempt receipt does not match the active logical turn.");

        pendingWrites.RestoreUnmarkedCommittedTurns(_launch.UserId, 1);
        _pendingSync = pendingWrites.IsPending(_launch.UserId);
        var current = _round[_turnIndex];
        current.Progress = receipt.Progress;
        _pending = null;
        _requiresCorrection = false;
        if (current.ReadyToRotateOut)
        {
            _round.RemoveAt(_turnIndex);
            _wordsMastered++;
        }
        else
        {
            _turnIndex++;
        }

        if (_batch.All(item => item.ReadyToRotateOut))
        {
            _roundsCompleted++;
            _round = _roundArchive.ToList();
            _turnIndex = _round.Count;
            await SaveSnapshotAsync(summaryVisible: true, cancellationToken);
            return await CompleteAsync(cancellationToken);
        }

        if (_round.Count == 0 || _turnIndex >= _round.Count || _turnIndex >= TurnsPerRound)
        {
            _roundsCompleted++;
            // A complete round needs an on-disk summary so Done and Back
            // remain distinct after restart.
            _round = _roundArchive.ToList();
            _turnIndex = _round.Count;
            await SaveSnapshotAsync(summaryVisible: true, cancellationToken);
            return SetState(SummaryState() with { HasPendingSync = _pendingSync });
        }

        _turnId = Guid.NewGuid().ToString("N");
        _promptUsesNative = _displayDirection switch
        {
            "NativeToTarget" => true,
            "Mixed" => Random.Shared.Next(2) == 0,
            _ => false
        };
        _mode = _round[_turnIndex].ChooseInteractionMode();
        var next = await PresentQuestionAsync(
            _promptUsesNative, _mode, graded: false, cancellationToken);
        await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
        return SetState(next with { HasPendingSync = _pendingSync });
    }

    public async Task<NativeQuizState> ContinueRoundAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Phase != NativePhase.Summary || _pending is not null)
                return RefuseInvalidAction();
            return await SetupRoundAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> DoneAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State.Phase == NativePhase.Ready
                && State.Destination == NativeDestination.TodaysPlan)
                return State;
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Destination != NativeDestination.QuizSummary
                || State.Phase is not (NativePhase.Summary or NativePhase.Error)
                || _pending is not null)
                return RefuseInvalidAction();
            return await CompleteAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeQuizState> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_launch is null || !HasOwner(_launch.UserId) || _contextKey is null)
            return RefuseLaunch();
        _clock.Pause();
        var elapsed = _clock.Elapsed;
        var minutes = (int)Math.Floor(elapsed.TotalMinutes);
        try
        {
            if (committer is INativeQuizCompletion production)
            {
                if (string.IsNullOrWhiteSpace(_planItemRowId))
                    return RefuseLaunch();
                var completedInSqlite = _allMasteredAtLaunch
                    ? await production.CompleteAllMasteredAsync(
                        _launch, _planItemRowId, minutes, cancellationToken)
                    : await production.CompleteQuizAsync(
                        _launch, _contextKey, _planItemRowId, minutes, cancellationToken);
                if (!completedInSqlite || !HasOwner(_launch.UserId))
                    return RefuseLaunch();

                pendingWrites.MarkPending(_launch.UserId);
                _clock.Stop();
                _pendingSync = true;
                return SetState(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                    Elapsed: elapsed, HasPendingSync: true));
            }

            using var lease = plans.ForOwner(_launch.UserId);
            var planService = lease.Service;
            var plan = await planService.GetTodayAsync(cancellationToken);
            var card = plan?.Items.SingleOrDefault(item =>
                item.Id == _launch.PlanItemId
                && item.ActivityType == nameof(PlanActivityType.VocabularyReview));
            if (!HasOwner(_launch.UserId)
                || plan?.GeneratedForDate != _launch.PlanDate
                || card is null
                || !card.FocusVocabularyIds.SequenceEqual(
                    _launch.FocusVocabularyIds.IsDefault ? [] : _launch.FocusVocabularyIds))
                return RefuseLaunch();

            pendingWrites.MarkPending(_launch.UserId);
            _pendingSync = true;
            var completed = await planService.MarkCompleteAsync(
                _launch.PlanDate, _launch.PlanItemId, minutes, cancellationToken);
            if (completed is not { IsCompleted: true } || !HasOwner(_launch.UserId))
                return RefuseLaunch();

            if (!_allMasteredAtLaunch && _resumable is not null)
                await sessions.CompleteAsync(_launch.UserId, ActivityType, _contextKey);

            _clock.Stop();
            _pendingSync = true;
            return SetState(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                Elapsed: elapsed, HasPendingSync: true));
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Assigned quiz completion failed; Done remains retryable.");
            return SetState(SummaryState()
                with { Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed });
        }
    }

    public async Task<NativeQuizState> SuspendAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await SuspendCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> BackAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State.Overlay != NativeQuizOverlay.None)
                return DismissOverlay();
            if (State.Destination == NativeDestination.QuizSummary
                && State.Phase is NativePhase.Summary or NativePhase.Error)
                return await CompleteAsync(cancellationToken);
            if (_launch is null || !HasOwner(_launch.UserId))
                return RefuseLaunch();
            if (_needsResumeAfterCommitFailure)
            {
                // Q25b at the last turn can have an in-memory summary cursor
                // ahead of the durable pending snapshot. Never overwrite that
                // snapshot on Back; the receipt is the recovery authority.
                var staleElapsed = _clock.Stop();
                return SetState(new(NativePhase.Error, NativeDestination.TodaysPlan,
                    ErrorKey: NativeText.SaveFailed,
                    Elapsed: staleElapsed, HasPendingSync: true));
            }
            var saved = await SuspendCoreAsync(cancellationToken);
            if (saved.Phase == NativePhase.Error)
                return saved;
            var elapsed = _clock.Stop();
            return SetState(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                Elapsed: elapsed, HasPendingSync: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeQuizState> SuspendCoreAsync(CancellationToken cancellationToken)
    {
        if (_launch is null || !HasOwner(_launch.UserId))
            return RefuseLaunch();
        if (_needsResumeAfterCommitFailure)
        {
            logger.LogWarning("Quiz suspension refused to overwrite a stale snapshot after a committed attempt.");
            return SetState(State with {
                Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed });
        }
        if (_allMasteredAtLaunch)
            return State;
        _clock.Pause();
        try
        {
            await SaveSnapshotAsync(
                summaryVisible: State.Phase == NativePhase.Summary, cancellationToken);
            using var lease = plans.ForOwner(_launch.UserId);
            pendingWrites.MarkPending(_launch.UserId);
            _pendingSync = true;
            var saved = await lease.Service
                .UpdateProgressAsync(
                    _launch.PlanDate, _launch.PlanItemId,
                    (int)Math.Floor(_clock.Elapsed.TotalMinutes),
                    cancellationToken);
            if (!saved || !HasOwner(_launch.UserId))
                return RefuseLaunch();

            return SetState(State with {
                Elapsed = _clock.Elapsed, HasPendingSync = true });
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Quiz suspension could not persist its partial state.");
            return SetState(State with {
                Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed });
        }
    }

    public async Task<NativeQuizState> RetrySyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId))
                return RefuseLaunch();
            var syncRevision = pendingWrites.Revision(_launch.UserId);
            var result = await sync.SynchronizeAsync(_launch.UserId, cancellationToken);
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();
            _pendingSync = result != NativeSyncOutcome.Confirmed;
            if (!_pendingSync)
            {
                pendingWrites.MarkConfirmedIfUnchanged(_launch.UserId, syncRevision);
                _pendingSync = pendingWrites.IsPending(_launch.UserId);
            }
            return SetState(State with {
                HasPendingSync = _pendingSync,
                ErrorKey = _pendingSync ? NativeText.ConnectionError : null });
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Quiz sync retry is offline; local writes remain pending.");
            _pendingSync = true;
            return SetState(State with {
                HasPendingSync = true, ErrorKey = NativeText.ConnectionError });
        }
        finally
        {
            _gate.Release();
        }
    }

    private NativeQuizState RefuseInvalidAction()
    {
        logger.LogWarning("Native quiz refused an action outside its current state or user scope.");
        if (_launch is not null && !HasOwner(_launch.UserId))
            return RefuseLaunch();
        return SetState(State with {
            ErrorKey = NativeText.QuizUnavailable });
    }
}
