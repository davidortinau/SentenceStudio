using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

internal static class AndroidRoutePolicy
{
    public static NativeDestination AfterAuth(NativeAuthState state) =>
        state.Phase == NativePhase.Ready
            && state.Destination is NativeDestination.Dashboard or NativeDestination.Onboarding
            ? state.Destination : NativeDestination.SignIn;

    public static NativeDestination AfterQuiz(NativeQuizState state) =>
        state.Destination == NativeDestination.TodaysPlan
            && state.Phase == NativePhase.Ready
            ? NativeDestination.Dashboard
            : state.Destination == NativeDestination.QuizSummary
                ? NativeDestination.QuizSummary : NativeDestination.VocabularyQuiz;

    public static NativeDestination AfterQuizBack(NativeQuizState state) =>
        state.Phase == NativePhase.Blocked
            && state.Destination == NativeDestination.VocabularyQuiz
            ? NativeDestination.Dashboard : AfterQuiz(state);

    public static bool HandlesBack(NativeDestination destination) =>
        destination is NativeDestination.VocabularyQuiz or NativeDestination.QuizSummary;
}
