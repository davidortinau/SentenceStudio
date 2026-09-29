using System.Diagnostics;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

public sealed class NativeQuizTimingTests
{
    [Fact]
    public void AutoAdvanceDelay_MatchesAllPersistedApprovedSettingsAcrossRestart()
    {
        using var fixture = new NativeSliceFixture();
        var preferences = fixture.QuizPreferences;
        var quiz = fixture.Quiz;
        Assert.Equal(2000, preferences.AutoAdvanceDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), quiz.AutoAdvanceDelay);

        foreach (var milliseconds in NativeQuizTiming.ApprovedDurationsMs)
        {
            Assert.Equal(NativeQuizTimingUpdate.Applied,
                NativeQuizTiming.TrySet(preferences, milliseconds));
            Assert.Equal(milliseconds, preferences.AutoAdvanceDuration);
            Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), quiz.AutoAdvanceDelay);
        }

        fixture.Restart();
        Assert.Equal(10000, fixture.QuizPreferences.AutoAdvanceDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(10000), fixture.Quiz.AutoAdvanceDelay);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1500)]
    [InlineData(5500)]
    public async Task AutoAdvance_UsesPersistedSourcePreferenceDelay(int milliseconds)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        Assert.Equal(NativeQuizTimingUpdate.Applied,
            NativeQuizTiming.TrySet(fixture.QuizPreferences, milliseconds));
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), quiz.AutoAdvanceDelay);

        var watch = Stopwatch.StartNew();
        var pendingAdvance = quiz.AutoAdvanceAsync();
        await Task.Delay(75);
        Assert.False(pendingAdvance.IsCompleted);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        var next = await pendingAdvance;
        watch.Stop();
        Assert.InRange(watch.ElapsedMilliseconds, milliseconds - 50, milliseconds + 5000);
        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.Equal(2, next.CurrentTurn);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ManualNextBeforeAutoAdvance_DoesNotGradeOrMoveTheNextQuestion()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        Assert.Equal(NativeQuizTimingUpdate.Applied,
            NativeQuizTiming.TrySet(fixture.QuizPreferences, 1000));
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        var scheduled = quiz.AutoAdvanceAsync();
        var manual = await quiz.NextAsync();
        Assert.Equal(2, manual.CurrentTurn);
        var afterDelay = await scheduled;
        Assert.Equal(2, afterDelay.CurrentTurn);
        Assert.Null(afterDelay.ErrorKey);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }
}
