using SentenceStudio.Native;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidQuizInputPolicyTests
{
    [Fact]
    public void NewReadyTurnClearsPriorAnswerBeforeItCanBeResubmitted()
    {
        Assert.True(AndroidQuizInputPolicy.ShouldClearForNewTurn(
            "first-turn", new(
                NativePhase.Ready, NativeDestination.VocabularyQuiz,
                TurnId: "second-turn")));
        Assert.True(AndroidQuizInputPolicy.ShouldClearForNewTurn(
            null, new(
                NativePhase.Ready, NativeDestination.VocabularyQuiz,
                TurnId: "first-after-resume")));
    }

    [Fact]
    public void SameTurnFeedbackAndErrorPreserveCorrectionUntilLearnerAdvances()
    {
        Assert.False(AndroidQuizInputPolicy.ShouldClearForNewTurn(
            "current-turn", new(
                NativePhase.Feedback, NativeDestination.VocabularyQuiz,
                TurnId: "current-turn")));
        Assert.False(AndroidQuizInputPolicy.ShouldClearForNewTurn(
            "current-turn", new(
                NativePhase.Error, NativeDestination.VocabularyQuiz,
                TurnId: "current-turn")));
        Assert.False(AndroidQuizInputPolicy.ShouldClearForNewTurn(
            "current-turn", new(
                NativePhase.Ready, NativeDestination.VocabularyQuiz,
                TurnId: "current-turn")));
    }

    [Fact]
    public void SummaryAndPlanDoNotCreateAnEditableQuizTurn()
    {
        Assert.False(AndroidQuizInputPolicy.ShouldClearForNewTurn(
            "current-turn", new(
                NativePhase.Summary, NativeDestination.QuizSummary,
                TurnId: "summary-turn")));
        Assert.False(AndroidQuizInputPolicy.ShouldClearForNewTurn(
            "current-turn", new(
                NativePhase.Ready, NativeDestination.TodaysPlan,
                TurnId: "return-turn")));
    }
}
