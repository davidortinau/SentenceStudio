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

    // Regression (iOS 26.5, 2026-10-31): a learner pressed Next while the
    // auto-advance had already committed the turn and was presenting (and
    // auto-playing) the next prompt. The head cancels the timer before sending
    // Next; that cancellation used to abort the committed advance mid-way,
    // leaving the previous feedback on screen with no pending attempt, so every
    // later Next was refused with QuizUnavailable.
    [Fact]
    public async Task NextWhileAutoAdvancePresentsTheNextTurn_IsIgnoredAndNeverBricksTheQuiz()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        Assert.Equal(NativeQuizTimingUpdate.Applied,
            NativeQuizTiming.TrySet(fixture.QuizPreferences, 1000));
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        var feedback = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        var answeredTurn = feedback.TurnId!;

        fixture.Media.HoldPlaybackCompletion = true;
        using var timer = new CancellationTokenSource();
        var scheduled = quiz.AutoAdvanceAsync(timer.Token);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        // ApplicationController.AdvanceQuiz: CancelAutoAdvance, then Next for
        // the feedback turn that is still displayed.
        timer.Cancel();
        var manual = quiz.NextAsync(answeredTurn);
        await Task.Delay(50);
        Assert.True(fixture.Media.FinishPlayback(true));
        var advanced = await scheduled.WaitAsync(TimeSpan.FromSeconds(10));
        var ignored = await manual.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativePhase.Ready, advanced.Phase);
        Assert.Equal(2, advanced.CurrentTurn);
        Assert.NotEqual(answeredTurn, advanced.TurnId);
        Assert.Equal(NativePhase.Ready, ignored.Phase);
        Assert.Equal(advanced.TurnId, ignored.TurnId);
        Assert.Null(ignored.ErrorKey);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Media.HoldPlaybackCompletion = false;
        var second = await quiz.AnswerAsync(ignored.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, second.Phase);
        var third = await quiz.NextAsync(second.TurnId!);
        Assert.True(third.Phase is NativePhase.Ready or NativePhase.Summary);
        Assert.Null(third.ErrorKey);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task UnscopedNextAfterCancelledAutoAdvance_LeavesTheQuizAdvanceable()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        Assert.Equal(NativeQuizTimingUpdate.Applied,
            NativeQuizTiming.TrySet(fixture.QuizPreferences, 1000));
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.Media.AudioAvailable = true;
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        var feedback = await quiz.AnswerAsync(question.Choices[0].Text);

        fixture.Media.HoldPlaybackCompletion = true;
        using var timer = new CancellationTokenSource();
        var scheduled = quiz.AutoAdvanceAsync(timer.Token);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(10));
        timer.Cancel();
        var manual = quiz.NextAsync();
        try { await scheduled.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        await manual.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Media.HoldPlaybackCompletion = false;

        // Whatever the duplicate Next returned, the session must not be stuck
        // on the previous feedback without a pending attempt.
        var state = quiz.State;
        Assert.NotEqual(feedback.TurnId, state.TurnId);
        Assert.Equal(2, state.CurrentTurn);
        var answered = await quiz.AnswerAsync(state.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, answered.Phase);
        var next = await quiz.NextAsync();
        Assert.True(next.Phase is NativePhase.Ready or NativePhase.Summary);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    // Review finding (2026-10-31): the resume replay of a committed receipt
    // used the caller token, so cancelling Resume while it presented the next
    // turn could abandon a half-applied advance.
    [Fact]
    public async Task CancelledResumeWhileReplayingCommittedReceipt_StillCompletesTheAdvance()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!;
        using (var first = fixture.Quiz)
        {
            var question = await first.OpenAsync(launch);
            await first.AnswerAsync(question.Choices[0].Text);
            fixture.FaultSessions.FailNextSnapshot = true;
            Assert.Equal(NativePhase.Error, (await first.NextAsync()).Phase);
            Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.AutoPlayVocabAudio = true;
        fixture.Media.AudioAvailable = true;
        using var quiz = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await quiz.OpenAsync(launch)).Phase);

        fixture.Media.HoldPlaybackCompletion = true;
        using var caller = new CancellationTokenSource();
        var resuming = quiz.ResumeAsync(caller.Token);
        await fixture.Media.PlaybackStarted.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();
        Assert.True(fixture.Media.FinishPlayback(true));
        var resumed = await resuming.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Media.HoldPlaybackCompletion = false;

        Assert.Equal(NativePhase.Ready, resumed.Phase);
        Assert.Equal(2, resumed.CurrentTurn);
        Assert.Null(resumed.ErrorKey);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        var answered = await quiz.AnswerAsync(resumed.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, answered.Phase);
        var next = await quiz.NextAsync(answered.TurnId!);
        Assert.True(next.Phase is NativePhase.Ready or NativePhase.Summary);
        Assert.Null(next.ErrorKey);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }
}
