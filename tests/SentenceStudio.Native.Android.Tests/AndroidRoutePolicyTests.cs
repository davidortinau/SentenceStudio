using System.Reflection;
using SentenceStudio.Native;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidRoutePolicyTests
{
    [Fact]
    public void SingleRealTopLevelDestination_DoesNotRenderBottomTabs()
    {
        Assert.Equal(NativeDestination.Dashboard,
            Assert.Single(AndroidRoutePolicy.TopLevelDestinations));
        Assert.False(AndroidRoutePolicy.UsesBottomTabs);
        Assert.DoesNotContain(NativeDestination.TodaysPlan,
            AndroidRoutePolicy.TopLevelDestinations);
        Assert.DoesNotContain(NativeDestination.VocabularyQuiz,
            AndroidRoutePolicy.TopLevelDestinations);
    }

    [Fact]
    public void OnlyReadyAuthenticatedDashboardOpensPlan()
    {
        Assert.Equal(NativeDestination.Dashboard,
            AndroidRoutePolicy.AfterAuth(new(
                NativePhase.Ready, NativeDestination.Dashboard, "isolated-user")));
        Assert.Equal(NativeDestination.SignIn,
            AndroidRoutePolicy.AfterAuth(new(
                NativePhase.PendingSync, NativeDestination.Dashboard,
                "isolated-user", NativeText.ConnectionError)));
        Assert.Equal(NativeDestination.SignIn,
            AndroidRoutePolicy.AfterAuth(new(
                NativePhase.Error, NativeDestination.SignIn,
                ErrorKey: NativeText.InvalidCredentials)));
    }

    [Fact]
    public void OnboardingRemainsAnExplicitNativeDestination()
    {
        Assert.Equal(NativeDestination.Onboarding,
            AndroidRoutePolicy.AfterAuth(new(
                NativePhase.Ready, NativeDestination.Onboarding, "isolated-user")));
    }

    [Fact]
    public void QuizBackAndDoneRespectDurablePresenterDestinations()
    {
        Assert.True(AndroidRoutePolicy.HandlesBack(NativeDestination.VocabularyQuiz));
        Assert.True(AndroidRoutePolicy.HandlesBack(NativeDestination.QuizSummary));
        Assert.False(AndroidRoutePolicy.HandlesBack(NativeDestination.Dashboard));

        Assert.Equal(NativeDestination.QuizSummary,
            AndroidRoutePolicy.AfterQuiz(new(
                NativePhase.Summary, NativeDestination.QuizSummary)));
        Assert.Equal(NativeDestination.Dashboard,
            AndroidRoutePolicy.AfterQuiz(new(
                NativePhase.Ready, NativeDestination.TodaysPlan)));
        Assert.Equal(NativeDestination.VocabularyQuiz,
            AndroidRoutePolicy.AfterQuiz(new(
                NativePhase.Error, NativeDestination.TodaysPlan,
                ErrorKey: NativeText.SaveFailed)));
        Assert.Equal(NativeDestination.Dashboard,
            AndroidRoutePolicy.AfterQuizBack(new(
                NativePhase.Blocked, NativeDestination.VocabularyQuiz,
                ErrorKey: NativeText.QuizUnavailable)));
    }

    [Fact]
    public void ResumeChoiceBackDelegatesToThePresenterAndFollowsItsDestination()
    {
        var root = typeof(AndroidRoutePolicyTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        var source = File.ReadAllText(Path.Combine(root, "AndroidSliceShell.cs"));
        var back = source[source.IndexOf("public bool HandleBack()", StringComparison.Ordinal)
            ..source.IndexOf("public void SignIn()", StringComparison.Ordinal)];
        Assert.Contains("await _quiz.BackAsync(cancellationToken)", back);
        Assert.Contains("await ApplyQuizStateAsync(state, cancellationToken)", back);
        Assert.DoesNotContain("ExitResumeChoiceWithoutSaving", back);
        Assert.DoesNotContain("SaveSnapshot", back);
        Assert.Equal(NativeDestination.Dashboard,
            AndroidRoutePolicy.AfterQuiz(new(
                NativePhase.Ready, NativeDestination.TodaysPlan)));
    }
}
