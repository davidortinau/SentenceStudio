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

    private async Task<bool> PlayAudioAsync(
        string text, string language, CancellationToken cancellationToken)
    {
        using var playback = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        Volatile.Write(ref _activeAudio, playback);
        try
        {
            return await media.PlayPromptAsync(text, language, playback.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeAudio, null, playback);
        }
    }

    private void StopActiveAudioBeforeNavigation(string? expectedTurnId = null)
    {
        if (_disposed || _launch is null || !HasOwner(_launch.UserId)
            || expectedTurnId is not null
                && (_turnId != expectedTurnId || State.Phase != NativePhase.Feedback))
            return;
        var playback = Volatile.Read(ref _activeAudio);
        if (playback is null)
            return;
        try
        {
            playback.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        media.StopPlayback();
    }

    public async Task<NativeQuizState> PlayPromptAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Phase != NativePhase.Ready || !_audioAvailable)
                return RefuseInvalidAction();

            var launch = _launch;
            var turnId = _turnId;
            var word = _round[_turnIndex].Word;
            var text = _promptUsesNative ? word.NativeLanguageTerm! : word.TargetLanguageTerm!;
            var language = _promptUsesNative
                ? _profile!.NativeLanguage : word.Language ?? _profile!.TargetLanguage;
            bool heard;
            string? errorKey = null;
            try
            {
                heard = await PlayAudioAsync(text, language, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Quiz prompt audio playback failed: {Failure}",
                    ex.GetType().Name);
                heard = false;
                errorKey = NativeText.ConnectionError;
            }
            if (_disposed)
                return new(NativePhase.Idle, NativeDestination.VocabularyQuiz);
            if (!ReferenceEquals(_launch, launch) || _turnId != turnId)
                return State;
            if (!HasOwner(launch.UserId))
                return RefuseLaunch();
            if (!heard)
            {
                logger.LogWarning("Quiz prompt audio failed.");
                _audioAvailable = false;
                errorKey ??= NativeText.ConnectionError;
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

    public async Task<NativeQuizState> PlayAnswerChoiceAsync(
        int optionIndex, string turnId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Phase is not (NativePhase.Ready or NativePhase.Feedback)
                || State.Overlay != NativeQuizOverlay.None || State.Sentence is not null
                || _turnId != turnId || !_promptUsesNative
                || _mode != "MultipleChoice" || !_answerAudioAvailable
                || optionIndex < 0 || optionIndex >= _choices.Length)
                return RefuseInvalidAction();

            var launch = _launch;
            var choice = _choices[optionIndex];
            bool heard;
            try
            {
                heard = await PlayAudioAsync(
                    choice.Text, choice.AudioLanguage ?? _profile!.TargetLanguage,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Quiz answer-choice audio unavailable: {Failure}",
                    ex.GetType().Name);
                heard = false;
            }
            if (_disposed)
                return new(NativePhase.Idle, NativeDestination.VocabularyQuiz);
            if (!ReferenceEquals(_launch, launch) || _turnId != turnId)
                return State;
            if (!HasOwner(launch.UserId))
                return RefuseLaunch();
            if (heard)
            {
                _heardAnswerChoices.Add(optionIndex);
                return SetState(QuestionState(
                    graded: State.Phase == NativePhase.Feedback));
            }
            _answerAudioAvailable = false;
            _heardAnswerChoices.Clear();
            _audioAvailable = false;
            return SetState(QuestionState(graded: State.Phase == NativePhase.Feedback,
                errorKey: NativeText.ConnectionError));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> SetPhotoTextVisibleAsync(
        bool showText, string turnId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || _profile is null || _turnId != turnId
                || State.Prompt?.CanTogglePhotoText != true
                || State.Phase is not (NativePhase.Ready or NativePhase.Feedback)
                || State.Overlay != NativeQuizOverlay.None)
                return RefuseInvalidAction();
            if (!await profiles.SaveVocabQuizShowTextWithPhotoAsync(
                    _launch.UserId, showText))
                return SetState(State with { ErrorKey = NativeText.SaveFailed });
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();
            _profile.VocabQuizShowTextWithPhoto = showText;
            pendingWrites.MarkPending(_launch.UserId);
            _pendingSync = true;
            return SetState(QuestionState(graded: State.Phase == NativePhase.Feedback)
                with { HasPendingSync = true });
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
            _photoDesired = visible;
            _photoVisible = false;
            return SetState(QuestionState(graded: State.Phase == NativePhase.Feedback));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> PhotoRenderedAsync(
        string turnId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_turnId != turnId || _turnIndex >= _round.Count
                || State.Phase is not (NativePhase.Ready or NativePhase.Feedback))
                return State;
            if (_launch is null || !HasOwner(_launch.UserId)
                || !_photoLoaded || State.Overlay != NativeQuizOverlay.None
                || State.Prompt?.PhotoRequested != true)
                return RefuseInvalidAction();
            _photoVisible = true;
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
            _photoDesired = false;
            _photoVisible = false;
            _photoAsset = null;
            logger.LogWarning("Quiz photo failed to load; checking the configured prompt.");
            return SetState(QuestionState(graded: State.Phase == NativePhase.Feedback,
                errorKey: NativeText.ConnectionError));
        }
        finally
        {
            _gate.Release();
        }
    }

    public NativeQuizState OpenDetails()
    {
        if (State.Phase is not (NativePhase.Ready or NativePhase.Feedback)
            || State.Overlay != NativeQuizOverlay.None
            || State.Sentence is not null)
            return RefuseInvalidAction();
        if (_launch is null || !HasOwner(_launch.UserId) || _turnIndex >= _round.Count)
            return RefuseInvalidAction();
        var item = _round[_turnIndex];
        var p = item.Progress;
        if (p is not null && (p.UserId != _launch.UserId
            || p.VocabularyWordId != item.Word.Id))
            return RefuseLaunch();
        var reason = item.PendingRecognitionCheck
            ? "NativeVocabQuiz_ModeRecognitionCheck"
            : _mode == "Text"
                ? item.RequiresFullSessionDemonstration && item.UseKnownWordShortcut
                    ? "NativeVocabQuiz_ModeKnownConfirmation"
                    : (p?.CurrentStreak ?? 0f) >= 3f
                        ? "NativeVocabQuiz_ModeLifetimeStreak"
                        : "NativeVocabQuiz_ModeMastery"
                : item.RequiresFullSessionDemonstration
                    ? "NativeVocabQuiz_ModeFocusRecognition"
                    : "NativeVocabQuiz_ModeBuildingRecognition";
        var details = new NativeQuizLearningDetails(
            p?.Status ?? LearningStatus.Unknown,
            p?.TotalAttempts ?? 0,
            p?.CorrectAttempts ?? 0,
            p?.Accuracy ?? 0f,
            p?.CurrentStreak ?? 0f,
            p?.ProductionInStreak ?? 0,
            p?.EffectiveStreak ?? 0f,
            p?.MasteryScore ?? 0f,
            p?.NextReviewDate,
            p?.ReviewInterval ?? 1,
            p?.EaseFactor ?? 2.5f,
            p?.IsDueForReview == true,
            _mode, _launch.DueOnly, reason,
            Math.Clamp(p?.QuizRecognitionDemonstrations ?? 0, 0, 3));
        return SetState(State with {
            Overlay = NativeQuizOverlay.LearningDetails,
            Details = details
        });
    }

    public NativeQuizState OpenPhoto()
    {
        if (State.Phase is not (NativePhase.Ready or NativePhase.Feedback)
            || State.Prompt?.Photo is null
            || State.Prompt.ShowPhoto != true
            || State.Overlay != NativeQuizOverlay.None
            || State.Sentence is not null)
            return RefuseInvalidAction();
        ResetSentenceHints(VocabQuizSentenceHintTransition.OpenFullscreen);
        return SetState(State with {
            Overlay = NativeQuizOverlay.FullscreenPhoto,
            SentenceHintsExpanded = false, SentenceHints = []
        });
    }

    public NativeQuizState DismissOverlay()
    {
        if (State.Overlay == NativeQuizOverlay.None)
            return RefuseInvalidAction();
        return SetState(State with { Overlay = NativeQuizOverlay.None, Details = null });
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
            var answerChoiceIndex = -1;
            if (_mode == "MultipleChoice")
                for (var index = 0; index < _choices.Length; index++)
                    if (string.Equals(_choices[index].Text, answer,
                            StringComparison.Ordinal))
                    {
                        answerChoiceIndex = index;
                        break;
                    }
            if (_mode == "MultipleChoice" && answerChoiceIndex < 0)
            {
                logger.LogWarning("Quiz answer refused an option outside the visible choice set.");
                return SetState(State with { ErrorKey = NativeText.QuizUnavailable });
            }
            if (_mode == "MultipleChoice" && _answerAudioAvailable
                && _promptUsesNative
                && !_heardAnswerChoices.Contains(answerChoiceIndex))
            {
                logger.LogWarning("Quiz refused an answer-choice letter before its target audio played.");
                return SetState(State with { ErrorKey = NativeText.AnswerRequired });
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
                _feedbackStartedAt = null;
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
        ResetSentenceSubmission();
        ResetSentenceHints(VocabQuizSentenceHintTransition.SentenceShortcut);
        return SetState(State with {
            Mode = "Sentence",
            Sentence = NativeQuizModality.RenderSentence(
                word, _profile!.TargetLanguage),
            CanSubmit = false,
            SentenceHintsExpanded = false,
            SentenceHints = [],
            SentenceResults = [],
            SentenceSuccessCount = 0
        });
    }

    public NativeQuizState CancelSentenceShortcut()
    {
        if (State.Sentence is null || State.IsSentenceSubmitting
            || _sentenceSubmissionId is not null)
            return RefuseInvalidAction();
        ResetSentenceSubmission();
        return SetState(QuestionState(graded: false));
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
                _feedbackStartedAt = null;
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
        var startedAt = State.FeedbackStartedAt;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        try
        {
            var elapsed = startedAt is { } start
                ? DateTimeOffset.UtcNow - start : TimeSpan.Zero;
            var remaining = AutoAdvanceDelay - elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, linked.Token);
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
        StopActiveAudioBeforeNavigation(expectedTurnId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (expectedTurnId is not null
                && (_turnId != expectedTurnId || State.Phase != NativePhase.Feedback
                    || State.Overlay != NativeQuizOverlay.None
                    || State.Sentence is not null))
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

        if (_batch.All(item => item.ReadyToRotateOut)
            || _round.Count == 0 || _turnIndex >= _round.Count
            || _turnIndex >= TurnsPerRound)
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
        if (next.Phase == NativePhase.Blocked)
        {
            _clock.Stop();
            return SetState(next);
        }
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
            if (_launch.IsRepractice)
            {
                if (!_allMasteredAtLaunch && _resumable is not null)
                    await sessions.CompleteAsync(
                        _launch.UserId, ActivityType, _contextKey);
                if (!HasOwner(_launch.UserId))
                    return RefuseLaunch();
                _clock.Stop();
                return SetState(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                    Elapsed: elapsed, HasPendingSync: _pendingSync));
            }
            if (_launch.PlanItemId.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(_adHocPlanItemId)
                    && !await planProgress.UpdatePlanItemProgressAsync(
                        _launch.UserId, _adHocPlanItemId, minutes, cancellationToken))
                    return RefuseLaunch();
                if (!_allMasteredAtLaunch && _resumable is not null)
                    await sessions.CompleteAsync(
                        _launch.UserId, ActivityType, _contextKey);
                if (!HasOwner(_launch.UserId))
                    return RefuseLaunch();
                if (!string.IsNullOrWhiteSpace(_adHocPlanItemId)
                    || _resumable is not null)
                    pendingWrites.MarkPending(_launch.UserId);
                _clock.Stop();
                _pendingSync = pendingWrites.IsPending(_launch.UserId);
                return SetState(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                    Elapsed: elapsed, HasPendingSync: _pendingSync));
            }
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
        StopActiveAudioBeforeNavigation();
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
            if (State.Phase == NativePhase.ResumeChoice)
                return SetState(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                    HasPendingSync: _pendingSync));
            if (State is { Phase: NativePhase.Blocked, Prompt: null,
                    ErrorKey: NativeText.QuizUnavailable })
                return SetState(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                    Elapsed: _clock.Stop(), HasPendingSync: _pendingSync));
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
        if (State is { Phase: NativePhase.Blocked, Prompt: null,
                ErrorKey: NativeText.QuizUnavailable })
            return State;
        if (_allMasteredAtLaunch)
            return State;
        _clock.Pause();
        try
        {
            await SaveSnapshotAsync(
                summaryVisible: State.Phase == NativePhase.Summary, cancellationToken);
            pendingWrites.MarkPending(_launch.UserId);
            _pendingSync = true;
            bool saved;
            if (_launch.PlanItemId.Length == 0)
                saved = !string.IsNullOrWhiteSpace(_adHocPlanItemId)
                    && await planProgress.UpdatePlanItemProgressAsync(
                        _launch.UserId, _adHocPlanItemId,
                        (int)Math.Floor(_clock.Elapsed.TotalMinutes),
                        cancellationToken);
            else if (_launch.IsRepractice)
                saved = true;
            else
            {
                using var lease = plans.ForOwner(_launch.UserId);
                saved = await lease.Service.UpdateProgressAsync(
                    _launch.PlanDate, _launch.PlanItemId,
                    (int)Math.Floor(_clock.Elapsed.TotalMinutes),
                    cancellationToken);
            }
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
