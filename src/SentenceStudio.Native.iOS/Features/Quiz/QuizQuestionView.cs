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
        Func<int, bool> canSelectAudioChoice,
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
        Action togglePhotoVisible)
    {
        if (state.Prompt is { } prompt)
        {
            if (prompt.ShowWrittenPrompt && !string.IsNullOrWhiteSpace(prompt.PromptText))
                items.Add(style.Text(Views.Text(prompt.PromptText),
                        SourceTextRole.Display, SourceTextTone.Primary,
                        sourceHeading: true)
                    .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(NativeAccess.QuizQuestion));
            else
                items.Add(style.Text(Views.Text(text(prompt.ShowPhoto
                            ? prompt.CanPlayPromptAudio
                                ? "VocabQuiz_ListenLookAnswer"
                                : "VocabQuiz_LookAndAnswer"
                            : "VocabQuiz_ListenAndAnswer")),
                        SourceTextRole.Display, SourceTextTone.Primary,
                        sourceHeading: true)
                    .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(NativeAccess.QuizQuestion));
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
                (prompt.PhotoRequested || prompt.ShowPhoto))
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
            if (state.CorrectAnswerAfterGrading is { Length: > 0 } correct)
                items.Add(style.Text(Views.Text(correct), SourceTextRole.Title2)
                    .WithAccessibilityIdentifier("native.quiz.correct-answer"));
            var status = state.CompleteForm is { Length: > 0 } fullForm
                && state.AnswerWasCorrect == true
                ? string.Format(CultureInfo.CurrentCulture,
                    text("NativeVocabQuiz_FuzzyFeedback"), fullForm)
                : state.AnswerWasCorrect == true
                    ? text("NativeVocabQuiz_CorrectFeedback")
                    : state.CorrectAnswerAfterGrading is { Length: > 0 } solution
                        ? string.Format(CultureInfo.CurrentCulture,
                            text("NativeVocabQuiz_IncorrectFeedback"), solution)
                        : text(NativeText.QuizIncorrect);
            items.Add(style.Alert(style.Text(Views.Text(status),
                        SourceTextRole.DefaultBody),
                    success: state.AnswerWasCorrect == true, compact: true)
                .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier(NativeAccess.QuizFeedback));
        }
        if (state.Mode == "MultipleChoice")
        {
            var audioChoices = state.Prompt is {
                IsTargetLanguagePrompt: false }
                && state.CanPlayAnswerAudio;
            for (var index = 0; index < state.Choices.Length; index++)
            {
                var optionIndex = index;
                var choice = state.Choices[index];
                var id = $"{NativeAccess.QuizChoice}.{index}";
                var choiceButton = QuizChoice(style, state, choice.Text,
                        busy || state.Phase == NativePhase.Ready
                            && !state.CanSubmit
                            || audioChoices && !canSelectAudioChoice(index),
                        () => selectChoice(choice.Text),
                        audioChoices ? ((char)('A' + index)).ToString() : null)
                    .WithAccessibilityIdentifier(id);
                if (audioChoices)
                {
                    var letter = ((char)('A' + index)).ToString();
                    choiceButton.WithAccessibilityLabel(
                        string.Format(CultureInfo.CurrentCulture,
                            text(canSelectAudioChoice(index)
                                ? "NativeVocabQuiz_ChooseChoice"
                                : "NativeVocabQuiz_ListenToChoose"), letter));
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
                        correcting ? submitCorrection : submitText)
                    .WithAccessibilityIdentifier(correcting
                        ? "native.quiz.correction" : "native.quiz.submit");
                SourceUiStyle.SetCustomButtonAvailability(submit, style.Appearance,
                    SourceButtonRole.Primary,
                    !busy && (correcting || state.CanSubmit)
                        && !string.IsNullOrWhiteSpace(answer));
            }
            items.Add(style.Text(Views.Text(text("VocabQuiz_TypeTranslation")),
                SourceTextRole.Body2, SourceTextTone.Secondary));
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
                items.Add(Views.HStack(submit)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
        }
    }

    private static SwiftView QuizChoice(
        SourceUiStyle style, NativeQuizState state, string answer, bool busy,
        Action select, string? audioLetter = null)
    {
        var answered = state.Phase is NativePhase.Feedback or NativePhase.Error;
        var correct = answered && string.Equals(answer,
            state.CorrectAnswerAfterGrading, StringComparison.OrdinalIgnoreCase);
        var incorrect = answered && !correct && string.Equals(answer,
            state.SelectedAnswer, StringComparison.OrdinalIgnoreCase);
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
        var label = style.Text(Views.Text(audioLetter ?? answer),
            SourceTextRole.Title3);
        if (correct || incorrect)
            label.WithForegroundColor("#FFFFFF", "#FFFFFF");
        else if (neutralDisabled && sourceClass)
            label.WithForegroundColor(border.Light, border.Dark);
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
        button.IsEnabled = !answered && !busy;
        return button;
    }

}
