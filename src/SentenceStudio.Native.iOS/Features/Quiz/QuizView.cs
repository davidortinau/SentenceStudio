using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Quiz;

internal static class QuizView
{
    internal static SwiftView Page(
        SourceUiStyle style,
        NativeQuizState state,
        QuizViewInputs inputs,
        Func<string, string> text,
        QuizActions actions)
    {
        var answer = inputs.Answer;
        var correctionCompleted = inputs.CorrectionCompleted;
        var busy = inputs.Busy;
        var canRetry = inputs.CanRetry;
        var answerShouldFocus = inputs.AnswerShouldFocus;
        var sentenceInput = inputs.SentenceInput;
        var answerChanged = actions.AnswerChanged;
        var answerFocusChanged = actions.AnswerFocusChanged;
        var canSelectAudioChoice = actions.CanSelectAudioChoice;
        var selectChoice = actions.SelectChoice;
        var playPrompt = actions.PlayPrompt;
        var playChoiceAudio = actions.PlayChoiceAudio;
        var submitText = actions.SubmitText;
        var submitCorrection = actions.SubmitCorrection;
        var overrideAsCorrect = actions.OverrideAsCorrect;
        var next = actions.Next;
        var resume = actions.Resume;
        var startFresh = actions.StartFresh;
        var continueRound = actions.ContinueRound;
        var done = actions.Done;
        var retry = actions.Retry;
        var openDetails = actions.OpenDetails;
        var toggleSentenceHints = actions.ToggleSentenceHints;
        var photoRendered = actions.PhotoRendered;
        var photoFailed = actions.PhotoFailed;
        var openPhoto = actions.OpenPhoto;
        var togglePhotoVisible = actions.TogglePhotoVisible;
        var sentenceChanged = actions.SentenceChanged;
        var submitSentence = actions.SubmitSentence;
        var cancelSentence = actions.CancelSentence;
        var continueAfterSentence = actions.ContinueAfterSentence;
        var items = new List<SwiftView>();
        if (state.HasPendingSync)
            items.Add(style.Text(Views.Text(
                    text("Native_SyncPending")),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.quiz.pending"));
        if (state.Phase == NativePhase.Loading && state.Sentence is null)
        {
            items.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.quiz.loading.spinner"));
            items.Add(style.Text(Views.Text(text("Common_Loading")),
                    SourceTextRole.Body1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.quiz.status"));
            return QuizPage(items, style);
        }
        if (state.ErrorKey is not null)
            items.Add(style.Text(Views.Text(text(state.ErrorKey)),
                    SourceTextRole.Body1, SourceTextTone.Danger)
                .WithAccessibilityIdentifier("native.quiz.error"));

        switch (state.Phase)
        {
            case NativePhase.Loading when state.Sentence is not null:
                QuizSentenceView.Add(items, state, sentenceInput, busy, style, text,
                    sentenceChanged, submitSentence, cancelSentence,
                    continueAfterSentence);
                break;
            case NativePhase.ResumeChoice:
                var resumeContent = new List<SwiftView>
                {
                    style.Text(Views.Text(text("VocabQuiz_ResumePrompt")),
                        SourceTextRole.Title1, sourceHeading: true),
                };
                if (state.ErrorKey is null)
                    resumeContent.Add(style.PrimaryButton(
                            text(NativeText.Resume), resume)
                        .WithAccessibilityIdentifier("native.quiz.resume.continue"));
                resumeContent.Add(style.SecondaryButton(
                        text(NativeText.StartFresh), startFresh)
                    .WithAccessibilityIdentifier("native.quiz.start-fresh"));
                items.Add(style.Card(Views.VStack(resumeContent.ToArray())
                        .WithPadding(SourceUiLayout.LargeCardInset),
                        maxWidth: 520)
                    .WithAccessibilityIdentifier(NativeAccess.QuizResume));
                break;
            case NativePhase.Ready:
            case NativePhase.Feedback:
                if (state.Sentence is not null)
                {
                    QuizSentenceView.Add(items, state, sentenceInput, busy, style, text,
                        sentenceChanged, submitSentence, cancelSentence,
                        continueAfterSentence);
                    break;
                }
                QuizQuestionView.Add(items, state, answer, correctionCompleted,
                    busy, style, text, answerChanged,
                    answerFocusChanged, answerShouldFocus, canSelectAudioChoice,
                    selectChoice, playPrompt, playChoiceAudio,
                    submitText, submitCorrection,
                    openDetails, toggleSentenceHints, photoRendered, photoFailed,
                    openPhoto, togglePhotoVisible);
                if (state.Phase == NativePhase.Feedback)
                    QuizFeedbackView.Add(items, state, style, text, overrideAsCorrect, next);
                break;
            case NativePhase.Error when state.Prompt is not null
                && state.ErrorKey == NativeText.SaveFailed
                && state.Destination != NativeDestination.QuizSummary:
                QuizQuestionView.Add(items, state, answer, correctionCompleted,
                    busy, style, text,
                    answerChanged, answerFocusChanged, answerShouldFocus,
                    canSelectAudioChoice,
                    selectChoice, playPrompt, playChoiceAudio,
                    submitText, submitCorrection,
                    openDetails, toggleSentenceHints, photoRendered, photoFailed,
                    openPhoto, togglePhotoVisible);
                if (canRetry)
                    items.Add(style.PrimaryButton(text(NativeText.Retry), retry)
                        .WithAccessibilityIdentifier("native.quiz.retry"));
                break;
            case NativePhase.Error when state.Sentence is not null:
                QuizSentenceView.Add(items, state, sentenceInput, busy, style, text,
                    sentenceChanged, submitSentence, cancelSentence,
                    continueAfterSentence);
                if (canRetry)
                    items.Add(style.PrimaryButton(text(NativeText.Retry), retry)
                        .WithAccessibilityIdentifier("native.quiz.sentence.retry"));
                break;
            case NativePhase.Summary:
            case NativePhase.Error when state.Destination == NativeDestination.QuizSummary:
                QuizSummaryView.Add(items, state, canRetry, style, text,
                    continueRound, done, retry);
                break;
            case NativePhase.Empty:
            case NativePhase.Error:
            case NativePhase.Blocked:
                if (state.ErrorKey is null)
                    items.Add(style.Text(Views.Text(text(NativeText.QuizUnavailable)),
                        SourceTextRole.Body1, SourceTextTone.Secondary));
                if (state.Destination == NativeDestination.QuizSummary)
                {
                    items.Add(style.Card(Views.VStack(
                        style.Text(Views.Text(text(NativeText.QuizSummary)),
                            SourceTextRole.Title1, sourceHeading: true),
                        style.Text(Views.Text($"{state.CorrectCount}/{state.TotalTurns}"),
                            SourceTextRole.Body1))
                        .WithPadding(SourceUiLayout.CardInset))
                        .WithAccessibilityIdentifier(NativeAccess.QuizSummary));
                }
                if (canRetry)
                    items.Add(Views.Button(text(NativeText.Retry), retry)
                        .WithAccessibilityIdentifier("native.quiz.retry"));
                break;
        }
        SwiftView? footer = null;
        if (state.Prompt is not null && state.Sentence is null
            && state.Phase is (NativePhase.Ready
                or NativePhase.Feedback or NativePhase.Error)
            && state.Destination != NativeDestination.QuizSummary)
        {
            var counts = Views.HStack(
                    style.Text(Views.Text($"{state.CurrentTurn} / {state.TurnsInRound}"),
                            SourceTextRole.Body1, SourceTextTone.Secondary)
                        .WithAccessibilityIdentifier("native.quiz.progress")
                        .WithFlexibleWidth(),
                    style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                            text("VocabQuiz_CorrectCount"), state.CorrectCount)),
                            SourceTextRole.BadgeSmall)
                        .WithPadding(4, 12, 4, 12)
                        .WithForegroundColor("#FFFFFF", "#FFFFFF")
                        .WithRoundedSurface(style.Appearance.Success.Light,
                            style.Appearance.Success.Dark, 16)
                        .WithAccessibilityIdentifier("native.quiz.correct-count"))
                .WithFlexibleWidth()
                .WithPadding(12, 16, 12, 16)
                .WithBackgroundColor(style.Appearance.BodyBackground.Light,
                    style.Appearance.BodyBackground.Dark)
                .WithAccessibilityIdentifier("native.quiz.footer");
            if (state is {
                Phase: NativePhase.Feedback, RequiresCorrection: false,
                FeedbackStartedAt: not null, AutoAdvanceDurationMs: > 0 })
            {
                var countdown = style.Progress(Views.Progress(0,
                        state.AutoAdvanceDurationMs, 3, text(NativeText.QuizNext)),
                        completion: false)
                    .WithAccessibilityIdentifier(
                        "native.quiz.auto-advance.progress");
                footer = Views.VStack(
                        Views.Divider().WithAccessibilityIdentifier(
                            "native.quiz.footer.divider"), countdown, counts)
                    .WithBackgroundColor(style.Appearance.BodyBackground.Light,
                        style.Appearance.BodyBackground.Dark);
            }
            else
                footer = Views.VStack(
                        Views.Divider().WithAccessibilityIdentifier(
                            "native.quiz.footer.divider"), counts)
                    .WithBackgroundColor(style.Appearance.BodyBackground.Light,
                        style.Appearance.BodyBackground.Dark);
        }
        return QuizPage(items, style, footer);
    }

    internal static SwiftView WithToolbarAndPhoto(
        SwiftView page, NativeQuizState state, NativeQuizPhotoAsset? photo,
        bool canOpenSentence, bool canTogglePhotoText, string photoTextTitle,
        string photoAccessibilityLabel, string photoCloseLabel,
        bool isPhotoPresented, Func<string, string> text,
        Action openSentence, Action togglePhotoText, Action dismissPhoto,
        Action<string, string> photoFailed, out SwiftView? photoCover)
    {
        var actions = new List<SwiftView>();
        if (canOpenSentence)
            actions.Add(Views.Button(text("NativeVocabQuiz_UseInSentence"),
                    openSentence)
                .WithAccessibilityIdentifier("native.quiz.use-in-sentence"));
        if (canTogglePhotoText)
            actions.Add(Views.Button(photoTextTitle, togglePhotoText)
                .WithAccessibilityIdentifier("native.quiz.photo-text"));
        var content = actions.Count == 0 ? page
            : Views.Toolbar(page,
                Views.Menu(text("More"), actions.ToArray())
                    .WithAccessibilityIdentifier("native.quiz.menu"));
        if (photo is not { Bytes: { Length: > 0 } })
        {
            photoCover = null;
            return content;
        }

        var turnId = state.TurnId;
        var fullPhoto = Views.ZoomablePhoto(photo.Bytes,
                photoAccessibilityLabel,
                onFailed: code =>
                {
                    if (turnId is not null)
                        photoFailed(turnId, code);
                })
            .WithAccessibilityIdentifier("native.quiz.photo.zoom");
        var close = Views.Button(Views.Image("xmark"), dismissPhoto)
            .WithAccessibilityLabel(photoCloseLabel)
            .WithAccessibilityIdentifier("native.quiz.photo.close");
        photoCover = Views.FullScreenPhotoCover(
                content, fullPhoto, close, dismissPhoto)
            .WithAccessibilityIdentifier("native.quiz.photo.cover");
        photoCover.IsPresented = isPhotoPresented;
        return photoCover;
    }

    private static SwiftView QuizPage(
        List<SwiftView> items, SourceUiStyle style, SwiftView? footer = null)
    {
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, 16, 12, 16)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.quiz.scroll.content");
        content.Spacing = SourceUiLayout.PageGap;
        var page = Views.Scroll(content)
            .WithAccessibilityIdentifier("native.quiz.screen");
        return style.Page(footer is null ? page : Views.BottomInset(page, footer));
    }

}
