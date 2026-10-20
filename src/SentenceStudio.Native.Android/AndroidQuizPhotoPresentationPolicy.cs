using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

internal static class AndroidQuizPhotoPresentationPolicy
{
    public static bool CanPresent(
        NativeQuizState state, string? activeOwner, string? openedOwner,
        string? openedTurn, string? openedUri) =>
        !string.IsNullOrWhiteSpace(activeOwner)
        && activeOwner == openedOwner
        && state.Destination == NativeDestination.VocabularyQuiz
        && state.Phase == NativePhase.Ready
        && state.Overlay == NativeQuizOverlay.FullscreenPhoto
        && state.Prompt is { ShowPhoto: true, PhotoUri: { } photo }
        && !string.IsNullOrWhiteSpace(state.TurnId)
        && state.TurnId == openedTurn
        && !string.IsNullOrWhiteSpace(photo)
        && photo == openedUri;
}
