using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>The quiz command whose save failed, so Retry repeats that exact command.</summary>
internal enum AndroidQuizFailedCommand { None, Answer, Override, Correction }

/// <summary>What the quiz Retry button does for the current error.</summary>
internal enum AndroidQuizRetry { None, Done, Sentence, Override, Correction, PendingSave, Reopen }

/// <summary>Turn-level rules of the Compose quiz screen that do not need Android types.</summary>
internal static class AndroidQuizTurnPolicy
{
    /// <summary>
    /// A native-language prompt in multiple choice with playable target-language
    /// answers shows lettered options with play buttons (source VocabQuiz.razor
    /// ShowAudioChoiceControls); otherwise each option shows its text.
    /// </summary>
    public static bool UsesAudioChoices(NativeQuizState state) =>
        state.Mode == "MultipleChoice"
        && state.Sentence is null
        && state.Prompt is { IsTargetLanguagePrompt: false }
        && state.CanPlayAnswerAudio;

    /// <summary>
    /// Captain 2026-11-03 (AF-Q03): a lettered answer can be picked at once.
    /// The head adds no listen-first gate of its own; it follows the
    /// presenter's <see cref="NativeQuizState.CanSubmit"/>.
    /// </summary>
    public static bool CanChoose(NativeQuizState state, bool busy) =>
        !busy
        && state.Phase == NativePhase.Ready
        && state.Overlay == NativeQuizOverlay.None
        && state.Sentence is null
        && state.CanSubmit;

    public static bool CanPlayChoice(NativeQuizState state, bool busy) =>
        !busy
        && state.Phase is NativePhase.Ready or NativePhase.Feedback
        && state.Overlay == NativeQuizOverlay.None
        && UsesAudioChoices(state);

    public static string Letter(int index) =>
        index is >= 0 and < 26 ? ((char)('A' + index)).ToString() : (index + 1).ToString();

    /// <summary>Source: "Use in a sentence" is offered only while a question waits for an answer.</summary>
    public static bool CanOpenSentence(NativeQuizState state, bool busy) =>
        !busy
        && state.Destination == NativeDestination.VocabularyQuiz
        && state is { Phase: NativePhase.Ready, Sentence: null, Prompt: not null,
            Overlay: NativeQuizOverlay.None }
        && !string.IsNullOrWhiteSpace(state.TurnId);

    /// <summary>Source: Hide Text / Show Text with a native-language photo prompt, before grading.</summary>
    public static bool CanTogglePhotoText(NativeQuizState state, bool busy) =>
        !busy
        && state.Destination == NativeDestination.VocabularyQuiz
        && state is { Phase: NativePhase.Ready, Sentence: null,
            Prompt.CanTogglePhotoText: true, Overlay: NativeQuizOverlay.None }
        && !string.IsNullOrWhiteSpace(state.TurnId);

    public static bool CanToggleSentenceHints(NativeQuizState state, bool busy) =>
        !busy
        && state.Phase is NativePhase.Ready or NativePhase.Feedback
        && state.Overlay == NativeQuizOverlay.None
        && state.Sentence is null
        && state.Prompt is { IsTargetLanguagePrompt: true }
        && state.CanShowSentenceHints;

    public static bool CanOpenDetails(NativeQuizState state, bool busy) =>
        !busy
        && state.Phase is NativePhase.Ready or NativePhase.Feedback
        && state.Overlay == NativeQuizOverlay.None
        && state.Sentence is null
        && state.Prompt is not null;

    /// <summary>
    /// Retry repeats the command whose save failed, keeping the graded turn:
    /// an override or a correction is resubmitted (the presenter accepts both in
    /// their own save-failed state), any other graded turn re-saves its pending
    /// snapshot, a sentence turn re-saves its submission, and the summary
    /// completes again. A launch that never reached a turn opens again.
    /// </summary>
    public static AndroidQuizRetry Route(
        NativeQuizState state, AndroidQuizFailedCommand failed, bool hasLaunch)
    {
        if (state.Phase != NativePhase.Error)
            return AndroidQuizRetry.None;
        if (state.Destination == NativeDestination.QuizSummary)
            return AndroidQuizRetry.Done;
        if (state.Destination != NativeDestination.VocabularyQuiz)
            return AndroidQuizRetry.None;
        if (state.Sentence is not null)
            return state.ErrorKey == NativeText.SaveFailed
                ? AndroidQuizRetry.Sentence : AndroidQuizRetry.None;
        if (state.Prompt is not null && state.ErrorKey == NativeText.SaveFailed)
            return failed switch
            {
                AndroidQuizFailedCommand.Override => AndroidQuizRetry.Override,
                AndroidQuizFailedCommand.Correction when state.RequiresCorrection =>
                    AndroidQuizRetry.Correction,
                _ => AndroidQuizRetry.PendingSave
            };
        if (state.CurrentTurn == 0 && state.ErrorKey == NativeText.ConnectionError
            && hasLaunch)
            return AndroidQuizRetry.Reopen;
        return AndroidQuizRetry.None;
    }

    /// <summary>
    /// Back leaves the quiz once the presenter routes to Today's Plan, including
    /// when it refused to overwrite a newer committed turn (Error, Today's Plan):
    /// that run resumes from its durable snapshot and receipt.
    /// </summary>
    public static bool BackLeavesQuiz(NativeQuizState state) =>
        state.Destination == NativeDestination.TodaysPlan
            && state.Phase is NativePhase.Ready or NativePhase.Error
        || state.Phase == NativePhase.Blocked
            && state.Destination == NativeDestination.VocabularyQuiz;
}
