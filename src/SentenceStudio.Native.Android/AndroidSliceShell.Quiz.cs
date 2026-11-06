using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Vocabulary Quiz commands of the Compose head that render existing shared
/// presenter state: lettered answer audio, learning details, sentence hints,
/// "Use in a sentence", photo show/hide and Hide Text, photo render reports,
/// and Retry routed to the command whose save failed.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeQuizLaunch? _quizLaunch;
    private AndroidQuizFailedCommand _quizFailedCommand;
    private bool _quizMenuOpen;
    private string? _sentenceDraftTurnId;
    private FocusRequester? _correctionFocus;
    private FocusRequester? _detailsFocus;
    private string? _detailsFocusTurnId;
    private int _detailsFocusRestoreVersion;
    private CancellationTokenSource? _quizAutoPlay;
    private string? _quizAutoPlayTurnId;

    /// <summary>The "Use in a sentence" draft; a Compose TextFieldValue keeps Korean IME composition local.</summary>
    internal MutableState<TextFieldValue> SentenceDraft { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    /// <summary>Head-only quiz state that must never cross an account or quiz boundary.</summary>
    private void ResetQuizHeadState()
    {
        _quizFailedCommand = AndroidQuizFailedCommand.None;
        _quizMenuOpen = false;
        CancelQuizAutoPlay();
        _quizAutoPlayTurnId = null;
        _sentenceDraftTurnId = null;
        _detailsFocusTurnId = null;
        SentenceDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
    }

    /// <summary>Remembers which command's save failed so Retry repeats exactly that command.</summary>
    private void NoteQuizResult(AndroidQuizFailedCommand command, NativeQuizState state)
    {
        if (state.Phase == NativePhase.Error && state.ErrorKey == NativeText.SaveFailed)
            _quizFailedCommand = command;
        else if (state.Phase != NativePhase.Error)
            _quizFailedCommand = AndroidQuizFailedCommand.None;
    }

    /// <summary>The active owner whose photos and turns this head may show.</summary>
    private string? QuizPhotoOwner()
    {
        if (_scope is null)
            return null;
        if (!_scope.ServiceProvider.GetRequiredService<DeviceUserScopeProvider>()
                .TryGetUserProfileId(out var active)
            || string.IsNullOrWhiteSpace(active))
            return null;
        return _preview || HasDashboardOwner(Auth.UserId) && Auth.UserId == active
            ? active : null;
    }

    /// <summary>
    /// Plays a turn's prompt once after the turn is on screen, beside the quiz rather than as a
    /// busy action, as the source starts PlayWordAudio without awaiting it (Captain,
    /// 2026-11-06: see the quiz, then hear it). The presenter's StateChanged publishes the
    /// heard prompt or its playback failure.
    /// </summary>
    private void StartQuizAutoPlay(string owner, string turnId)
    {
        if (_disposed || _quiz is not { } quiz || QuizPhotoOwner() != owner
            || Quiz is not { AutoPlayPending: true } || Quiz.TurnId != turnId
            || string.Equals(_quizAutoPlayTurnId, turnId, StringComparison.Ordinal))
            return;
        _quizAutoPlayTurnId = turnId;
        CancelQuizAutoPlay();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _quizAutoPlay = cancellation;
        _ = RunQuizAutoPlayAsync(quiz, turnId, cancellation);
    }

    private async Task RunQuizAutoPlayAsync(
        NativeQuizPresenter quiz, string turnId, CancellationTokenSource cancellation)
    {
        try
        {
            await quiz.PlayAutoPromptAsync(turnId, cancellation.Token);
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested || _disposed)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(_quiz, quiz))
                ShowFailure(ex);
        }
        finally
        {
            Interlocked.CompareExchange(ref _quizAutoPlay, null, cancellation);
            cancellation.Dispose();
        }
    }

    /// <summary>An answer, a played sound or Back replaces the turn's autoplay.</summary>
    private void CancelQuizAutoPlay()
    {
        var current = Interlocked.Exchange(ref _quizAutoPlay, null);
        try
        {
            current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void PlayQuizChoice(int index) => Execute(async cancellationToken =>
    {
        CancelQuizAutoPlay();
        if (_quiz is not { } quiz || Quiz.TurnId is not { } turnId
            || !AndroidQuizTurnPolicy.CanPlayChoice(Quiz, busy: false))
            return;
        await ApplyQuizStateAsync(
            await quiz.PlayAnswerChoiceAsync(index, turnId, cancellationToken),
            cancellationToken);
    });

    public void OpenQuizDetails()
    {
        if (_quiz is null || !AndroidQuizTurnPolicy.CanOpenDetails(Quiz, Busy.Value))
            return;
        var turnId = Quiz.TurnId;
        var state = _quiz.OpenDetails();
        if (state.Overlay == NativeQuizOverlay.LearningDetails)
            _detailsFocusTurnId = turnId;
        ApplyQuizState(state);
    }

    /// <summary>After the details sheet closes, focus returns to its info button.</summary>
    private void QueueDetailsFocusRestore()
    {
        var turn = _detailsFocusTurnId;
        _detailsFocusTurnId = null;
        if (turn is null)
            return;
        Publish(() =>
        {
            if (Destination == NativeDestination.VocabularyQuiz
                && Quiz.TurnId == turn
                && Quiz.Overlay == NativeQuizOverlay.None
                && Quiz.Phase is NativePhase.Ready or NativePhase.Feedback)
                _detailsFocusRestoreVersion++;
        });
    }

    public void ToggleQuizSentenceHints()
    {
        if (_quiz is null
            || !AndroidQuizTurnPolicy.CanToggleSentenceHints(Quiz, Busy.Value))
            return;
        ApplyQuizState(_quiz.ToggleSentenceHints());
    }

    public void ToggleQuizPhotoVisible()
    {
        if (Busy.Value || !AndroidQuizPhotoPolicy.ShowsToggle(Quiz))
            return;
        SetPhotoVisible(Quiz.Prompt?.ShowPhoto != true);
    }

    public void ToggleQuizPhotoText() => Execute(async cancellationToken =>
    {
        Publish(() => _quizMenuOpen = false);
        if (_quiz is not { } quiz || Quiz.TurnId is not { } turnId
            || !AndroidQuizTurnPolicy.CanTogglePhotoText(Quiz, busy: false))
            return;
        await ApplyQuizStateAsync(await quiz.SetPhotoTextVisibleAsync(
            Quiz.Prompt?.ShowTextWithPhoto != true, turnId, cancellationToken),
            cancellationToken);
    });

    public void OpenQuizSentence()
    {
        Publish(() => _quizMenuOpen = false);
        if (_quiz is null || !AndroidQuizTurnPolicy.CanOpenSentence(Quiz, Busy.Value))
            return;
        CancelQuizAutoPlay();
        var state = _quiz.OpenSentenceShortcut();
        SentenceDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
        _sentenceDraftTurnId = state.Sentence is null ? null : state.TurnId;
        ApplyQuizState(state);
    }

    public void CancelQuizSentence()
    {
        if (_quiz is null || Busy.Value || Quiz.Sentence is null
            || Quiz.IsSentenceSubmitting
            || !Quiz.SentenceResults.IsDefaultOrEmpty)
            return;
        var state = _quiz.CancelSentenceShortcut();
        SentenceDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
        _sentenceDraftTurnId = null;
        ApplyQuizState(state);
    }

    public void SubmitQuizSentence() => Execute(async cancellationToken =>
    {
        var text = SentenceDraft.Value.Text ?? string.Empty;
        if (_quiz is not { } quiz || Quiz.Sentence is null
            || string.IsNullOrWhiteSpace(text))
            return;
        await ApplyQuizStateAsync(
            await quiz.SubmitSentenceAsync(text, cancellationToken), cancellationToken);
    });

    public void ContinueAfterQuizSentence() => Execute(async cancellationToken =>
    {
        if (_quiz is not { } quiz || Quiz.Sentence is null)
            return;
        var state = await quiz.ContinueSentenceShortcutAsync(cancellationToken);
        if (state.Sentence is null)
            Post(() =>
            {
                SentenceDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
                _sentenceDraftTurnId = null;
            });
        await ApplyQuizStateAsync(state, cancellationToken);
    });

    /// <summary>
    /// A sentence turn restored from a saved session (from any client) shows its
    /// saved draft; a turn whose grading was unavailable shows its sentences again.
    /// </summary>
    private void RestoreSentenceDraft(NativeQuizState state)
    {
        if (state.Sentence is null)
        {
            _sentenceDraftTurnId = null;
            return;
        }
        if (state.TurnId is null || _sentenceDraftTurnId == state.TurnId)
            return;
        var results = state.SentenceResults;
        SentenceDraft.Value = ComposeExtensions.NewTextFieldValue(state.SentenceDraft
            ?? (!results.IsDefaultOrEmpty && results.All(result => result.GradeUnavailable)
                ? string.Join("\n", results.Select(result => result.Sentence))
                : string.Empty));
        _sentenceDraftTurnId = state.TurnId;
    }

    /// <summary>Retry repeats the failed command and keeps the graded turn (AF-Q16).</summary>
    public void RetryQuiz() => Execute(async cancellationToken =>
    {
        if (_quiz is not { } quiz)
            return;
        var route = AndroidQuizTurnPolicy.Route(
            Quiz, _quizFailedCommand, _quizLaunch is not null);
        var turnId = Quiz.TurnId;
        NativeQuizState state;
        switch (route)
        {
            case AndroidQuizRetry.Done:
                state = await quiz.DoneAsync(cancellationToken);
                break;
            case AndroidQuizRetry.Sentence:
                state = await quiz.RetryPendingSentenceAsync(cancellationToken);
                break;
            case AndroidQuizRetry.Override:
                state = await quiz.OverrideAsCorrectAsync(cancellationToken);
                NoteQuizResult(AndroidQuizFailedCommand.Override, state);
                break;
            case AndroidQuizRetry.Correction:
                state = await quiz.SubmitCorrectionAsync(
                    Correction.Value.Text ?? string.Empty, cancellationToken);
                NoteQuizResult(AndroidQuizFailedCommand.Correction, state);
                break;
            case AndroidQuizRetry.PendingSave:
                state = await quiz.RetryPendingSaveAsync(cancellationToken);
                NoteQuizResult(_quizFailedCommand, state);
                break;
            case AndroidQuizRetry.Reopen when _quizLaunch is { } launch:
                await StartQuizAsync(launch, cancellationToken);
                return;
            default:
                return;
        }
        await ApplyQuizStateAsync(state, cancellationToken);
        if (!_preview && route is AndroidQuizRetry.Override or AndroidQuizRetry.Correction
                or AndroidQuizRetry.PendingSave
            && state is { Phase: NativePhase.Feedback, RequiresCorrection: false }
            && state.TurnId == turnId)
            _ = AutoAdvanceAsync(turnId);
    });

    /// <summary>
    /// Back from the quiz: closes an overlay first, saves the turn and returns to
    /// the Dashboard. When the presenter refuses to overwrite a newer committed
    /// turn it still routes to Today's Plan, so Back leaves (AF-Q16).
    /// </summary>
    private void BackFromQuiz() => Execute(async cancellationToken =>
    {
        CancelQuizAutoPlay();
        if (_quiz is null)
            return;
        var state = await _quiz.BackAsync(cancellationToken);
        if (AndroidQuizTurnPolicy.BackLeavesQuiz(state)
            && !(state is { Destination: NativeDestination.TodaysPlan,
                Phase: NativePhase.Ready }))
        {
            Publish(() =>
            {
                UpdateQuizState(state);
                Destination = NativeDestination.Dashboard;
            });
            if (_dashboard is not null)
                await _dashboard.LoadAsync(cancellationToken);
            return;
        }
        await ApplyQuizStateAsync(state, cancellationToken);
    });

    /// <summary>
    /// The presenter counts a photo as shown only after the head drew it. Compose
    /// composed the thumbnail from a decoded bitmap, so report it, without a busy
    /// flash, for exactly the owner, turn and photo that were drawn.
    /// </summary>
    private async Task ReportQuizPhotoRenderedAsync(
        string owner, string turnId, NativeQuizPhotoAsset asset)
    {
        if (_quiz is not { } quiz)
            return;
        try
        {
            await _actions.WaitAsync(_lifetime.Token);
            try
            {
                var current = quiz.State;
                if (_disposed || !ReferenceEquals(_quiz, quiz)
                    || QuizPhotoOwner() != owner
                    || current.TurnId != turnId
                    || !ReferenceEquals(current.Prompt?.Photo, asset)
                    || !AndroidQuizPhotoPolicy.NeedsRenderReport(current, owner)
                    || _media?.TryGetPhoto(asset, out _) != true)
                    return;
                ApplyQuizState(await quiz.PhotoRenderedAsync(turnId, _lifetime.Token));
            }
            finally
            {
                _actions.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
    }
}
