using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Quiz;

internal static class QuizQuestionView
{
    internal static void Add(
        List<SwiftView> items,
        NativeQuizState state,
        string answer,
        bool correctionCompleted,
        bool busy,
        SourceUiStyle style,
        Func<string, string> text,
        Action<string> answerChanged,
        Action<bool> answerFocusChanged,
        bool answerShouldFocus,
        Action<string> selectChoice,
        Action playPrompt,
        Action<int> playChoiceAudio,
        Action submitText,
        Action submitCorrection,
        Action openDetails,
        Action toggleSentenceHints,
        Action<string> photoRendered,
        Action<string, string> photoFailed,
        Action openPhoto,
        Action togglePhotoVisible,
        Action<string>? promptAppeared = null,
        SwiftView? besideSubmit = null)
    {
        if (state.Prompt is { } prompt)
        {
            var question = prompt.ShowWrittenPrompt && !string.IsNullOrWhiteSpace(prompt.PromptText)
                ? style.Text(Views.Text(prompt.PromptText),
                        SourceTextRole.Display, SourceTextTone.Primary,
                        sourceHeading: true)
                    .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(NativeAccess.QuizQuestion)
                : style.Text(Views.Text(text(prompt.ShowPhoto
                            ? prompt.CanPlayPromptAudio
                                ? "VocabQuiz_ListenLookAnswer"
                                : "VocabQuiz_LookAndAnswer"
                            : "VocabQuiz_ListenAndAnswer")),
                        SourceTextRole.Display, SourceTextTone.Primary,
                        sourceHeading: true)
                    .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(NativeAccess.QuizQuestion);
            // The question reports when it is on screen; the turn's autoplay waits for it.
            if (promptAppeared is not null && state.TurnId is { } appearedTurn)
                question.OnAppear(() => promptAppeared(appearedTurn));
            items.Add(question);
            var actions = new List<SwiftView>();
            if (prompt.CanPlayPromptAudio)
                actions.Add(style.QuizPromptIconButton(
                    "speaker.wave.2", playPrompt, text("VocabWordEdit_PlayAudio"),
                    "native.quiz.play-prompt"));
            if (state.Phase is NativePhase.Ready or NativePhase.Feedback)
                actions.Add(style.QuizPromptIconButton(
                    "info.circle", openDetails, text("VocabQuiz_LearningDetails"),
                    NativeAccess.QuizDetails));
            if (prompt.Photo is not null &&
                (prompt.PhotoRequested || prompt.ShowPhoto || prompt.CanTogglePhoto))
            {
                var photoButton = style.QuizPromptIconButton(
                    prompt.ShowPhoto ? "eye.slash" : "photo", togglePhotoVisible,
                    text(prompt.ShowPhoto
                        ? "NativeVocabQuiz_HideImage"
                        : "NativeVocabQuiz_ShowImage"),
                    "native.quiz.photo.toggle");
                photoButton.IsEnabled = !busy;
                actions.Add(photoButton);
            }
            if (prompt.IsTargetLanguagePrompt
                && state.CanShowSentenceHints
                && state.Phase is (NativePhase.Ready or NativePhase.Feedback))
                actions.Add(style.QuizPromptIconButton(
                    state.SentenceHintsExpanded
                        ? "text.quote" : "quote.bubble", toggleSentenceHints,
                    text(state.SentenceHintsExpanded
                        ? "VocabQuiz_HideSampleSentences"
                        : "VocabQuiz_ShowSampleSentences"),
                    "native.quiz.hints.toggle"));
            if (actions.Count > 0)
            {
                var controls = Views.HStack(actions.ToArray())
                    .WithFlexibleWidth(
                        alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.quiz.prompt.actions");
                controls.Spacing = SourceUiLayout.PageGap;
                items.Add(controls);
            }
            if (state.Phase is (NativePhase.Ready or NativePhase.Feedback)
                && prompt.PhotoRequested && prompt.Photo is { } photo
                && state.TurnId is { } turnId)
            {
                var thumbnail = Views.Button(
                        Views.ImageFromBytes(photo.Bytes,
                                text(prompt.PhotoAccessibilityKey), 160,
                                onRendered: () => photoRendered(turnId),
                                onFailed: code => photoFailed(turnId, code))
                            .WithAccessibilityIdentifier(
                                "native.quiz.photo.image"),
                        openPhoto)
                    .WithButtonStyle(SwiftButtonStyle.Plain)
                    .WithAccessibilityLabel(text("VocabQuiz_EnlargePhoto"))
                    .WithAccessibilityIdentifier("native.quiz.photo.thumbnail");
                thumbnail.IsEnabled = !busy && prompt.ShowPhoto;
                items.Add(Views.HStack(thumbnail)
                    .WithFlexibleWidth(
                        alignment: SwiftHorizontalAlignment.Center));
            }
            if (prompt.IsTargetLanguagePrompt && state.SentenceHintsExpanded
                && !state.SentenceHints.IsDefaultOrEmpty)
            {
                var hints = new List<SwiftView>
                {
                    style.Text(Views.Text(text("VocabQuiz_SampleSentencesLabel")),
                            SourceTextRole.Body2Strong)
                        .WithForegroundColor(
                            style.Appearance.InfoAlertText.Light,
                            style.Appearance.InfoAlertText.Dark)
                };
                foreach (var hint in state.SentenceHints)
                    hints.Add(style.Text(Views.Text(hint.TargetSentence),
                            SourceTextRole.Body2)
                        .WithForegroundColor(
                            style.Appearance.InfoAlertText.Light,
                            style.Appearance.InfoAlertText.Dark)
                        .WithAccessibilityIdentifier(
                            $"native.quiz.hints.sentence.{hint.ExampleSentenceId}"));
                var panel = Views.VStack(hints.ToArray());
                panel.Spacing = SourceUiLayout.ItemGap;
                items.Add(style.InfoAlert(panel, compact: true)
                    .WithFlexibleWidth(560, SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.quiz.hints.panel"));
            }
        }
        if (state.Phase == NativePhase.Feedback)
        {
            if (state.AnswerWasCorrect == true
                && state.FeedbackMnemonicText is { Length: > 0 } mnemonic)
            {
                var message = Views.HStack(
                    style.Text(Views.Text($"{text("NativeVocabQuiz_Mnemonic")}:"),
                            SourceTextRole.Body2Strong)
                        .WithForegroundColor(style.Appearance.InfoAlertText.Light,
                            style.Appearance.InfoAlertText.Dark),
                    style.Text(Views.Text(mnemonic), SourceTextRole.Body2)
                        .WithForegroundColor(style.Appearance.InfoAlertText.Light,
                            style.Appearance.InfoAlertText.Dark));
                message.Spacing = 4;
                items.Add(style.InfoAlert(message, compact: true)
                    .WithFlexibleWidth(560, SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.quiz.feedback.mnemonic"));
            }
        }
        // Issue 225 (Captain, 2026-11-15): a multiple-choice result shows only on the
        // options, so the graded question still fits a landscape screen. Audio choices show
        // letters, so they keep the written answer; text answers keep the whole result, and
        // so does a turn whose answer matches none of the options.
        var audioChoices = state.Mode == "MultipleChoice"
            && state.Prompt is { IsTargetLanguagePrompt: false }
            && state.CanPlayAnswerAudio;
        var choicesShowResult = state.Mode == "MultipleChoice"
            && !state.Choices.IsDefault && state.Choices.Any(choice => IsCorrectChoice(state, choice.Text));
        // Issue 254 (Captain, 2026-11-20): a wrong typed answer's banner already gives the
        // answer, so the line is not repeated above it and the field stays above the keyboard.
        var bannerGivesAnswer = state.Mode == "Text" && state.AnswerWasCorrect != true;
        var showAnswerLine = (!choicesShowResult || audioChoices) && !bannerGivesAnswer;
        if (state.Phase == NativePhase.Feedback && showAnswerLine)
        {
            // Source: the answer sits in the centered prompt block (text-center).
            if (state.CorrectAnswerAfterGrading is { Length: > 0 } correct)
                items.Add(style.Text(Views.Text(correct), SourceTextRole.Title2)
                    .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.quiz.correct-answer"));
        }
        if (state.Phase == NativePhase.Feedback && !choicesShowResult)
            items.Add(style.Alert(style.Text(Views.Text(FeedbackStatus(state, text)),
                        SourceTextRole.DefaultBody),
                    success: state.AnswerWasCorrect == true, compact: true)
                .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier(NativeAccess.QuizFeedback));
        if (state.Mode == "MultipleChoice")
        {
            for (var index = 0; index < state.Choices.Length; index++)
            {
                var optionIndex = index;
                var choice = state.Choices[index];
                var id = $"{NativeAccess.QuizChoice}.{index}";
                var choiceButton = QuizChoice(style, state, choice.Text,
                        busy || state.Phase == NativePhase.Ready
                            && !state.CanSubmit,
                        () => selectChoice(choice.Text), text,
                        audioChoices ? ((char)('A' + index)).ToString() : null)
                    .WithAccessibilityIdentifier(id);
                // Without the banner, VoiceOver hears the result as the turn is graded.
                if (index == 0)
                    choiceButton.WithAccessibilityAnnouncement(
                        state.Phase == NativePhase.Feedback && choicesShowResult
                            ? FeedbackStatus(state, text) : string.Empty);
                if (audioChoices)
                {
                    var letter = ((char)('A' + index)).ToString();
                    // Source: a letter can be chosen at once; its play button is optional.
                    choiceButton.WithAccessibilityLabel(
                        string.Format(CultureInfo.CurrentCulture,
                            text("NativeVocabQuiz_ChooseChoice"), letter)
                        + ChoiceResultCaption(state, choice.Text, text));
                    var play = Views.Button(
                            Views.Image("speaker.wave.2"),
                            () => playChoiceAudio(optionIndex))
                        .WithButtonStyle(SwiftButtonStyle.Bordered)
                        .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                        .WithAccessibilityLabel(string.Format(
                            CultureInfo.CurrentCulture,
                            text("NativeVocabQuiz_PlayChoice"), letter))
                        .WithAccessibilityIdentifier(
                            $"native.quiz.choice.audio.{index}");
                    play.IsEnabled = !busy
                        && state.Phase is (NativePhase.Ready or NativePhase.Feedback);
                    var option = Views.HStack(play, choiceButton)
                        .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center);
                    option.Spacing = SourceUiLayout.ItemGap;
                    items.Add(option);
                }
                else
                    items.Add(choiceButton);
            }
        }
        else if (state.Mode == "Text")
        {
            var correcting = state.RequiresCorrection;
            var displayedAnswer = state.Phase == NativePhase.Feedback
                && !correcting
                ? correctionCompleted ? answer : state.SelectedAnswer ?? answer
                : answer;
            SwiftView? submit = null;
            if (state.Phase == NativePhase.Ready || correcting)
            {
                submit = style.QuizPrimaryButton(
                        text("VocabQuiz_SubmitAnswer"), "checkmark",
                        correcting ? submitCorrection : submitText, leadingSymbol: true)
                    .WithAccessibilityIdentifier(correcting
                        ? "native.quiz.correction" : "native.quiz.submit");
                SourceUiStyle.SetCustomButtonAvailability(submit, style.Appearance,
                    SourceButtonRole.Primary,
                    !busy && (correcting || state.CanSubmit)
                        && !string.IsNullOrWhiteSpace(answer));
            }
            var field = style.TextInput(Views.TextField(
                    text(correcting
                        ? NativeText.CorrectionRequired : NativeText.AnswerRequired),
                    displayedAnswer, value =>
                    {
                        answerChanged(value);
                        if (submit is not null)
                            SourceUiStyle.SetCustomButtonAvailability(
                                submit, style.Appearance, SourceButtonRole.Primary,
                                !busy && (correcting || state.CanSubmit)
                                    && !string.IsNullOrWhiteSpace(value));
                    }))
                .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier(NativeAccess.QuizAnswer)
                .OnSubmit(correcting ? submitCorrection : submitText)
                .OnFocusChanged(answerFocusChanged);
            field.IsEnabled = !busy && (correcting
                || state.Phase == NativePhase.Ready && state.CanSubmit);
            if (answerShouldFocus && field.IsEnabled)
                field.FocusRequested = true;
            items.Add(field);
            if (submit is not null)
            {
                var submitRow = besideSubmit is null
                    ? Views.HStack(submit) : Views.HStack(submit, besideSubmit);
                submitRow.Spacing = SourceUiLayout.ItemGap;
                items.Add(submitRow
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
            }
            else if (besideSubmit is not null)
                items.Add(besideSubmit);
        }
    }

    private static SwiftView QuizChoice(
        SourceUiStyle style, NativeQuizState state, string answer, bool busy,
        Action select, Func<string, string> text, string? audioLetter = null)
    {
        var answered = state.Phase is NativePhase.Feedback or NativePhase.Error;
        var correct = answered && IsCorrectChoice(state, answer);
        var incorrect = answered && IsWrongPick(state, answer);
        var sourceClass = style.Appearance.Selection.ThemeId is "brite" or "sketchy";
        var neutralDisabled = answered && !correct && !incorrect;
        SourceColorPair? fill = correct ? style.Appearance.Success
            : incorrect ? style.Appearance.Danger
            : neutralDisabled && sourceClass ? null
            : sourceClass ? style.Appearance.BootstrapOutlineSecondaryFill
                : style.Appearance.BodyBackground;
        var border = correct ? style.Appearance.Success
            : incorrect ? style.Appearance.Danger
            : neutralDisabled && sourceClass
                ? style.Appearance.BootstrapNeutralDisabledBorder
                : style.Appearance.BootstrapButtonBorder;
        var title = style.Text(Views.Text(audioLetter ?? answer),
            SourceTextRole.Title3);
        // The graded options carry the result, so it is not told by color alone. The
        // mark's place is kept on every option, so grading does not move or rewrap text.
        var mark = Views.Image(incorrect ? "xmark.circle.fill" : "checkmark.circle.fill")
            .WithHidden(!correct && !incorrect)
            .WithAccessibilityIdentifier(correct ? "native.quiz.choice.result.correct"
                : incorrect ? "native.quiz.choice.result.incorrect"
                : "native.quiz.choice.result.none");
        if (correct || incorrect)
        {
            title.WithForegroundColor("#FFFFFF", "#FFFFFF");
            mark.WithForegroundColor("#FFFFFF", "#FFFFFF");
        }
        else if (neutralDisabled && sourceClass)
            title.WithForegroundColor(border.Light, border.Dark);
        var label = Views.HStack(title.WithFlexibleWidth(), mark);
        label.Spacing = SourceUiLayout.ItemGap;
        label.WithFlexibleWidth()
            .WithPadding(16, 16, 16, 16);
        if (!sourceClass)
            label.WithRoundedSurface(fill?.Light ?? style.Appearance.BodyBackground.Light,
                fill?.Dark ?? style.Appearance.BodyBackground.Dark,
                style.Appearance.BootstrapRadius,
                border.Light, border.Dark, 1);
        var button = Views.Button(label, select)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight);
        if (sourceClass)
        {
            button.WithButtonAppearance(fill?.Light, fill?.Dark,
                    border.Light, border.Dark, style.Appearance.BootstrapRadius)
                .WithButtonBorderWidth(style.Appearance.BootstrapBaseButtonBorderWidth);
            style.WithSketchyBootstrapCorners(button);
        }
        if (audioLetter is null)
            button.WithAccessibilityLabel(answer + ChoiceResultCaption(state, answer, text));
        button.IsEnabled = !answered && !busy;
        return button;
    }

    /// <summary>", Correct" or ", Incorrect" for a graded option, otherwise empty.</summary>
    private static string ChoiceResultCaption(
        NativeQuizState state, string answer, Func<string, string> text)
    {
        if (state.Phase is not (NativePhase.Feedback or NativePhase.Error))
            return string.Empty;
        return IsCorrectChoice(state, answer) ? $", {text(NativeText.QuizCorrect)}"
            : IsWrongPick(state, answer) ? $", {text(NativeText.QuizIncorrect)}"
            : string.Empty;
    }

    private static bool IsCorrectChoice(NativeQuizState state, string answer) =>
        string.Equals(answer, state.CorrectAnswerAfterGrading,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsWrongPick(NativeQuizState state, string answer) =>
        !IsCorrectChoice(state, answer) && string.Equals(answer,
            state.SelectedAnswer, StringComparison.OrdinalIgnoreCase);

    /// <summary>The graded turn's result: the banner text, and what VoiceOver announces.</summary>
    private static string FeedbackStatus(NativeQuizState state, Func<string, string> text) =>
        state.CompleteForm is { Length: > 0 } fullForm && state.AnswerWasCorrect == true
            ? string.Format(CultureInfo.CurrentCulture,
                text("NativeVocabQuiz_FuzzyFeedback"), fullForm)
            : state.AnswerWasCorrect == true
                ? text("NativeVocabQuiz_CorrectFeedback")
                : state.CorrectAnswerAfterGrading is { Length: > 0 } solution
                    ? string.Format(CultureInfo.CurrentCulture,
                        text("NativeVocabQuiz_IncorrectFeedback"), solution)
                    : text(NativeText.QuizIncorrect);

}
