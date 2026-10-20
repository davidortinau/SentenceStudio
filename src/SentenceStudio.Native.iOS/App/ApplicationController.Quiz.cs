using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features.Quiz;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

internal sealed partial class ApplicationController
{
    private enum QuizAudioNavigation { None, Next, Back }

    private CancellationTokenSource? _quizAudioCancellation;
    private QuizAudioNavigation _quizNavigationAfterAudio;

    internal bool CanNavigateBackDuringQuizAudio =>
        IsQuizOpen && _quizAudioCancellation is not null;

    internal NativeQuizState QuizViewState => _feedbackAutoAdvanceCanceled
        ? _quizState with { FeedbackStartedAt = null }
        : _autoAdvanceDisplayStartedAt is { } restart
            ? _quizState with { FeedbackStartedAt = restart }
            : _quizState;
    internal NativeQuizPhotoAsset? QuizPhotoAsset => _quizState.Prompt?.Photo;
    internal bool IsQuizPhotoPresented =>
        _quizState.Overlay == NativeQuizOverlay.FullscreenPhoto;
    internal string QuizPhotoAccessibilityLabel => Text("VocabQuiz_PhotoHint");
    internal string QuizPhotoCloseLabel => Text("Common_Close");
    internal bool CanOpenQuizSentence =>
        _quizState is { Phase: NativePhase.Ready, Sentence: null };
    internal bool CanToggleQuizPhotoText =>
        _quizState is { Phase: NativePhase.Ready,
            Prompt.CanTogglePhotoText: true, TurnId: not null };
    internal string QuizPhotoTextTitle => Text(_quizState.Prompt?.ShowTextWithPhoto == true
        ? "VocabQuiz_HideText" : "VocabQuiz_ShowText");

    internal void OpenQuizSentence()
    {
        if (_quiz is null || !CanOpenQuizSentence)
            return;
        CancelAutoAdvance();
        _sentenceInput = string.Empty;
        _quizState = _quiz.OpenSentenceShortcut();
        _sentenceDraftRestoredTurn = _quizState.TurnId;
        Publish();
    }

    internal void ToggleQuizPhotoText()
    {
        if (_quiz is null || !CanToggleQuizPhotoText
            || _quizState.TurnId is not { } turnId)
            return;
        var next = !_quizState.Prompt!.ShowTextWithPhoto;
        Run(async () => _quizState = await _quiz.SetPhotoTextVisibleAsync(
            next, turnId, _lifetime.Token));
    }

    internal void ToggleQuizPhotoVisibility()
    {
        if (_quiz is null || _busy || _quizState.Prompt?.Photo is null
            || _quizState.TurnId is not { } turnId)
            return;
        var visible = !_quizState.Prompt.ShowPhoto;
        Run(async () => _quizState = await _quiz.SetPhotoVisibleAsync(
            visible, turnId, _lifetime.Token));
    }

    internal void OpenQuizPhoto()
    {
        if (_quiz is null || _busy || _quizState.Prompt?.Photo is null
            || _quizState.Prompt.ShowPhoto != true
            || _quizState.Phase is not (NativePhase.Ready or NativePhase.Feedback))
            return;
        _quizState = _quiz.OpenPhoto();
        Publish();
    }

    internal void DismissQuizPhoto()
    {
        if (_quiz is null
            || _quizState.Overlay != NativeQuizOverlay.FullscreenPhoto)
            return;
        _quizState = _quiz.DismissOverlay();
        Publish();
    }

    internal void OnQuizPhotoRendered(string turnId)
    {
        if (_quiz is null || _busy || _quizState.TurnId != turnId
            || _quizState.Prompt is not {
                PhotoRequested: true, ShowPhoto: false, Photo: not null }
            || _quizState.Overlay != NativeQuizOverlay.None)
            return;
        Run(async () => _quizState = await _quiz.PhotoRenderedAsync(
            turnId, _lifetime.Token));
    }

    internal void OnQuizPhotoFailed(string turnId, string code)
    {
        if (_quiz is null || _busy || _quizState.TurnId != turnId
            || _quizState.Prompt?.Photo is null)
            return;
        _logger?.LogWarning(
            "Native SwiftUI photo failed to render for this quiz turn ({FailureCode}).",
            code);
        Run(async () => _quizState = await _quiz.PhotoFailedAsync(
            turnId, _lifetime.Token));
    }

    internal SwiftView RenderQuiz(SourceUiStyle style)
    {
        if (_quiz is null)
            return Views.Text(Text(NativeText.QuizUnavailable));
        if (_quizState.Sentence is not null
            && _sentenceDraftRestoredTurn != _quizState.TurnId)
        {
            var results = _quizState.SentenceResults;
            _sentenceInput = _quizState.SentenceDraft
                ?? (!results.IsDefaultOrEmpty
                    && results.All(result => result.GradeUnavailable)
                        ? string.Join("\n", results.Select(result => result.Sentence))
                        : string.Empty);
            _sentenceDraftRestoredTurn = _quizState.TurnId;
        }
        var canRetry = _quizState.Phase == NativePhase.Error
            && ((_quizState.Destination == NativeDestination.QuizSummary
                    && _quizState.ErrorKey == NativeText.SaveFailed)
                || (_quizState.Sentence is not null
                    && _quizState.ErrorKey == NativeText.SaveFailed)
                || (_quizState.Prompt is not null
                    && _quizState.ErrorKey == NativeText.SaveFailed)
                || (_quizState.CurrentTurn == 0
                    && _quizState.ErrorKey == NativeText.ConnectionError
                    && _quizLaunch is not null));
        return QuizView.Page(style, QuizViewState,
            new QuizViewInputs(_answer, _quizCorrectionCompleted, _busy,
                canRetry, _quizState.RequiresCorrection || _quizInputFocused,
                _sentenceInput),
            Text, new QuizActions(
            AnswerChanged: value => _answer = value,
            AnswerFocusChanged: focused => _quizInputFocused = focused,
            CanSelectAudioChoice: index =>
                !_quizState.HeardAnswerChoiceIndices.IsDefault
                && _quizState.HeardAnswerChoiceIndices.Contains(index),
            SelectChoice: value =>
            {
                if (_busy || _quizState.Phase != NativePhase.Ready)
                    return;
                if (_quizState.Prompt?.IsTargetLanguagePrompt == false
                    && _quizState.CanPlayAnswerAudio)
                {
                    var index = -1;
                    for (var option = 0; option < _quizState.Choices.Length; option++)
                        if (string.Equals(_quizState.Choices[option].Text, value,
                                StringComparison.Ordinal))
                        {
                            index = option;
                            break;
                        }
                    if (_quizState.HeardAnswerChoiceIndices.IsDefault
                        || !_quizState.HeardAnswerChoiceIndices.Contains(index))
                        return;
                }
                _quizState = _quizState with { SelectedAnswer = value };
                Run(async () =>
                {
                    _quizState = await _quiz.AnswerAsync(value, _lifetime.Token);
                    ScheduleAutoAdvance();
                });
            },
            PlayPrompt: () => PlayQuizAudio(
                token => _quiz.PlayPromptAsync(token)),
            PlayChoiceAudio: index =>
            {
                if (_quizState.TurnId is not { } turnId)
                    return;
                PlayQuizAudio(token => _quiz.PlayAnswerChoiceAsync(
                    index, turnId, token));
            },
            SubmitText: () =>
            {
                if (_busy || _quizState.Phase != NativePhase.Ready
                    || string.IsNullOrWhiteSpace(_answer))
                    return;
                Run(async () =>
                {
                    _quizState = await _quiz.AnswerAsync(_answer, _lifetime.Token);
                    _quizCorrectionCompleted = false;
                    if (_quizState.RequiresCorrection)
                    {
                        _answer = string.Empty;
                        _quizInputFocused = true;
                    }
                    else
                        _quizInputFocused = false;
                    ScheduleAutoAdvance();
                });
            },
            SubmitCorrection: () =>
            {
                if (_busy || !_quizState.RequiresCorrection
                    || string.IsNullOrWhiteSpace(_answer))
                    return;
                Run(async () =>
                {
                    _quizState = await _quiz.SubmitCorrectionAsync(
                        _answer, _lifetime.Token);
                    if (!_quizState.RequiresCorrection)
                    {
                        _quizCorrectionCompleted = _quizState.Phase ==
                            NativePhase.Feedback;
                        _quizInputFocused = false;
                    }
                    ScheduleAutoAdvance();
                });
            },
            OverrideAsCorrect: () =>
            {
                CancelAutoAdvance();
                Run(async () =>
                {
                    _quizState = await _quiz.OverrideAsCorrectAsync(_lifetime.Token);
                    ScheduleAutoAdvance();
                });
            },
            Next: AdvanceQuiz,
            Resume: () =>
            {
                CancelAutoAdvance();
                Run(async () => _quizState = await _quiz.ResumeAsync(_lifetime.Token));
            },
            StartFresh: () =>
            {
                CancelAutoAdvance();
                Run(async () => _quizState = await _quiz.StartFreshAsync(_lifetime.Token));
            },
            ContinueRound: () =>
            {
                CancelAutoAdvance();
                Run(async () =>
                {
                    _quizState = await _quiz.ContinueRoundAsync(_lifetime.Token);
                    _destination = NativeDestination.VocabularyQuiz;
                });
            },
            Done: () =>
            {
                CancelAutoAdvance();
                Run(ReturnFromQuizAsync);
            },
            Retry: () => Run(RetryQuizAsync),
            OpenDetails: OpenQuizDetails,
            ToggleSentenceHints: ToggleQuizSentenceHints,
            PhotoRendered: OnQuizPhotoRendered,
            PhotoFailed: OnQuizPhotoFailed,
            OpenPhoto: OpenQuizPhoto,
            TogglePhotoVisible: ToggleQuizPhotoVisibility,
            SentenceChanged: value => _sentenceInput = value,
            SubmitSentence: () =>
            {
                if (_busy || string.IsNullOrWhiteSpace(_sentenceInput)
                    || _quizState.Sentence is null)
                    return;
                Run(async () =>
                    _quizState = await _quiz.SubmitSentenceAsync(
                        _sentenceInput, _lifetime.Token));
            },
            CancelSentence: CancelQuizSentence,
            ContinueAfterSentence: () => Run(async () =>
            {
                _quizState = await _quiz.ContinueSentenceShortcutAsync(
                    _lifetime.Token);
                _sentenceInput = string.Empty;
                _sentenceDraftRestoredTurn = null;
                if (_quizState.Phase == NativePhase.Summary)
                    _destination = NativeDestination.QuizSummary;
            })));
    }

    internal SwiftView RenderQuizContent(SourceUiStyle style, out SwiftView? photoCover) =>
        QuizView.WithToolbarAndPhoto(RenderQuiz(style), QuizViewState, QuizPhotoAsset,
            CanOpenQuizSentence, CanToggleQuizPhotoText, QuizPhotoTextTitle,
            QuizPhotoAccessibilityLabel, QuizPhotoCloseLabel,
            IsQuizPhotoPresented, Text,
            OpenQuizSentence, ToggleQuizPhotoText, DismissQuizPhoto,
            OnQuizPhotoFailed, out photoCover);

    private void ToggleQuizSentenceHints()
    {
        if (_quiz is null || !_quizState.CanShowSentenceHints)
            return;
        _quizState = _quiz.ToggleSentenceHints();
        Publish();
    }

    private void CancelQuizSentence()
    {
        if (_quiz is null || _quizState.Sentence is null
            || _quizState.IsSentenceSubmitting
            || !_quizState.SentenceResults.IsDefaultOrEmpty)
            return;
        _quizState = _quiz.CancelSentenceShortcut();
        _sentenceInput = string.Empty;
        _sentenceDraftRestoredTurn = null;
        Publish();
    }

    internal void OnQuizPopped()
    {
        if (!IsQuizOpen || _nativePopPending)
            return;
        CancelAutoAdvance();
        _nativePopPending = true;
        if (!_busy)
            HandlePoppedQuiz();
        else
            CancelQuizAudioPlayback();
    }

    private void HandlePoppedQuiz()
    {
        if (_handlingNativePop || !_nativePopPending)
            return;
        _handlingNativePop = true;
        Run(async () =>
        {
            try
            {
                await BackFromQuizAsync();
            }
            finally
            {
                _handlingNativePop = false;
                _nativePopPending = false;
            }
        });
    }

    internal void BackFromQuiz()
    {
        CancelAutoAdvance();
        if (QueueQuizAudioNavigation(QuizAudioNavigation.Back))
            return;
        Run(BackFromQuizAsync);
    }

    private void AdvanceQuiz()
    {
        CancelAutoAdvance();
        if (QueueQuizAudioNavigation(QuizAudioNavigation.Next))
            return;
        Run(AdvanceQuizAsync);
    }

    private async Task AdvanceQuizAsync()
    {
        if (_quiz is null || !IsQuizOpen)
            return;
        _quizState = await _quiz.NextAsync(_lifetime.Token);
        _answer = string.Empty;
        _quizInputFocused = false;
        _quizCorrectionCompleted = false;
        if (_quizState.Phase == NativePhase.Summary)
            _destination = NativeDestination.QuizSummary;
    }

    private void PlayQuizAudio(Func<CancellationToken, Task<NativeQuizState>> play)
    {
        if (_disposed || _busy || _nativePopPending || !IsQuizOpen || _quiz is null)
            return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _quizAudioCancellation = cancellation;
        Run(async () =>
        {
            try
            {
                _quizState = await play(cancellation.Token);
            }
            catch (OperationCanceledException) when (
                cancellation.IsCancellationRequested || _disposed)
            {
            }
            finally
            {
                if (ReferenceEquals(_quizAudioCancellation, cancellation))
                    _quizAudioCancellation = null;
                cancellation.Dispose();
            }
        });
    }

    private bool QueueQuizAudioNavigation(QuizAudioNavigation navigation)
    {
        if (_nativePopPending)
            return true;
        if (!_busy || _quizAudioCancellation is null)
            return false;
        if (navigation == QuizAudioNavigation.Back
            || _quizNavigationAfterAudio != QuizAudioNavigation.Back)
            _quizNavigationAfterAudio = navigation;
        CancelQuizAudioPlayback();
        return true;
    }

    private void CancelQuizAudioPlayback()
    {
        try
        {
            _quizAudioCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OpenQuizDetails()
    {
        if (_quiz is null || !IsQuizOpen
            || _quizState.Phase is not (NativePhase.Ready or NativePhase.Feedback))
            return;
        _quizState = _quiz.OpenDetails();
        Publish();
    }

    internal SwiftView RenderQuizDetails(SourceUiStyle style) =>
        _quizState.Details is { } details
            ? QuizLearningDetailsView.Page(details, style, Text, DismissQuizDetails)
            : Views.Text(Text("Common_Loading"));

    internal void DismissQuizDetails()
    {
        if (_quiz is not null
            && _quizState.Overlay == NativeQuizOverlay.LearningDetails)
            _quizState = _quiz.DismissOverlay();
        Publish();
    }

    private async Task OpenAssignedQuizAsync(string cardId)
    {
        if (_dashboard is null || _services is null || _settingsOpen)
            throw new InvalidOperationException("The native dashboard is not available.");
        if (_dashboardState.Cards.IsDefaultOrEmpty
            || _dashboardState.Cards.FirstOrDefault(card => card.Id == cardId)
                is not { Activity: PlanActivityType.VocabularyReview })
            return;
        var launch = await _dashboard
            .SelectAssignedQuizAsync(cardId, _lifetime.Token);
        if (launch is null)
        {
            _dashboardState = _dashboardState with {
                Phase = NativePhase.Error, ErrorKey = NativeText.QuizUnavailable };
            return;
        }
        await StartQuizAsync(launch);
    }

    private async Task OpenChooseOwnQuizAsync()
    {
        if (_dashboard is null || _services is null || _settingsOpen
            || _dashboardState.Mode != NativeDashboardMode.ChooseOwn)
            return;
        var selection = await _dashboard.SelectChooseOwnQuizAsync(_lifetime.Token);
        _dashboardState = _dashboard.State;
        if (selection is null)
            return;
        var launch = await _services.Provider
            .GetRequiredService<NativeQuizLaunchService>()
            .CreateChooseOwnAsync(selection.UserId, selection.ResourceIds,
                selection.SkillId, selection.DueOnly,
                selection.FocusVocabularyIds, _lifetime.Token);
        if (launch is null)
        {
            _dashboardState = _dashboardState with {
                ChooseOwn = _dashboardState.ChooseOwn is { } choice
                    ? choice with {
                        ErrorKey = "NativeDashboard_SelectionUnavailable"
                    } : null
            };
            return;
        }
        await StartQuizAsync(launch);
    }

    private async Task StartQuizAsync(NativeQuizLaunch launch)
    {
        if (_services is null)
            return;
        CancelAutoAdvance();
        _feedbackAutoAdvanceCanceled = false;
        _quiz?.Dispose();
        _quiz = _services.Provider.GetRequiredService<NativeQuizPresenter>();
        _quizLaunch = launch;
        _answer = string.Empty;
        _quizInputFocused = false;
        _quizCorrectionCompleted = false;
        _sentenceInput = string.Empty;
        _sentenceDraftRestoredTurn = null;
        _destination = NativeDestination.VocabularyQuiz;
        _quizState = await _quiz.OpenAsync(launch, _lifetime.Token);
        if (_quizState.Phase == NativePhase.Summary)
            _destination = NativeDestination.QuizSummary;
    }

    private async Task ReturnFromQuizAsync()
    {
        if (_quiz is null)
            return;
        CancelAutoAdvance();
        _quizState = await _quiz.DoneAsync(_lifetime.Token);
        if (_quizState.Destination == NativeDestination.TodaysPlan)
            await ReturnToDashboardAsync();
    }

    private async Task BackFromQuizAsync()
    {
        if (_quiz is null)
            return;
        CancelAutoAdvance();
        _logger?.LogInformation(
            "Native iOS quiz Back invoked in {Phase} at turn {Turn}.",
            _quizState.Phase, _quizState.CurrentTurn);
        if (_quizState.CurrentTurn == 0
            && _quizState.Phase is (NativePhase.Empty
                or NativePhase.Error or NativePhase.Blocked)
            && _quizState.Destination != NativeDestination.QuizSummary)
        {
            await ReturnToDashboardAsync();
            return;
        }
        _quizState = await _quiz.BackAsync(_lifetime.Token);
        if (_quizState is {
            Destination: NativeDestination.TodaysPlan,
            Phase: NativePhase.Ready })
            await ReturnToDashboardAsync();
    }

    private async Task RetryQuizAsync()
    {
        if (_quiz is null || _quizState.Phase != NativePhase.Error)
            return;
        if (_quizState is {
            Destination: NativeDestination.QuizSummary,
            ErrorKey: NativeText.SaveFailed })
            await ReturnFromQuizAsync();
        else if (_quizState.Sentence is not null
            && _quizState.ErrorKey == NativeText.SaveFailed)
            _quizState = await _quiz.RetryPendingSentenceAsync(_lifetime.Token);
        else if (_quizState.Prompt is not null
            && _quizState.ErrorKey == NativeText.SaveFailed)
        {
            _quizState = await _quiz.RetryPendingSaveAsync(_lifetime.Token);
            if (_quizState.Phase == NativePhase.Feedback)
                ScheduleAutoAdvance(restarted: true);
        }
        else if (_quizState.CurrentTurn == 0
            && _quizState.ErrorKey == NativeText.ConnectionError
            && _quizLaunch is not null)
            _quizState = await _quiz.OpenAsync(_quizLaunch, _lifetime.Token);
    }

    private async Task ReturnToDashboardAsync()
    {
        CancelAutoAdvance();
        _feedbackAutoAdvanceCanceled = false;
        _autoAdvanceDisplayStartedAt = null;
        _destination = NativeDestination.Dashboard;
        if (_dashboard is not null)
            _dashboardState = _preview || _authenticatedSample
                ? await _dashboard.LoadAsync(_lifetime.Token)
                : await _dashboard.RefreshAsync(_lifetime.Token);
        _quiz?.Dispose();
        _quiz = null;
        _quizLaunch = null;
        _answer = string.Empty;
        _quizInputFocused = false;
        _quizCorrectionCompleted = false;
        _sentenceInput = string.Empty;
        _sentenceDraftRestoredTurn = null;
    }

    private void ScheduleAutoAdvance(bool restarted = false)
    {
        CancelAutoAdvance();
        _feedbackAutoAdvanceCanceled = false;
        _autoAdvanceDisplayStartedAt =
            restarted ? DateTimeOffset.UtcNow : null;
        if (_quiz is not { } quiz || _quizState.Phase != NativePhase.Feedback
            || _quizState.RequiresCorrection)
            return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _autoAdvanceCancellation = cancellation;
        _ = RunAutoAdvanceAsync(quiz, cancellation);
    }

    private async Task RunAutoAdvanceAsync(
        NativeQuizPresenter quiz, CancellationTokenSource cancellation)
    {
        try
        {
            var expectedTurnId = _quizState.TurnId;
            var state = await quiz.AutoAdvanceAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested
                || !ReferenceEquals(_quiz, quiz))
                return;
            if (expectedTurnId is not null && state is {
                    Phase: NativePhase.Feedback, Overlay: not NativeQuizOverlay.None }
                && state.TurnId == expectedTurnId)
            {
                var current = quiz.State;
                if (current.Phase == NativePhase.Feedback
                    && current.TurnId == expectedTurnId
                    && current.Overlay != NativeQuizOverlay.None)
                    quiz.DismissOverlay();
                current = quiz.State;
                if (cancellation.IsCancellationRequested
                    || current.TurnId != expectedTurnId)
                    return;
                state = current is {
                    Phase: NativePhase.Feedback, Overlay: NativeQuizOverlay.None }
                    ? await quiz.NextAsync(cancellation.Token) : current;
            }
            _quizState = state;
            if (state.Phase == NativePhase.Summary)
                _destination = NativeDestination.QuizSummary;
            Publish();
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested || _disposed)
        {
        }
        catch (Exception ex)
        {
            if (cancellation.IsCancellationRequested || _disposed)
                return;
            _logger?.LogError(ex, "Native iOS quiz auto-advance failed.");
            _quizState = _quizState with {
                Phase = NativePhase.Error, ErrorKey = NativeText.SaveFailed };
            Publish();
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _autoAdvanceCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void CancelAutoAdvance()
    {
        _feedbackAutoAdvanceCanceled = true;
        _autoAdvanceDisplayStartedAt = null;
        var current = Interlocked.Exchange(
            ref _autoAdvanceCancellation, null);
        try
        {
            current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

#if DEBUG
    private async Task RunPreviewJourneyAsync(
        NativePreviewFixture fixture, bool authenticated = false)
    {
        var provider = _services!.Provider;
        if (fixture.Surface is "signin-loading" or "signin-error")
        {
            _preview = true;
            _destination = NativeDestination.SignIn;
            _auth = provider.GetRequiredService<NativeAuthPresenter>();
            _authState = fixture.Surface == "signin-loading"
                ? new(NativePhase.Loading, NativeDestination.SignIn)
                : new(NativePhase.Error, NativeDestination.SignIn,
                    ErrorKey: NativeText.InvalidCredentials);
            return;
        }

        await fixture.SeedAsync(provider);
        _preview = !authenticated;
        _authenticatedSample = authenticated;
        if (fixture.Surface is "onboarding" or "onboarding-preferences")
        {
            _authState = new(NativePhase.Ready, NativeDestination.Onboarding,
                fixture.Owner);
            _onboardingOwner = fixture.Owner;
            _onboardingDraft = new NativeOnboardingDraft(
                "", "English", "Korean");
            _onboardingStep = fixture.Surface == "onboarding-preferences" ? 4 : 0;
            _destination = NativeDestination.Onboarding;
            return;
        }

        _destination = NativeDestination.Dashboard;
        _dashboard = provider.GetRequiredService<NativeDashboardPresenter>();
        EnsureResources();
        _dashboardState = await _dashboard.LoadAsync(_lifetime.Token);
        if (_dashboardState.Phase != NativePhase.Ready)
            throw new InvalidOperationException(
                "The preview plan did not load through the shared presenter.");
        if (fixture.Surface == "starter-loading")
        {
            await _resources!.BeginControlledStarterPreviewAsync(_lifetime.Token);
            _selectedTab = 1;
            _ = ReleaseControlledStarterPreviewAsync();
            return;
        }
        if (fixture.Surface == "dashboard")
            return;

        var card = fixture.SelectAssignedQuizCard(_dashboardState);
        if (card is null)
            return;
        await OpenAssignedQuizAsync(card.Id);
        if (_quiz is null)
            throw new InvalidOperationException(
                "The preview quiz launch context is invalid.");
        if (_quizState.Phase == NativePhase.ResumeChoice)
        {
            if (fixture.Surface is "resume" or "mc-resume")
                return;
            _quizState = await _quiz.ResumeAsync(_lifetime.Token);
        }
        else if (fixture.Surface is "resume" or "mc-resume")
            throw new InvalidOperationException(
                "A preview resume choice needs a saved quiz session.");

        if (fixture.ShouldShowRestoredSummary(_quizState))
        {
            _destination = NativeDestination.QuizSummary;
            return;
        }
        if (fixture.Surface is "back" or "mc-back")
        {
            _quizState = await _quiz.BackAsync(_lifetime.Token);
            if (_quizState.Destination != NativeDestination.TodaysPlan)
                throw new InvalidOperationException(
                    "The preview back action did not preserve the plan.");
            await ReturnToDashboardAsync();
            return;
        }
        if (fixture.Surface == "mc")
        {
            if (_quizState is not {
                Phase: NativePhase.Ready, Mode: "MultipleChoice" })
                throw new InvalidOperationException(
                    "The preview MC question did not use shared quiz choices.");
            return;
        }
        if (fixture.Surface == "mc-feedback")
        {
            if (_quizState is not {
                Phase: NativePhase.Feedback, Mode: "MultipleChoice",
                RequiresCorrection: false })
                throw new InvalidOperationException(
                    "The preview MC Next needs existing non-correction feedback.");
            return;
        }
        if (fixture.Surface is "mc-style-feedback-correct"
            or "mc-style-feedback-wrong")
        {
            if (_quizState is { Phase: NativePhase.Ready, Mode: "MultipleChoice" })
                await AnswerPreviewQuestionAsync(provider,
                    incorrect: fixture.Surface == "mc-style-feedback-wrong");
            if (_quizState is not { Phase: NativePhase.Feedback, Mode: "MultipleChoice" })
                throw new InvalidOperationException(
                    "The real multiple-choice answer did not retain feedback.");
            return;
        }
        if (fixture.Surface is "question" or "quiz")
        {
            if (_quizState.Phase is not
                (NativePhase.Ready or NativePhase.Feedback))
                throw new InvalidOperationException(
                    "The preview question could not be restored.");
            return;
        }
        if (fixture.Surface == "feedback"
            && _quizState.Phase == NativePhase.Ready)
            await AnswerPreviewQuestionAsync(provider);
        if (fixture.Surface == "feedback")
        {
            if (_quizState.Phase != NativePhase.Feedback)
                throw new InvalidOperationException(
                    "The real quiz did not persist feedback.");
            return;
        }

        for (var step = 0;
             step < 20 && _quizState.Phase != NativePhase.Summary; step++)
        {
            if (_quizState.Phase == NativePhase.Ready)
                await AnswerPreviewQuestionAsync(provider);
            if (_quizState.Phase != NativePhase.Feedback
                || _quizState.RequiresCorrection)
                throw new InvalidOperationException(
                    "The real quiz could not grade a preview turn.");
            _quizState = await _quiz.NextAsync(_lifetime.Token);
        }
        if (_quizState.Phase != NativePhase.Summary)
            throw new InvalidOperationException(
                "The real quiz did not reach its persisted round summary.");
        _destination = NativeDestination.QuizSummary;
        if (fixture.Surface != "complete")
            return;
        _quizState = await _quiz.DoneAsync(_lifetime.Token);
        if (_quizState.Destination != NativeDestination.TodaysPlan)
            throw new InvalidOperationException(
                "The real quiz did not complete its assigned plan item.");
        await ReturnToDashboardAsync();
        if (!_dashboardState.Cards.Any(item =>
                item.Id == card.Id && item.IsCompleted))
            throw new InvalidOperationException(
                "The completed quiz card was not persisted.");
    }

    private async Task ReleaseControlledStarterPreviewAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _lifetime.Token);
            if (!_disposed)
                _resources?.EndControlledStarterPreview();
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
    }

    private async Task AnswerPreviewQuestionAsync(
        IServiceProvider provider, bool incorrect = false)
    {
        var prompt = _quizState.Prompt
            ?? throw new InvalidOperationException(
                "A visible quiz question is required.");
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = await db.VocabularyWords.AsNoTracking()
            .SingleAsync(item => item.Id == prompt.WordId, _lifetime.Token);
        var answer = prompt.IsTargetLanguagePrompt
            ? word.NativeLanguageTerm
            : word.TargetLanguageTerm;
        if (incorrect)
            answer = _quizState.Choices.Select(choice => choice.Text)
                .FirstOrDefault(choice =>
                    !string.Equals(choice, answer, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException(
                "A preview answer needs a stored vocabulary term.");
        _quizState = await _quiz!.AnswerAsync(answer, _lifetime.Token);
    }
#endif
}
