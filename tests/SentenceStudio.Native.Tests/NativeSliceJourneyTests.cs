using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeSliceJourneyTests
{
    [Fact]
    public async Task SignInPlanAssignedQuizDoneRestart_UsesRealServicesAndSqlite()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);

        Assert.Equal(NativeDestination.SignIn, (await fixture.Auth.RestoreAsync()).Destination);
        Assert.Equal(NativePhase.Error,
            (await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "incorrect")).Phase);
        var signedIn = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativeDestination.Dashboard, signedIn.Destination);
        Assert.Equal(NativeSliceFixture.UserA, signedIn.UserId);

        var dashboard = fixture.Dashboard;
        Assert.Equal(NativePhase.Empty, (await dashboard.LoadAsync()).Phase);
        var generated = await dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, generated.Phase);
        var card = Assert.Single(generated.Cards);
        var launch = await dashboard.SelectAssignedQuizAsync(card.Id);
        Assert.NotNull(launch);

        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch!);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.True(question.CanSubmit);
        Assert.True(question.Prompt!.IsTargetLanguagePrompt);
        Assert.True(question.Prompt.TargetLanguageEngaged);
        await Task.Delay(30);

        while (question.Phase == NativePhase.Ready
            && question.Destination == NativeDestination.VocabularyQuiz)
        {
            var answer = await quiz.AnswerAsync(question.Choices[0].Text);
            Assert.Equal(NativePhase.Feedback, answer.Phase);
            question = await quiz.NextAsync();
        }
        Assert.Equal(NativePhase.Summary, question.Phase);
        Assert.Equal(4, question.TotalTurns);
        Assert.Equal(1, question.RoundsCompleted);
        Assert.Equal(4, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.True(question.Elapsed > TimeSpan.Zero);

        var back = await quiz.DoneAsync();
        Assert.Equal(NativeDestination.TodaysPlan, back.Destination);
        var completed = await dashboard.LoadAsync();
        Assert.True(Assert.Single(completed.Cards).IsCompleted);
        var before = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
        Assert.Equal(ActivitySessionStatus.Completed, before.Session?.Status);
        Assert.Equal(before.Item.CompletedAt, (await fixture.RowsAsync(
            NativeSliceFixture.UserA, card.Id)).Item.CompletedAt);
        await quiz.DoneAsync();
        Assert.Equal(4, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Restart();
        var restored = await fixture.Auth.RestoreAsync();
        Assert.Equal(NativeDestination.Dashboard, restored.Destination);
        var after = await fixture.Dashboard.LoadAsync();
        Assert.True(Assert.Single(after.Cards).IsCompleted);
        Assert.Equal(before.Item.CompletedAt,
            (await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id)).Item.CompletedAt);
        Assert.Equal(4, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task BackAfterPendingFeedback_PreservesSessionWithoutCreditingAttemptOrPlan()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planItemId);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch!);
        await Task.Delay(30);
        var graded = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, graded.Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));

        var back = await quiz.BackAsync();
        Assert.Equal(NativeDestination.TodaysPlan, back.Destination);
        Assert.True(back.Elapsed > TimeSpan.Zero);
        var rows = await fixture.RowsAsync(NativeSliceFixture.UserA, planItemId);
        Assert.False(rows.Item.IsCompleted);
        Assert.Equal(ActivitySessionStatus.InProgress, rows.Session?.Status);
        Assert.True(rows.Session!.StateJson.Contains("\"pendingAttempt\"", StringComparison.Ordinal));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var launchAfterRestart = await fixture.Dashboard.SelectAssignedQuizAsync(planItemId);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(launchAfterRestart!)).Phase);
        var feedback = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        Assert.True(feedback.Elapsed >= back.Elapsed);
        Assert.Equal(1, feedback.TotalTurns);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        await resumed.NextAsync();
        Assert.Equal(1, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt!.WordId));
    }

    [Fact]
    public async Task Q25b_CommittedBeforeSnapshot_RecoversFromDurableReceiptOnce()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planItemId);
        var question = await quiz.OpenAsync(launch!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        var before = await fixture.RowsAsync(NativeSliceFixture.UserA, planItemId);

        fixture.FaultSessions.FailNextSnapshot = true;
        var failedNext = await quiz.NextAsync();
        Assert.Equal(NativePhase.Error, failedNext.Phase);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt!.WordId));
        var stale = await fixture.RowsAsync(NativeSliceFixture.UserA, planItemId);
        Assert.Equal(before.Session!.StateJson, stale.Session!.StateJson);
        Assert.False(stale.Item.IsCompleted);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var newLaunch = await fixture.Dashboard.SelectAssignedQuizAsync(planItemId);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(newLaunch!)).Phase);
        var nextQuestion = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Ready, nextQuestion.Phase);
        Assert.Equal(2, nextQuestion.CurrentTurn);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt!.WordId));
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planItemId)).Item.IsCompleted);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var restartLaunch = await fixture.Dashboard.SelectAssignedQuizAsync(planItemId);
        using var secondResume = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await secondResume.OpenAsync(restartLaunch!)).Phase);
        Assert.Equal(2, (await secondResume.ResumeAsync()).CurrentTurn);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task FailedAttemptWrite_DoesNotAdvance_AndRetryScoresOnlyOnce()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        fixture.Committer.FailNextWrite = true;
        Assert.Equal(NativePhase.Error, (await quiz.NextAsync()).Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));

        // The durable pending snapshot, rather than a guessed success,
        // is the only source for a retry after an uncertain write.
        Assert.Equal(NativePhase.Feedback, (await quiz.ResumeAsync()).Phase);
        Assert.Equal(NativePhase.Ready, (await quiz.NextAsync()).Phase);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ForeignOwnerOrMissingUser_FailsClosedBeforeAnySqliteWrite()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var planA = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launchA = await fixture.Dashboard.SelectAssignedQuizAsync(planA);

        fixture.Activate(NativeSliceFixture.UserB);
        var planB = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserB);
        var dashboardB = await fixture.Dashboard.LoadAsync();
        Assert.All(dashboardB.Cards, card =>
            Assert.Equal(fixture.WordId(NativeSliceFixture.UserB, 0),
                card.FocusVocabularyIds[0]));
        using var quiz = fixture.Quiz;
        Assert.Equal(NativePhase.Blocked, (await quiz.OpenAsync(launchA!)).Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));

        fixture.LoseOwner();
        Assert.Equal(NativePhase.Blocked, (await fixture.Dashboard.LoadAsync()).Phase);
        Assert.Null(await fixture.Dashboard.SelectAssignedQuizAsync(planB));
        Assert.Equal(NativePhase.Blocked,
            (await fixture.Quiz.OpenAsync(launchA!)).Phase);
        Assert.Equal(0, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.ReceiptCountAsync(NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task ExistingPlanRegeneration_PreservesMatchingPartialWork()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var plans = scope.ServiceProvider.GetRequiredService<IPlanService>();
            await plans.UpdateProgressAsync(new DateOnly(2026, 9, 28), itemId, 3);
        }
        var before = await fixture.RowsAsync(NativeSliceFixture.UserA, itemId);

        var regenerated = await fixture.Dashboard.RegenerateAsync();
        Assert.Equal(NativePhase.Ready, regenerated.Phase);
        Assert.Null(regenerated.ErrorKey);
        Assert.Equal(3, Assert.Single(regenerated.Cards).MinutesSpent);
        Assert.Equal(before.Item.Id,
            (await fixture.RowsAsync(NativeSliceFixture.UserA, itemId)).Item.Id);
        Assert.Equal(3, (await fixture.RowsAsync(
            NativeSliceFixture.UserA, itemId)).Item.MinutesSpent);
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, itemId)).Item.IsCompleted);
    }

    [Fact]
    public async Task OfflineLocalWrite_PersistsAcrossRestart_ThenSyncRetryReportsConfirmedOnly()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Sync.Outcome = NativeSyncOutcome.Offline;
        var signedIn = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativeDestination.Dashboard, signedIn.Destination);
        Assert.True(signedIn.HasPendingSync);

        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        await quiz.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planItemId)).Item.IsCompleted);

        fixture.Restart();
        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        Assert.Equal(NativeDestination.Dashboard,
            (await fixture.Auth.RestoreAsync()).Destination);
        var refreshed = await fixture.Dashboard.RefreshAsync();
        Assert.Equal(NativePhase.Ready, refreshed.Phase);
        Assert.False(refreshed.HasPendingSync);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task AllMasteredAssignedPlan_DoneIsIdempotentWithoutAnActivitySession()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        fixture.SetPlanFocus(NativeSliceFixture.UserA);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var summary = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!);
        Assert.Equal(NativePhase.Summary, summary.Phase);
        Assert.Equal(0, summary.TotalTurns);
        await quiz.DoneAsync();
        await quiz.DoneAsync();
        var rows = await fixture.RowsAsync(NativeSliceFixture.UserA, planItemId);
        Assert.True(rows.Item.IsCompleted);
        Assert.Equal(0, rows.Item.MinutesSpent);
        Assert.Null(rows.Session);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task TwentyFocusWords_RotateInTwoDistinctTenTurnRounds()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 20);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!);
        var seenFirst = new HashSet<string>();
        for (var turn = 0; turn < 10; turn++)
        {
            Assert.Equal(NativePhase.Ready, question.Phase);
            Assert.True(seenFirst.Add(question.Prompt!.WordId));
            await quiz.AnswerAsync(question.Choices[0].Text);
            question = await quiz.NextAsync();
        }
        Assert.Equal(NativePhase.Summary, question.Phase);
        Assert.Equal(10, question.TotalTurns);

        question = await quiz.ContinueRoundAsync();
        var seenSecond = new HashSet<string>();
        for (var turn = 0; turn < 10; turn++)
        {
            Assert.Equal(NativePhase.Ready, question.Phase);
            Assert.True(seenSecond.Add(question.Prompt!.WordId));
            await quiz.AnswerAsync(question.Choices[0].Text);
            question = await quiz.NextAsync();
        }
        Assert.Empty(seenFirst.Intersect(seenSecond));
        Assert.Equal(20, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Summary, question.Phase);
        Assert.Equal(2, question.RoundsCompleted);
    }
}
