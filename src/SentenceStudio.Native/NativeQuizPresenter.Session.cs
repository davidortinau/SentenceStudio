using System.Collections.Immutable;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
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

        _clock.Start(_launch.PlanItemId, TimeSpan.Zero);
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

        if (mode == "MultipleChoice")
        {
            _choices = await choices.CreateAsync(
                _launch.UserId, item.Word, _reachableWords,
                promptUsesNative, cancellationToken);
            ValidateChoices(item.Word, promptUsesNative, _choices);
        }
        else
        {
            _choices = [];
        }
        if (!HasOwner(_launch.UserId))
            return RefuseLaunch();

        var promptTerm = promptUsesNative
            ? item.Word.NativeLanguageTerm!
            : item.Word.TargetLanguageTerm!;
        var language = promptUsesNative
            ? _profile.NativeLanguage
            : _profile.TargetLanguage;
        var mediaError = false;
        try
        {
            _audioAvailable = quizPreferences.UseAudioPrompt
                && await media.CanPlayPromptAsync(promptTerm, language, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Quiz prompt audio is offline; using written cue.");
            _audioAvailable = false;
            mediaError = true;
        }
        try
        {
            _photoLoaded = quizPreferences.UsePhotoPrompt
                && !string.IsNullOrWhiteSpace(item.Word.MnemonicImageUri)
                && await media.CanShowPhotoAsync(item.Word.MnemonicImageUri!, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Quiz photo is offline; using other cues.");
            _photoLoaded = false;
            mediaError = true;
        }
        if (mediaError)
            _pendingSync = true;
        _photoVisible = _photoLoaded;
        _questionStartedAt = _clock.Elapsed;
        if (!HasOwner(_launch.UserId))
            return RefuseLaunch();
        return QuestionState(graded, mediaError ? NativeText.ConnectionError : null);
    }

    private NativeQuizState QuestionState(bool graded, string? errorKey = null)
    {
        var word = _round[_turnIndex].Word;
        var responseIsTarget = _mode == "Text"
            || _promptUsesNative && _choices.Length > 0;
        var prompt = NativeQuizModality.Render(
            word, _promptUsesNative, quizPreferences,
            _profile!.VocabQuizShowTextWithPhoto,
            _audioAvailable, _audioHeard,
            _photoLoaded, _photoVisible,
            responseIsTarget, _profile.NativeLanguage,
            _profile.TargetLanguage);

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
            TurnId: _turnId);
    }

    private NativeQuizState SummaryState() => new(
        NativePhase.Summary,
        NativeDestination.QuizSummary,
        CurrentTurn: _round.Count,
        TurnsInRound: _round.Count,
        TotalTurns: _totalTurns,
        CorrectCount: _correctCount,
        RoundsCompleted: _roundsCompleted,
        Elapsed: _clock.Elapsed,
        HasPendingSync: _pendingSync);

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
            DisplayDirection = _displayDirection!,
            TurnId = _turnId,
            Snapshot = snapshot,
            PendingAttempt = _pending,
            RequiresCorrection = _requiresCorrection,
            SummaryVisible = summaryVisible,
            ElapsedTicks = _clock.Elapsed.Ticks
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
