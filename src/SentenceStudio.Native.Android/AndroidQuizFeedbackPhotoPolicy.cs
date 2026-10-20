using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

internal static class AndroidQuizFeedbackPhotoPolicy
{
    public static bool CanLoad(NativeQuizState state, string? owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && state.Destination == NativeDestination.VocabularyQuiz
        && state.Phase == NativePhase.Feedback
        && state.AnswerWasCorrect == true
        && state.Prompt?.ShowPhoto == false
        && !string.IsNullOrWhiteSpace(state.TurnId)
        && !string.IsNullOrWhiteSpace(state.FeedbackPhotoCandidateUri);

    public static bool CanDisplay(
        NativeQuizState state, string? owner, string? loadedOwner,
        string? loadedTurn, string? loadedUri) =>
        CanLoad(state, owner)
        && owner == loadedOwner
        && state.TurnId == loadedTurn
        && state.FeedbackPhotoCandidateUri == loadedUri;
}
