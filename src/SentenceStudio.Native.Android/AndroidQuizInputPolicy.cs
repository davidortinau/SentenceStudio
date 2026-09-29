using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

internal static class AndroidQuizInputPolicy
{
    public static bool ShouldClearForNewTurn(
        string? displayedTurnId, NativeQuizState state) =>
        state.Phase == NativePhase.Ready
        && state.Destination == NativeDestination.VocabularyQuiz
        && state.TurnId is not null
        && !string.Equals(displayedTurnId, state.TurnId, StringComparison.Ordinal);
}
