using System.Collections.Immutable;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

public sealed partial class NativeQuizPresenter
{
    private string? _sentenceSubmissionId;
    private string? _sentenceInput;
    private ImmutableArray<NativeQuizSentenceGrade> _sentenceResults = [];
    private bool _sentenceCreditApplied;

    public async Task<NativeQuizState> SubmitSentenceAsync(
        string input, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await SubmitSentenceCoreAsync(input, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeQuizState> RetryPendingSentenceAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sentenceInput is null || State.Sentence is null
                || State.Phase != NativePhase.Error)
                return RefuseInvalidAction();
            return await SubmitSentenceCoreAsync(_sentenceInput, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NativeQuizState> SubmitSentenceCoreAsync(
        string input, CancellationToken cancellationToken)
    {
        if (_launch is null || !HasOwner(_launch.UserId)
            || _contextKey is null || State.Sentence is null
            || State.Phase is not (NativePhase.Ready or NativePhase.Error)
            || State.Overlay != NativeQuizOverlay.None
            || _pending is not null || _turnIndex >= _round.Count)
            return RefuseInvalidAction();
        var entries = VocabQuizSentenceUsagePolicy.SplitEntries(input);
        if (entries.Count == 0)
            return SetState(State with { ErrorKey = NativeText.AnswerRequired });

        if (_sentenceCreditApplied
            && string.Equals(_sentenceInput, input, StringComparison.Ordinal))
        {
            if (State.Phase == NativePhase.Error)
            {
                try
                {
                    await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
                    return SetState(State with {
                        Phase = NativePhase.Ready, IsSentenceSubmitting = false,
                        SentenceResults = _sentenceResults,
                        SentenceSuccessCount = _sentenceResults.Count(
                            result => result.UsageCorrect && !result.GradeUnavailable),
                        TotalTurns = _totalTurns,
                        CorrectCount = _correctCount,
                        Elapsed = _clock.Elapsed,
                        ErrorKey = null, HasPendingSync = _pendingSync
                    });
                }
                catch (DbUpdateException)
                {
                    return State;
                }
            }
            ResetSentenceSubmission();
        }
        if (!string.Equals(_sentenceInput, input, StringComparison.Ordinal)
            || _sentenceSubmissionId is null)
        {
            _sentenceSubmissionId = Guid.NewGuid().ToString("N");
            _sentenceInput = input;
            _sentenceResults = [];
            _sentenceCreditApplied = false;
            try
            {
                await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Sentence submission could not be saved before grading.");
                return SetState(State with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed });
            }
        }
        SetState(State with {
            Phase = NativePhase.Loading, IsSentenceSubmitting = true,
            ErrorKey = null
        });
        if (_sentenceResults.IsDefaultOrEmpty)
        {
            var word = _round[_turnIndex].Word;
            var results = ImmutableArray.CreateBuilder<NativeQuizSentenceGrade>(
                entries.Count);
            foreach (var sentence in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!HasOwner(_launch.UserId))
                    return RefuseLaunch();
                try
                {
                    results.Add(await sentenceGrader.GradeAsync(
                        _launch.UserId, sentence, word,
                        _profile!.TargetLanguage, _profile.NativeLanguage,
                        cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Provider errors can echo the learner's sentence; do not log the exception.
                    results.Add(new NativeQuizSentenceGrade(sentence, false,
                        LocalizationManager.Instance.GetString(
                            NativeText.SentenceGradeUnavailable,
                            CultureInfo.CurrentUICulture),
                        null, null, "", "", "", "", GradeUnavailable: true));
                }
            }
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();
            _sentenceResults = results.MoveToImmutable();
            try
            {
                await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Sentence results could not be saved before scoring.");
                return SetState(State with {
                    Phase = NativePhase.Error, IsSentenceSubmitting = false,
                    ErrorKey = NativeText.SaveFailed });
            }
        }

        try
        {
            var receipt = await sentenceCommitter.FindAsync(
                _launch.UserId, _contextKey, _turnId,
                _sentenceSubmissionId!, cancellationToken)
                ?? await sentenceCommitter.CommitAsync(
                    _launch.UserId, _contextKey, _turnId, _sentenceSubmissionId!,
                    _sentenceResults, cancellationToken);
            if (!HasOwner(_launch.UserId))
                return RefuseLaunch();
            ApplySentenceReceipt(receipt);
            await SaveSnapshotAsync(summaryVisible: false, cancellationToken);
            return SetState(State with {
                Phase = NativePhase.Ready, Mode = "Sentence",
                Sentence = NativeQuizModality.RenderSentence(
                    _round[_turnIndex].Word, _profile!.TargetLanguage),
                SentenceResults = _sentenceResults,
                SentenceSuccessCount = receipt.SuccessCount,
                IsSentenceSubmitting = false,
                TotalTurns = _totalTurns,
                CorrectCount = _correctCount,
                Elapsed = _clock.Elapsed,
                HasPendingSync = _pendingSync,
                ErrorKey = null
            });
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Sentence attempt was not durably acknowledged.");
            return SetState(State with {
                Phase = NativePhase.Error, IsSentenceSubmitting = false,
                ErrorKey = NativeText.SaveFailed });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Sentence receipt could not be verified.");
            return SetState(State with {
                Phase = NativePhase.Error, IsSentenceSubmitting = false,
                ErrorKey = NativeText.SaveFailed });
        }
    }

    private void ApplySentenceReceipt(NativeQuizSentenceCommitReceipt receipt)
    {
        if (_sentenceCreditApplied)
            return;
        if (_launch is null || !HasOwner(_launch.UserId)
            || receipt.SubmissionId != _sentenceSubmissionId
            || receipt.Results.IsDefaultOrEmpty
            || receipt.SuccessCount != receipt.Results.Count(
                result => result.UsageCorrect && !result.GradeUnavailable)
            || receipt.SuccessCount > 0 && (receipt.Progress is null
                || receipt.Progress.UserId != _launch.UserId
                || receipt.Progress.VocabularyWordId != _round[_turnIndex].Word.Id))
            throw new InvalidOperationException("Sentence receipt does not match the quiz turn.");
        _sentenceResults = receipt.Results;
        var item = _round[_turnIndex];
        if (receipt.Progress is not null)
            item.Progress = receipt.Progress;
        item.SessionCorrectCount += receipt.SuccessCount;
        item.SessionTextCorrect += receipt.SuccessCount;
        if (receipt.SuccessCount > 0)
            item.WasCorrectThisSession = true;
        _totalTurns += receipt.Results.Length;
        _correctCount += receipt.SuccessCount;
        _sentenceCreditApplied = true;
        pendingWrites.MarkPending(_launch.UserId);
        _pendingSync = true;
    }

    public async Task<NativeQuizState> ContinueSentenceShortcutAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is null || !HasOwner(_launch.UserId)
                || State.Sentence is null || !_sentenceCreditApplied
                || State.Phase != NativePhase.Ready
                || _pending is not null || _turnIndex >= _round.Count)
                return RefuseInvalidAction();

            var previousRound = _round.ToList();
            var previousTurn = _turnIndex;
            var previousTurnId = _turnId;
            var previousMastered = _wordsMastered;
            var previousRounds = _roundsCompleted;
            var previousSubmission = _sentenceSubmissionId;
            var previousInput = _sentenceInput;
            var previousResults = _sentenceResults;
            var previousCredited = _sentenceCreditApplied;
            var previousDirection = _promptUsesNative;
            var previousMode = _mode;
            var previousPhoto = _photoAsset;
            var previousPhotoDesired = _photoDesired;
            var previousPhotoLoaded = _photoLoaded;
            var previousPhotoVisible = _photoVisible;
            var previousAudioAvailable = _audioAvailable;
            var previousAudioHeard = _audioHeard;
            var previousAnswerAudio = _answerAudioAvailable;
            try
            {
                if (_round[_turnIndex].ReadyToRotateOut)
                {
                    _round.RemoveAt(_turnIndex);
                    _wordsMastered++;
                }
                else
                    _turnIndex++;
                ResetSentenceSubmission();

                if (_round.Count == 0 || _turnIndex >= _round.Count
                    || _turnIndex >= TurnsPerRound)
                {
                    _roundsCompleted++;
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
            catch (DbUpdateException ex)
            {
                _round = previousRound;
                _turnIndex = previousTurn;
                _turnId = previousTurnId;
                _wordsMastered = previousMastered;
                _roundsCompleted = previousRounds;
                _sentenceSubmissionId = previousSubmission;
                _sentenceInput = previousInput;
                _sentenceResults = previousResults;
                _sentenceCreditApplied = previousCredited;
                _promptUsesNative = previousDirection;
                _mode = previousMode;
                _photoAsset = previousPhoto;
                _photoDesired = previousPhotoDesired;
                _photoLoaded = previousPhotoLoaded;
                _photoVisible = previousPhotoVisible;
                _audioAvailable = previousAudioAvailable;
                _audioHeard = previousAudioHeard;
                _answerAudioAvailable = previousAnswerAudio;
                logger.LogWarning(ex, "Sentence turn did not advance durably.");
                return SetState(State with {
                    Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ResetSentenceSubmission()
    {
        _sentenceSubmissionId = null;
        _sentenceInput = null;
        _sentenceResults = [];
        _sentenceCreditApplied = false;
    }
}
