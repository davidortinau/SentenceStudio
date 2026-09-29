using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

public sealed class NativePendingSyncRestartTests
{
    [Fact]
    public async Task CommittedQuizWrite_RemainsPendingAfterRestartUntilOwnerConfirmedSync()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        Assert.False((await fixture.Dashboard.RefreshAsync()).HasPendingSync);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!;

        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch);
            Assert.False(question.HasPendingSync);
            Assert.False((await quiz.AnswerAsync(question.Choices[0].Text))
                .HasPendingSync);
            Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
            var next = await quiz.NextAsync();
            Assert.True(next.HasPendingSync);
            Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await resumed.OpenAsync(launch)).Phase);
        var restored = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Ready, restored.Phase);
        Assert.True(restored.HasPendingSync);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Sync.Outcome = NativeSyncOutcome.Offline;
        Assert.True((await resumed.RetrySyncAsync()).HasPendingSync);
        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        Assert.False((await resumed.RetrySyncAsync()).HasPendingSync);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var confirmed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await confirmed.OpenAsync(launch)).Phase);
        Assert.False((await confirmed.ResumeAsync()).HasPendingSync);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task Q25b_ReceiptCommittedBeforeNextSnapshot_RemainsPendingAcrossRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!;
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch);
            await quiz.AnswerAsync(question.Choices[0].Text);
            fixture.FaultSessions.FailNextSnapshot = true;
            var failed = await quiz.NextAsync();
            Assert.Equal(NativePhase.Error, failed.Phase);
            Assert.True(failed.HasPendingSync);
            Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var recovered = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await recovered.OpenAsync(launch)).Phase);
        var next = await recovered.ResumeAsync();
        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.True(next.HasPendingSync);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task PreMarkerQuizSnapshot_RecoversPendingOnceButNotAfterConfirmedSync()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!;
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch);
            await quiz.AnswerAsync(question.Choices[0].Text);
            Assert.Equal(NativePhase.Ready, (await quiz.NextAsync()).Phase);
        }
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Preferences.Remove(
            "native_sync_pending_v1_" + NativeSliceFixture.UserA);
        fixture.Preferences.Remove(
            "native_sync_revision_v1_" + NativeSliceFixture.UserA);
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var recovered = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await recovered.OpenAsync(launch)).Phase);
        Assert.True((await recovered.ResumeAsync()).HasPendingSync);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        Assert.False((await recovered.RetrySyncAsync()).HasPendingSync);
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var afterConfirmation = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await afterConfirmation.OpenAsync(launch)).Phase);
        Assert.False((await afterConfirmation.ResumeAsync()).HasPendingSync);
    }

    [Fact]
    public async Task PendingOwnerMarker_IsNotSharedWithAnotherAccountAfterRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var planA = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launchA = (await fixture.Dashboard.SelectAssignedQuizAsync(planA))!;
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launchA);
            await quiz.AnswerAsync(question.Choices[0].Text);
            await quiz.NextAsync();
        }
        fixture.Restart();
        var planB = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserB);
        using (var quizB = fixture.Quiz)
        {
            var questionB = await quizB.OpenAsync(
                (await fixture.Dashboard.SelectAssignedQuizAsync(planB))!);
            Assert.False(questionB.HasPendingSync);
        }
        fixture.Activate(NativeSliceFixture.UserA);
        using var quizA = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await quizA.OpenAsync(launchA)).Phase);
        Assert.True((await quizA.ResumeAsync()).HasPendingSync);
    }
}
