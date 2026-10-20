using System.Collections.Immutable;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

public sealed partial class NativeQuizPresenter
{
    private async Task<NativeQuizState> StartNewAsync(CancellationToken cancellationToken)
    {
        if (_launch is null || !HasOwner(_launch.UserId) || _contextKey is null || _profile is null)
            return RefuseLaunch();

        var all = _launch.FocusVocabularyIds.IsDefaultOrEmpty
            ? _reachableWords.Where(FocusVocabularySelection.IsUsableVocabularyWord).ToList()
            : FocusVocabularySelection.SelectFocusWords(
                _reachableWords, _launch.FocusVocabularyIds);
        var progressByWord = (await progress.GetByWordIdsForUserAsync(
                all.Select(word => word.Id).ToList(), _launch.UserId))
            .ToDictionary(item => item.VocabularyWordId, StringComparer.Ordinal);
        if (!HasOwner(_launch.UserId))
            return RefuseLaunch();

        var candidates = all.Select(word =>
        {
            var item = new VocabularyQuizItem
            {
                Word = word,
                Progress = progressByWord.GetValueOrDefault(word.Id)
                    ?? new VocabularyProgress { VocabularyWordId = word.Id, UserId = _launch.UserId },
                RequiresFullSessionDemonstration = !_launch.FocusVocabularyIds.IsDefaultOrEmpty,
                IsDueOnlySession = _launch.DueOnly
            };
            if (item.RequiresFullSessionDemonstration)
                item.CaptureKnownWordShortcutBaseline();
            return item;
        }).ToList();
        if (_launch.FocusVocabularyIds.IsDefaultOrEmpty)
        {
            var selected = await eligibility.SelectAsync(
                _launch.UserId, _launch.DueOnly, candidates, cancellationToken);
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();
            var reachableIds = candidates.Select(item => item.Word.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (selected.Batch is null || selected.Batch.Count > BatchSize
                || selected.Batch.Any(item =>
                    item is null
                    || !reachableIds.Contains(item.Word.Id)
                    || item.Progress?.UserId != _launch.UserId)
                || selected.Batch.Select(item => item.Word.Id)
                    .Distinct(StringComparer.Ordinal).Count() != selected.Batch.Count
                || selected.AllMastered
                    && (selected.Batch.Count > 0
                        || candidates.Any(item => !item.IsKnown)))
                throw new NotSupportedException(
                    "Quiz eligibility returned an unowned, duplicated, or unsupported batch.");

            _batch = selected.Batch.ToList();
            _allMasteredAtLaunch = selected.AllMastered;
        }
        else
        {
            _batch = candidates.Take(BatchSize).ToList();
            _allMasteredAtLaunch = false;
        }

        _round = [];
        _roundArchive = [];
        _clock.Stop();
        _pending = null;
        _overrideRetryPending = false;
        _correctionRetryPending = false;
        _needsResumeAfterCommitFailure = false;
        _pendingSync = pendingWrites.IsPending(_launch.UserId);
        _turnIndex = 0;
        _roundCursor = 0;
        _roundsCompleted = 0;
        _wordsMastered = 0;
        _totalTurns = 0;
        _correctCount = 0;
        _requiresCorrection = false;
        ResetSentenceSubmission();

        if (_batch.Count == 0)
        {
            if (_allMasteredAtLaunch)
            {
                // Source all-mastered-at-launch has no ActivitySession or
                // timer; the assigned plan card is completed only on Done.
                _allMasteredAtLaunch = true;
                return SetState(SummaryState());
            }
            return SetState(new(NativePhase.Empty, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.QuizEmpty));
        }

        await LoadSentenceHintsAsync(cancellationToken);
        if (_launch.PlanItemId.Length == 0 && string.IsNullOrWhiteSpace(_planItemRowId))
        {
            var adHoc = await planProgress.StartAdHocSessionAsync(
                _launch.UserId, PlanActivityType.VocabularyReview,
                !_launch.DueOnly && _launch.ResourceIds.Length == 1
                    ? _launch.ResourceIds[0] : null,
                _launch.SkillId, _batch.Select(item => item.Word.Id).ToArray(),
                estimatedMinutes: 10, cancellationToken);
            if (string.IsNullOrWhiteSpace(adHoc) || !HasOwner(_launch.UserId))
                return RefuseLaunch();
            using var lease = plans.ForOwner(_launch.UserId);
            var row = await lease.Database.DailyPlanCompletions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.UserProfileId == _launch.UserId
                    && item.Date == _launch.PlanDate.ToDateTime(
                        TimeOnly.MinValue, DateTimeKind.Utc)
                    && item.PlanItemId == adHoc && item.ActivityType == ActivityType
                    && !item.IsArchived, cancellationToken);
            if (row is null)
                return RefuseLaunch();
            _adHocPlanItemId = adHoc;
            _planItemRowId = row.Id;
        }

        _clock.Start(_adHocPlanItemId ?? _launch.PlanItemId, TimeSpan.Zero);
        try
        {
            return await SetupRoundAsync(cancellationToken);
        }
        catch
        {
            _clock.Stop();
            throw;
        }
    }

    private async Task<NativeQuizState> SetupRoundAsync(CancellationToken cancellationToken)
    {
        _batch.RemoveAll(item => item.ReadyToRotateOut);
        if (_batch.Count == 0)
            return await CompleteAsync(cancellationToken);

        var (window, nextCursor) = RoundSampler.NextWindow(
            _batch, _roundCursor, TurnsPerRound);
        _roundCursor = nextCursor;
        _round = window.OrderBy(_ => Guid.NewGuid()).ToList();
        _roundArchive = _round.ToList();
        _turnIndex = 0;
        _turnId = Guid.NewGuid().ToString("N");
        _pending = null;
        _requiresCorrection = false;
        _overrideRetryPending = false;
        _correctionRetryPending = false;
        _promptUsesNative = _displayDirection switch
        {
            "NativeToTarget" => true,
            "Mixed" => Random.Shared.Next(2) == 0,
            _ => false
        };
        _mode = _round[0].ChooseInteractionMode();
        var state = await PresentQuestionAsync(
            _promptUsesNative, _mode, graded: false, cancellationToken);
        if (state.Phase == NativePhase.Blocked)
        {
            _clock.Stop();
            return SetState(state);
        }
        await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
        return SetState(state with { HasPendingSync = _pendingSync });
    }

    private async Task<NativeQuizState> PresentQuestionAsync(
        bool promptUsesNative, string mode, bool graded, CancellationToken cancellationToken)
    {
        if (_launch is null || _profile is null || _turnIndex >= _round.Count)
            return RefuseLaunch();
        var item = _round[_turnIndex];
        _promptUsesNative = promptUsesNative;
        _mode = mode;
        _audioHeard = false; // Restored turns must re-engage with audio before answering.
        _heardAnswerChoices.Clear();
        media.StopPlayback();
        ResetSentenceHints(VocabQuizSentenceHintTransition.LoadCurrentItem);
        _feedbackStartedAt = null;

        var promptTerm = promptUsesNative
            ? item.Word.NativeLanguageTerm!
            : item.Word.TargetLanguageTerm!;
        var language = promptUsesNative
            ? _profile.NativeLanguage
            : item.Word.Language ?? _profile.TargetLanguage;
        var mediaError = false;
        try
        {
            _audioAvailable = quizPreferences.UseAudioPrompt
                && await media.CanPlayPromptAsync(promptTerm, language, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Quiz prompt audio is unavailable: {Failure}",
                ex.GetType().Name);
            _audioAvailable = false;
            mediaError = true;
        }
        _photoAsset = null;
        if (!string.IsNullOrWhiteSpace(item.Word.MnemonicImageUri)
            && (quizPreferences.UsePhotoPrompt || quizPreferences.ShowMnemonicImage))
        {
            try
            {
                _photoAsset = await media.LoadPhotoAsync(
                    item.Word.MnemonicImageUri, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Quiz photo could not be loaded: {Failure}",
                    ex.GetType().Name);
                mediaError = true;
            }
        }
        _photoLoaded = _photoAsset is not null;
        _photoDesired = _photoLoaded && quizPreferences.UsePhotoPrompt;
        _photoVisible = false;
        if (!HasOwner(_launch.UserId))
            return RefuseLaunch();
        if (!NativeQuizModality.CanPresent(
            promptUsesNative, mode, quizPreferences,
            !string.IsNullOrWhiteSpace(item.Word.MnemonicImageUri),
            _profile.VocabQuizShowTextWithPhoto,
            _audioAvailable, photoRendered: false))
            return BlockMediaTurn();

        if (mode == "MultipleChoice")
        {
            _choices = await choices.CreateAsync(
                _launch.UserId, item.Word, _reachableWords,
                promptUsesNative, cancellationToken);
            ValidateChoices(item.Word, promptUsesNative, _choices);
            _answerAudioAvailable = promptUsesNative
                && quizPreferences.UseAudioPrompt;
            if (_answerAudioAvailable)
            {
                foreach (var choice in _choices)
                {
                    bool canPlay;
                    try
                    {
                        canPlay = await media.CanPlayPromptAsync(
                            choice.Text, choice.AudioLanguage ?? _profile.TargetLanguage,
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException
                        || ex is OperationCanceledException
                            && !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Quiz answer audio unavailable: {Failure}",
                            ex.GetType().Name);
                        canPlay = false;
                        mediaError = true;
                    }
                    if (!canPlay)
                    {
                        _answerAudioAvailable = false;
                        break;
                    }
                }
            }
        }
        else
        {
            _choices = [];
            _answerAudioAvailable = false;
        }
        if (!HasOwner(_launch.UserId))
            return RefuseLaunch();

        _questionStartedAt = _clock.Elapsed;
        if (quizPreferences.AutoPlayVocabAudio && _audioAvailable)
        {
            try
            {
                _audioHeard = await PlayAudioAsync(
                    promptTerm, language, cancellationToken);
                if (!_audioHeard)
                {
                    _audioAvailable = false;
                    mediaError = true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (_disposed)
                    throw;
                logger.LogWarning("Quiz prompt autoplay unavailable: {Failure}",
                    ex.GetType().Name);
                _audioAvailable = false;
                mediaError = true;
            }
            if (_disposed)
                throw new OperationCanceledException();
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();
        }
        return QuestionState(graded, mediaError ? NativeText.ConnectionError : null);
    }

    private NativeQuizState QuestionState(bool graded, string? errorKey = null)
    {
        var word = _round[_turnIndex].Word;
        if (!NativeQuizModality.CanPresent(
            _promptUsesNative, _mode, quizPreferences,
            !string.IsNullOrWhiteSpace(word.MnemonicImageUri),
            _profile!.VocabQuizShowTextWithPhoto,
            _audioAvailable, _photoLoaded && _photoVisible))
            return BlockMediaTurn();

        var responseIsTarget = _mode == "Text"
            || _promptUsesNative && _choices.Length > 0
                && (!_answerAudioAvailable || _heardAnswerChoices.Count > 0);
        var correctFeedback = graded && _pending?.WasCorrect == true;
        var prompt = NativeQuizModality.Render(
            word, _promptUsesNative, quizPreferences,
            _profile!.VocabQuizShowTextWithPhoto,
            _audioAvailable, _audioHeard,
            _photoLoaded, _photoVisible,
            responseIsTarget, _profile.NativeLanguage,
            _profile.TargetLanguage,
            isAnswerFeedbackVisible: graded) with
        {
            Photo = _photoAsset,
            PhotoRequested = _photoAsset is not null
                && (_photoDesired || correctFeedback && quizPreferences.ShowMnemonicImage),
            CanTogglePhotoText = VocabQuizPhotoTextPolicy.IsTextToggleEligible(
                _photoAsset is not null, quizPreferences.UsePhotoPrompt, _promptUsesNative),
            ShowTextWithPhoto = _profile.VocabQuizShowTextWithPhoto
        };
        if (correctFeedback && _photoAsset is not null && _photoVisible)
            prompt = prompt with {
                ShowPhoto = true,
                PhotoUri = word.MnemonicImageUri
            };

        var completeForm = correctFeedback && _mode == "Text"
            ? VocabQuizSourcePolicy.EvaluateAnswer(
                "Text", _pending!.UserInput ?? string.Empty,
                _pending.ExpectedAnswer ?? string.Empty).CompleteForm
            : null;
        var hints = CurrentSentenceHints();
        return new NativeQuizState(
            graded ? NativePhase.Feedback : NativePhase.Ready,
            NativeDestination.VocabularyQuiz,
            prompt,
            _choices,
            _mode,
            graded ? _pending?.WasCorrect == true
                ? NativeText.QuizCorrect : NativeText.QuizIncorrect : null,
            graded ? _pending?.ExpectedAnswer : null,
            errorKey,
            RequiresCorrection: _requiresCorrection,
            CanSubmit: !graded && prompt.TargetLanguageEngaged,
            CurrentTurn: _turnIndex + 1,
            TurnsInRound: _round.Count,
            TotalTurns: _totalTurns,
            CorrectCount: _correctCount,
            RoundsCompleted: _roundsCompleted,
            Elapsed: _clock.Elapsed,
            HasPendingSync: _pendingSync,
            TurnId: _turnId,
            SelectedAnswer: graded ? _pending?.UserInput : null,
            AnswerWasCorrect: graded ? _pending?.WasCorrect : null,
            CompleteForm: completeForm,
            FeedbackMnemonicText: correctFeedback && quizPreferences.ShowMnemonicImage
                && !string.IsNullOrWhiteSpace(word.MnemonicText)
                    ? word.MnemonicText : null,
            FeedbackPhotoCandidateUri: correctFeedback && quizPreferences.ShowMnemonicImage
                && _photoAsset is not null && !string.IsNullOrWhiteSpace(word.MnemonicImageUri)
                    ? word.MnemonicImageUri : null)
        {
            SentenceHints = _sentenceHintsExpanded ? hints : [],
            CanShowSentenceHints = !hints.IsDefaultOrEmpty,
            SentenceHintsExpanded = _sentenceHintsExpanded,
            CanPlayAnswerAudio = _answerAudioAvailable,
            HeardAnswerChoiceIndices =
                _heardAnswerChoices.Order().ToImmutableArray(),
            FeedbackStartedAt = graded && !_requiresCorrection
                ? (_feedbackStartedAt ??= DateTimeOffset.UtcNow) : null,
            AutoAdvanceDurationMs = quizPreferences.AutoAdvanceDuration
        };
    }

    private NativeQuizState BlockMediaTurn()
    {
        _choices = [];
        _photoLoaded = false;
        _photoDesired = false;
        _photoVisible = false;
        _clock.Stop();
        logger.LogWarning("Quiz turn was refused because no safe prompt or target-language response is available.");
        return new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
            ErrorKey: NativeText.QuizUnavailable, HasPendingSync: _pendingSync);
    }

    private NativeQuizState SummaryState()
    {
        if (_launch is null || !HasOwner(_launch.UserId))
            return new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.QuizUnavailable);

        var reachable = _reachableWords.Select(word => word.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (_roundArchive.Any(item =>
            item.Progress?.UserId != _launch.UserId
            || item.Progress.VocabularyWordId != item.Word.Id
            || !reachable.Contains(item.Word.Id)))
            return new(NativePhase.Blocked, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.QuizUnavailable);

        var results = _roundArchive.Select(item => new NativeQuizResult(
            item.Word.Id, item.Word.TargetLanguageTerm!,
            item.Word.NativeLanguageTerm!, item.WasCorrectThisSession,
            item.ReadyToRotateOut))
            .ToImmutableArray();
        return new(
            NativePhase.Summary,
            NativeDestination.QuizSummary,
            CurrentTurn: _round.Count,
            TurnsInRound: _round.Count,
            TotalTurns: _totalTurns,
            CorrectCount: _correctCount,
            RoundsCompleted: _roundsCompleted,
            Elapsed: _clock.Elapsed,
            HasPendingSync: _pendingSync,
            WordsMastered: _wordsMastered,
            Results: results);
    }

    private void ValidateChoices(
        VocabularyWord question, bool promptUsesNative,
        ImmutableArray<NativeQuizChoice> options)
    {
        var answer = promptUsesNative
            ? question.TargetLanguageTerm!
            : question.NativeLanguageTerm!;
        var reachableAnswers = _reachableWords.Select(word =>
                promptUsesNative ? word.TargetLanguageTerm : word.NativeLanguageTerm)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (options.IsDefaultOrEmpty || options.Length is < 2 or > 4
            || options.Any(choice => !reachableAnswers.Contains(choice.Text))
            || options.Select(choice => choice.Text)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Length
            || options.Count(choice =>
                string.Equals(choice.Text, answer, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new NotSupportedException(
                "Quiz choices need at least two distinct owner-reachable answers, including exactly one correct answer.");
        }
    }

    private async Task SaveSnapshotAsync(bool summaryVisible, CancellationToken cancellationToken)
    {
        if (_launch is null || _contextKey is null
            || string.IsNullOrWhiteSpace(_planItemRowId) || _batch.Count == 0
            || _round.Count == 0 || !HasOwner(_launch.UserId))
            throw new InvalidOperationException("A current owner and question are required to persist quiz state.");

        var snapshot = new VocabQuizSessionSnapshot
        {
            PlanItemId = _launch.PlanItemId,
            FocusVocabularyIds = _launch.FocusVocabularyIds.IsDefault
                ? [] : _launch.FocusVocabularyIds,
            ResourceIds = _launch.ResourceIds.IsDefault ? [] : _launch.ResourceIds,
            DueOnly = _launch.DueOnly,
            SkillId = _launch.SkillId,
            BatchPool = _batch.Select(ToBatchSnapshot).ToArray(),
            RoundWordOrder = _round.Select(item => item.Word.Id).ToArray(),
            RoundCursor = _roundCursor,
            CurrentTurnInRound = _turnIndex,
            RoundsCompleted = _roundsCompleted,
            WordsMastered = _wordsMastered,
            TotalTurns = _totalTurns,
            CorrectCount = _correctCount,
            SessionItemsWordIds = _roundArchive.Select(item => item.Word.Id).ToArray(),
            PromptUsesNativeLanguage = _promptUsesNative,
            UserMode = _mode
        };
        var stored = new NativeQuizStoredState
        {
            UserId = _launch.UserId,
            PlanDate = _launch.PlanDate,
            LaunchContextKey = _contextKey,
            PlanItemRowId = _planItemRowId,
            AdHocPlanItemId = _adHocPlanItemId,
            IsRepractice = _launch.IsRepractice,
            DisplayDirection = _displayDirection!,
            TurnId = _turnId,
            Snapshot = snapshot,
            PendingAttempt = _pending,
            RequiresCorrection = _requiresCorrection,
            SummaryVisible = summaryVisible,
            ElapsedTicks = _clock.Elapsed.Ticks,
            SentenceSubmissionId = _sentenceSubmissionId,
            SentenceSourceInput = _sentenceInput,
            SentenceResults = _sentenceResults,
            SentenceCreditApplied = _sentenceCreditApplied
        };
        var session = await sessions.SaveSnapshotAsync(
            _launch.UserId, ActivityType, _contextKey, stored.Serialize())
            ?? throw new InvalidOperationException("Quiz snapshot persistence returned no session.");
        if (!HasOwner(_launch.UserId))
            throw new UnauthorizedAccessException(
                "Quiz owner changed during persistence; further writes are refused.");
        _resumable = session;
        _stored = stored;
        _needsResumeAfterCommitFailure = false;
    }

    private static VocabQuizBatchItemSnapshot ToBatchSnapshot(VocabularyQuizItem item) => new()
    {
        WordId = item.Word.Id,
        SessionCorrectCount = item.SessionCorrectCount,
        SessionMCCorrect = item.SessionMCCorrect,
        SessionTextCorrect = item.SessionTextCorrect,
        PendingRecognitionCheck = item.PendingRecognitionCheck,
        LostKnownThisSession = item.LostKnownThisSession,
        WasCorrectThisSession = item.WasCorrectThisSession,
        IsDueOnlySession = item.IsDueOnlySession,
        RequiresFullSessionDemonstration = item.RequiresFullSessionDemonstration,
        UseKnownWordShortcut = item.UseKnownWordShortcut,
        RecognitionDemonstrationsBaseline = item.RecognitionDemonstrationsBaseline,
        ProductionDemonstrationsBaseline = item.ProductionDemonstrationsBaseline
    };
}
