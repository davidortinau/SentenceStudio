using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

internal static class AndroidQuizPhotoPresentationPolicy
{
    /// <summary>
    /// The full-screen viewer stays only for the owner, turn and photo it opened
    /// with, in the phase it opened in: the question, or its feedback (the
    /// mnemonic image revealed after a correct answer). Grading, advancing or
    /// leaving closes it.
    /// </summary>
    public static bool CanPresent(
        NativeQuizState state, string? activeOwner, string? openedOwner,
        string? openedTurn, string? openedUri, NativePhase openedPhase) =>
        !string.IsNullOrWhiteSpace(activeOwner)
        && activeOwner == openedOwner
        && state.Destination == NativeDestination.VocabularyQuiz
        && openedPhase is NativePhase.Ready or NativePhase.Feedback
        && state.Phase == openedPhase
        && state.Overlay == NativeQuizOverlay.FullscreenPhoto
        && state.Sentence is null
        && state.Prompt is { ShowPhoto: true, PhotoUri: { } photo }
        && !string.IsNullOrWhiteSpace(state.TurnId)
        && state.TurnId == openedTurn
        && !string.IsNullOrWhiteSpace(photo)
        && photo == openedUri;
}
