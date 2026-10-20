using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeQuizSourceLayoutContractTests
{
    [Fact]
    public void PromptIconRow_UsesOneFortyFourPointCircularButtonForEachSourceControl()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.UI/Pages/VocabQuiz.razor")));
        var promptStart = source.IndexOf(
            "<div class=\"d-flex justify-content-center gap-3 mb-2\">",
            StringComparison.Ordinal);
        var promptEnd = source.IndexOf("</div>", promptStart, StringComparison.Ordinal);
        Assert.True(promptStart >= 0 && promptEnd > promptStart);
        var sourceRow = source[promptStart..promptEnd];
        Assert.Equal(4, Regex.Matches(sourceRow,
            "style=\"width: 44px; height: 44px;\"").Count);
        Assert.Equal(44, SourceUiLayout.QuizPromptButtonDiameter);
        Assert.Equal(14, SourceUiLayout.QuizPromptSymbolBaseSize);
        Assert.Equal(21, SourceUiLayout.QuizPromptSymbolBaseSize
            * SentenceStudio.Contracts.Theme.AppearanceSelection.MaxFontScale);

        var feature = Read("Features/Quiz/QuizQuestionView.cs");
        var rowStart = feature.IndexOf("var actions = new List<SwiftView>();",
            StringComparison.Ordinal);
        var rowEnd = feature.IndexOf("if (actions.Count > 0)", rowStart,
            StringComparison.Ordinal);
        Assert.True(rowStart >= 0 && rowEnd > rowStart);
        var row = feature[rowStart..rowEnd];
        Assert.Equal(4, Regex.Matches(row, "style.QuizPromptIconButton\\(").Count);
        Assert.Contains("\"speaker.wave.2\", playPrompt", row);
        Assert.Contains("\"info.circle\", openDetails", row);
        Assert.Contains("togglePhotoVisible,", row);
        Assert.Contains("toggleSentenceHints,", row);
        Assert.Contains("photoButton.IsEnabled = !busy;", row);
        Assert.Contains("\"native.quiz.play-prompt\"", row);
        Assert.Contains("NativeAccess.QuizDetails", row);
        Assert.Contains("\"native.quiz.photo.toggle\"", row);
        Assert.Contains("\"native.quiz.hints.toggle\"", row);
        Assert.DoesNotContain("WithMinimumHeight", row);
        Assert.DoesNotContain("SwiftButtonStyle.Bordered", row);

        var style = Read("Styling/SourceUiStyle.cs");
        var helper = style[style.IndexOf("internal SwiftView QuizPromptIconButton(",
                StringComparison.Ordinal)
            ..style.IndexOf("internal SwiftView TextLinkButton(",
                StringComparison.Ordinal)];
        Assert.Contains("appearance.BootstrapOutlineSecondaryFill", helper);
        Assert.Contains("appearance.BootstrapButtonBorder", helper);
        Assert.Contains("appearance.SecondaryText", helper);
        Assert.Contains("WithButtonAppearance(", helper);
        Assert.Contains(".WithButtonCircle(SourceUiLayout.QuizPromptButtonDiameter)", helper);
        Assert.Contains(".WithFixedSymbolSize(", helper);
        Assert.Contains("SourceUiLayout.QuizPromptSymbolBaseSize * appearance.Selection.FontScale",
            helper);
        Assert.Contains(".WithAccessibilityLabel(name)", helper);
        Assert.Contains(".WithAccessibilityIdentifier(accessibilityIdentifier)", helper);
        Assert.DoesNotContain("WithTypography(", helper);
        Assert.DoesNotContain("WithSketchyBootstrapCorners", helper);
        Assert.Contains("() => playChoiceAudio(optionIndex)", feature);
        Assert.Contains(".WithButtonStyle(SwiftButtonStyle.Bordered)", feature);
    }

    [Fact]
    public void AnswerTermAndResultAlert_PrecedeMcChoicesAndSharedTextField()
    {
        var feature = Read("Features/Quiz/QuizQuestionView.cs");
        var render = feature[feature.IndexOf("internal static void Add(",
            StringComparison.Ordinal)..];

        var term = render.IndexOf("native.quiz.correct-answer",
            StringComparison.Ordinal);
        var result = render.IndexOf("NativeAccess.QuizFeedback",
            StringComparison.Ordinal);
        var choices = render.IndexOf("if (state.Mode == \"MultipleChoice\")",
            StringComparison.Ordinal);
        var input = render.IndexOf(".WithAccessibilityIdentifier(NativeAccess.QuizAnswer)",
            StringComparison.Ordinal);
        Assert.True(term >= 0 && result > term && choices > result
            && input > choices);
        Assert.DoesNotContain("QuizAnswer}.correction", render);
        Assert.Contains("correcting ? submitCorrection : submitText", render);
        Assert.Contains("field.FocusRequested = true;", render);
    }

    [Fact]
    public void SentenceEditor_UsesSourceInputSurfaceAndUpdatesGradeWithoutLosingFocus()
    {
        var sentence = Read("Features/Quiz/QuizSentenceView.cs");

        Assert.Contains("style.Input(Views.TextEditor(input, value =>",
            sentence);
        Assert.Contains("changed(value);", sentence);
        Assert.Contains("grade.IsEnabled = !busy && !state.IsSentenceSubmitting",
            sentence);
        Assert.Contains("text(\"VocabQuiz_SentencePlaceholder\")", sentence);
        Assert.Contains(".WithTextEditorPlaceholder(placeholder)", sentence);
        Assert.Contains("style.Appearance.TertiaryText.Light", sentence);
        Assert.Contains("style.Appearance.TertiaryText.Dark", sentence);
        Assert.Contains("&& !string.IsNullOrWhiteSpace(value);", sentence);
        Assert.Contains(".WithMinimumHeight(140)", sentence);
    }

    [Fact]
    public void FeedbackDurationProgress_UsesRealTurnTimestampAndStopsOnBusyOrExit()
    {
        var feature = Read("Features/Quiz/QuizView.cs");
        var scene = Read("App/ApplicationScene.cs");
        var app = Read("App/ApplicationController.Quiz.cs");

        Assert.Contains("state.AutoAdvanceDurationMs, 3", feature);
        Assert.Contains("\"native.quiz.auto-advance.progress\"", feature);
        Assert.Contains("Views.Divider().WithAccessibilityIdentifier(", feature);
        Assert.Contains("\"native.quiz.footer.divider\"", feature);
        Assert.Contains("Phase: NativePhase.Feedback, RequiresCorrection: false",
            feature);
        Assert.Contains("DateTimeOffset.UtcNow - state.FeedbackStartedAt.Value",
            scene);
        Assert.Contains("new PeriodicTimer(TimeSpan.FromMilliseconds(33))", scene);
        Assert.Contains("startOffset + clock.Elapsed.TotalMilliseconds", scene);
        Assert.Contains("if (_app.IsBusy)", scene);
        Assert.Contains("_quizProgressCancellation?.Cancel();", scene);
        Assert.Contains("ReferenceEquals(_quizProgress, progress)", scene);
        Assert.Contains("StopQuizProgress();\n            _dashboardStack.Pop();", scene);
        Assert.Contains("quiz.AutoAdvanceAsync(cancellation.Token)", app);
        Assert.Contains("current?.Cancel();", app);
        Assert.Contains("CancelAutoAdvance();", app);
        Assert.Contains("_feedbackAutoAdvanceCanceled = true;", app);
        Assert.Contains("_quizState with { FeedbackStartedAt = null }", app);
        Assert.DoesNotContain("async void ScheduleAutoAdvance", app);
    }

    [Fact]
    public void InfoDuringFeedback_AutoAdvanceClosesOverlayAtDeadlineRatherThanCancelingIt()
    {
        var app = Read("App/ApplicationController.Quiz.cs");
        var open = app[app.IndexOf("private void OpenQuizDetails()", StringComparison.Ordinal)
            ..app.IndexOf("internal SwiftView RenderQuizDetails(",
                StringComparison.Ordinal)];
        var auto = app[app.IndexOf("private async Task RunAutoAdvanceAsync(",
                StringComparison.Ordinal)..app.IndexOf("private void CancelAutoAdvance()",
                StringComparison.Ordinal)];

        Assert.DoesNotContain("CancelAutoAdvance();", open);
        Assert.Contains("_quiz.OpenDetails()", open);
        var due = auto.IndexOf("await quiz.AutoAdvanceAsync(cancellation.Token)",
            StringComparison.Ordinal);
        var close = auto.IndexOf("quiz.DismissOverlay()", StringComparison.Ordinal);
        var next = auto.IndexOf("await quiz.NextAsync(cancellation.Token)",
            StringComparison.Ordinal);
        Assert.True(due >= 0 && close > due && next > close);
        Assert.Contains("expectedTurnId", auto);
        Assert.Contains("cancellation.IsCancellationRequested", auto);
    }

    [Fact]
    public void NativeAudioChoices_KeepL2EngagementByRequiringPlayedTargetOption()
    {
        var feature = Read("Features/Quiz/QuizQuestionView.cs");
        var app = Read("App/ApplicationController.Quiz.cs");

        Assert.Contains("state.CanPlayAnswerAudio", feature);
        Assert.Contains("NativeVocabQuiz_ListenToChoose", feature);
        Assert.Contains("canSelectAudioChoice(index)", feature);
        Assert.Contains("_quizState.HeardAnswerChoiceIndices.Contains(index)", app);
        Assert.Contains("_quiz.PlayAnswerChoiceAsync(", app);
        Assert.DoesNotContain("_heardAudioChoices", app);
    }

    [Fact]
    public void QuizAudioNavigation_CancelsPlaybackWithoutUnblockingOtherActions()
    {
        var app = Read("App/ApplicationController.Quiz.cs");
        var controller = Read("App/ApplicationController.cs");
        var scene = Read("App/ApplicationScene.cs");
        var question = Read("Features/Quiz/QuizQuestionView.cs");
        var sentence = Read("Features/Quiz/QuizSentenceView.cs");
        var feedback = Read("Features/Quiz/QuizFeedbackView.cs");
        var queue = app[app.IndexOf("private bool QueueQuizAudioNavigation(",
                StringComparison.Ordinal)
            ..app.IndexOf("private void CancelQuizAudioPlayback()",
                StringComparison.Ordinal)];
        var runner = controller[controller.IndexOf(
                "private async Task RunSafelyAsync(", StringComparison.Ordinal)
            ..controller.IndexOf("private static string Text(",
                StringComparison.Ordinal)];

        Assert.Contains("PlayPrompt: () => PlayQuizAudio(", app);
        Assert.Contains("PlayQuizAudio(token => _quiz.PlayAnswerChoiceAsync(", app);
        Assert.Contains("cancellation.IsCancellationRequested || _disposed", app);
        Assert.Contains("if (QueueQuizAudioNavigation(QuizAudioNavigation.Next))", app);
        Assert.Contains("if (QueueQuizAudioNavigation(QuizAudioNavigation.Back))", app);
        Assert.Contains("_quizState = await _quiz.NextAsync(_lifetime.Token);", app);
        Assert.Contains("_quizState = await _quiz.BackAsync(_lifetime.Token);", app);
        Assert.Contains("_quizAudioCancellation?.Cancel();", app);
        Assert.Contains("if (!_busy || _quizAudioCancellation is null)\n"
            + "            return false;", queue);
        var queued = queue.IndexOf("_quizNavigationAfterAudio = navigation;",
            StringComparison.Ordinal);
        var canceled = queue.IndexOf("CancelQuizAudioPlayback();",
            StringComparison.Ordinal);
        Assert.True(queued >= 0 && canceled > queued);
        Assert.Contains("var queuedNavigation = _quizNavigationAfterAudio;", controller);
        Assert.Contains("if (_disposed || _busy)\n            return;", controller);
        Assert.Contains("if (_busy || _quizState.Phase != NativePhase.Ready", app);
        Assert.Contains("button.IsEnabled = !answered && !busy;", question);
        Assert.Contains("grade.IsEnabled = !busy && !state.IsSentenceSubmitting",
            sentence);
        Assert.Contains("text(NativeText.QuizNext), \"chevron.right\", next",
            feedback);
        var busyCleared = runner.IndexOf("_busy = false;", StringComparison.Ordinal);
        var nativePop = runner.IndexOf(
            "if (_nativePopPending && !_handlingNativePop && IsQuizOpen)",
            StringComparison.Ordinal);
        var deferred = runner.IndexOf(
            "else if (queuedNavigation != QuizAudioNavigation.None",
            StringComparison.Ordinal);
        Assert.True(busyCleared >= 0 && nativePop > busyCleared
            && deferred > nativePop);
        Assert.Contains("_tabs.IsEnabled = !_app.NavigationBlocked;", scene);
        Assert.Contains("!_app.NavigationBlocked || _app.CanNavigateBackDuringQuizAudio",
            scene);
        Assert.DoesNotContain("PlayPrompt: () => Run(", app);
    }

    [Fact]
    public void RealPhotoBytes_AcknowledgeOnlyAfterActualSwiftUIRendering()
    {
        var feature = Read("Features/Quiz/QuizQuestionView.cs");
        var quiz = Read("Features/Quiz/QuizView.cs");
        var app = Read("App/ApplicationController.Quiz.cs");
        var scene = Read("App/ApplicationScene.cs");

        Assert.Contains("prompt.PhotoRequested && prompt.Photo is { } photo",
            feature);
        Assert.Contains("Views.ImageFromBytes(photo.Bytes,", feature);
        Assert.Contains("onRendered: () => photoRendered(turnId)", feature);
        Assert.Contains("onFailed: code => photoFailed(turnId, code)", feature);
        Assert.Contains("_quiz.PhotoRenderedAsync(", app);
        Assert.Contains("_quiz.PhotoFailedAsync(", app);
        Assert.Contains("PhotoRequested: true, ShowPhoto: false", app);
        Assert.Contains("Views.ZoomablePhoto(photo.Bytes,", quiz);
        Assert.Contains("Views.FullScreenPhotoCover(", quiz);
        Assert.Contains("_quizPhotoCover.IsPresented = _app.IsQuizPhotoPresented;",
            scene);
        Assert.Contains(".WithSheetDetentFraction(0.7)", scene);
        Assert.DoesNotContain("UIKit", feature);
        Assert.DoesNotContain("UIKit", app);
        Assert.DoesNotContain("UIKit", scene);
    }

    [Fact]
    public void SourceSecondaryMenu_OpensRealSentenceProductionAndSafePhotoTextToggle()
    {
        var scene = Read("App/ApplicationScene.cs");
        var app = Read("App/ApplicationController.Quiz.cs");
        var feature = Read("Features/Quiz/QuizView.cs");
        var sentence = Read("Features/Quiz/QuizSentenceView.cs");
        var question = Read("Features/Quiz/QuizQuestionView.cs");

        Assert.Contains("NativeVocabQuiz_UseInSentence", feature);
        Assert.Contains("OpenQuizSentence, ToggleQuizPhotoText, DismissQuizPhoto,",
            app);
        Assert.Contains("Views.Menu(text(\"More\"), actions.ToArray())", feature);
        Assert.Contains("_quiz.OpenSentenceShortcut()", app);
        Assert.Contains("_quiz.SubmitSentenceAsync(", app);
        Assert.Contains("_quiz.ContinueSentenceShortcutAsync(", app);
        Assert.Contains("_quiz.ToggleSentenceHints()", app);
        Assert.Contains("native.quiz.sentence.input", sentence);
        Assert.Contains("native.quiz.sentence.result", sentence);
        Assert.Contains("native.quiz.hints.panel", question);
        Assert.Contains(".OnSubmit(correcting ? submitCorrection : submitText)",
            question);
        Assert.DoesNotContain("native.quiz.details.unavailable", question);
    }

    [Fact]
    public void AllGradeUnavailableSentenceResults_OfferRetryWithoutProductiveContinue()
    {
        var sentence = Read("Features/Quiz/QuizSentenceView.cs");
        var app = Read("App/ApplicationController.Quiz.cs");

        Assert.Contains("var graded = !results.IsEmpty;", sentence);
        Assert.Contains("var allGradingUnavailable = graded", sentence);
        Assert.Contains("results.All(result => result.GradeUnavailable)", sentence);
        Assert.Contains("var headerAction = allGradingUnavailable ? null", sentence);
        Assert.Contains("text(\"VocabQuiz_ContinueQuiz\"), next", sentence);
        Assert.Contains("? NativeText.Retry : \"VocabQuiz_GradeSentences\"", sentence);
        Assert.Contains("text(result.GradeUnavailable", sentence);
        Assert.Contains("if (!result.GradeUnavailable)", sentence);
        Assert.Contains("_quizState.SentenceDraft", app);
        Assert.Contains("results.All(result => result.GradeUnavailable)", app);
        Assert.Contains("string.Join(\"\\n\", results.Select(result => result.Sentence))",
            app);
        Assert.Contains("_quiz.SubmitSentenceAsync(", app);
        Assert.Contains("|| !_quizState.SentenceResults.IsDefaultOrEmpty)",
            app);
    }

    private static string Read(string file) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS", file)));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
