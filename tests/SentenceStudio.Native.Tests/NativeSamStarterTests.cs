using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

/// <summary>Starter prompts replace the plan-time chips in an empty Sam conversation.</summary>
public sealed class NativeSamStarterTests
{
    private static NativeSamState Ready(bool canEditPlan = true, bool write = false) => new(
        IsVisible: true, IsOpen: true, Status: NativeSamStatus.Ready, ConversationId: "c1")
    {
        CanEditPlan = canEditPlan,
        SamWriteAvailable = write,
        IsDurableHistoryAvailable = write,
    };

    [Fact]
    public void StartersShowWithoutAPlanToEdit()
        => Assert.True(Ready(canEditPlan: false).ShowStarters);

    [Fact]
    public void StartersLeaveOnceTheLearnerHasSpoken()
        => Assert.False((Ready() with { HasLearnerSpoken = true }).ShowStarters);

    [Fact]
    public void HelpComesFirstAndAddWordNeedsWriteAccess()
    {
        Assert.Equal(
            [NativeSamStarter.Help, NativeSamStarter.VocabularyReview, NativeSamStarter.LastPractice],
            NativeSamStarters.For(Ready(write: false)));
        Assert.Equal(
            [NativeSamStarter.Help, NativeSamStarter.VocabularyReview, NativeSamStarter.AddWord, NativeSamStarter.LastPractice],
            NativeSamStarters.For(Ready(write: true)));
    }

    [Theory]
    [InlineData(NativeSamStarter.Help, "Coach_Starter_Help", "help")]
    [InlineData(NativeSamStarter.VocabularyReview, "Coach_Starter_VocabularyReview", "review")]
    [InlineData(NativeSamStarter.AddWord, "Coach_Starter_AddWord", "addword")]
    [InlineData(NativeSamStarter.LastPractice, "Coach_Starter_LastPractice", "lastpractice")]
    public void EachStarterHasAStableKeyAndId(NativeSamStarter starter, string key, string id)
    {
        Assert.Equal(key, NativeSamStarters.TextKey(starter));
        Assert.Equal(id, NativeSamStarters.AutomationId(starter));
    }
}

/// <summary>Both heads name a shell screen the same way.</summary>
public sealed class NativeSamScreenNameTests
{
    [Theory]
    [InlineData(NativeDestination.Dashboard, "study", "study")]
    [InlineData(NativeDestination.TodaysPlan, "study", "study")]
    [InlineData(NativeDestination.TodaysPlan, "library", "library")]
    [InlineData(NativeDestination.Activity, "study", "activity")]
    [InlineData(NativeDestination.VocabularyQuiz, "study", "vocabulary-quiz")]
    [InlineData(NativeDestination.ResourceEdit, "library", "resources.form")]
    public void ShellDestinationsHaveOneName(NativeDestination destination, string tab, string expected)
        => Assert.Equal(expected, NativeSamScreenContext.ForDestination(destination, tab));
}
