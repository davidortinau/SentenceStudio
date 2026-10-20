using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeSliceBoundaryTests
{
    [Fact]
    public async Task BackFromResumeChoicePreservesTheOriginalSnapshotAndAllowsLaterResume()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!;
        using (var initial = fixture.Quiz)
        {
            Assert.Equal(NativePhase.Ready, (await initial.OpenAsync(launch)).Phase);
            Assert.Equal(NativeDestination.TodaysPlan, (await initial.BackAsync()).Destination);
        }
        var original = await fixture.RowsAsync(NativeSliceFixture.UserA, planId);
        Assert.NotNull(original.Session);

        using (var choice = fixture.Quiz)
        {
            Assert.Equal(NativePhase.ResumeChoice, (await choice.OpenAsync(launch)).Phase);
            var back = await choice.BackAsync();
            Assert.Equal(NativePhase.Ready, back.Phase);
            Assert.Equal(NativeDestination.TodaysPlan, back.Destination);
            Assert.Null(back.ErrorKey);
        }
        var after = await fixture.RowsAsync(NativeSliceFixture.UserA, planId);
        Assert.Equal(original.Session.StateJson, after.Session!.StateJson);
        Assert.Equal(original.Item.MinutesSpent, after.Item.MinutesSpent);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await resumed.OpenAsync(launch)).Phase);
        Assert.Equal(NativePhase.Ready, (await resumed.ResumeAsync()).Phase);
    }

    [Fact]
    public async Task Q25a_ImmediateProcessInterruption_RestoresGradedPendingBeforeNext()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch!);
        var graded = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, graded.Phase);
        var saved = await fixture.RowsAsync(NativeSliceFixture.UserA, planId);
        Assert.Equal(ActivitySessionStatus.InProgress, saved.Session?.Status);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await resumed.OpenAsync(launch!)).Phase);
        var sameTurn = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Feedback, sameTurn.Phase);
        Assert.Equal(question.Prompt!.WordId, sameTurn.Prompt?.WordId);
        Assert.Equal(1, sameTurn.TotalTurns);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));

        Assert.Equal(NativePhase.Ready, (await resumed.NextAsync()).Phase);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planId)).Item.IsCompleted);
    }

    [Fact]
    public async Task Q25a_OverriddenWrongTextSurvivesRestartWithoutImplicitCommit()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using var quiz = fixture.Quiz;
        Assert.Equal("Text", (await quiz.OpenAsync(launch!)).Mode);
        await quiz.AnswerAsync("incorrect");
        Assert.Equal(NativePhase.Feedback,
            (await quiz.OverrideAsCorrectAsync()).Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(launch!)).Phase);
        var pending = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Feedback, pending.Phase);
        Assert.False(pending.RequiresCorrection);
        Assert.Equal(NativeText.QuizCorrect, pending.FeedbackKey);
        Assert.Equal(1, pending.CorrectCount);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        await resumed.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task TenTurnSummaryRestartsAtSummaryWithoutRepeatingQuestions()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 10);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using var quiz = fixture.Quiz;
        var turn = await quiz.OpenAsync(launch!);
        for (var index = 0; index < 10; index++)
        {
            await quiz.AnswerAsync(turn.Choices[0].Text);
            turn = await quiz.NextAsync();
        }
        Assert.Equal(NativePhase.Summary, turn.Phase);
        Assert.Equal(10, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(launch!)).Phase);
        Assert.Equal(NativePhase.Summary, (await resumed.ResumeAsync()).Phase);
        Assert.Equal(10, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(NativeDestination.TodaysPlan, (await resumed.BackAsync()).Destination);
        Assert.True((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planId)).Item.IsCompleted);
        Assert.Equal(10, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task Q25b_StartFreshAfterCommittedBeforeSnapshot_DoesNotReplayOldTurn()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        fixture.FaultSessions.FailNextSnapshot = true;
        Assert.Equal(NativePhase.Error, (await quiz.NextAsync()).Phase);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var fresh = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await fresh.OpenAsync(launch!)).Phase);
        Assert.Equal(NativePhase.Ready, (await fresh.StartFreshAsync()).Phase);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));
        using var scope = fixture.Scope();
        var sessions = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .ActivitySessions.AsNoTracking()
            .Where(s => s.UserId == NativeSliceFixture.UserA)
            .ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, session => session.Status == ActivitySessionStatus.Abandoned);
        Assert.Contains(sessions, session => session.Status == ActivitySessionStatus.InProgress);
    }

    [Fact]
    public async Task Q25b_LastTurnBackPreservesStalePendingSnapshotForSummaryRecovery()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        question = await quiz.NextAsync();
        await quiz.AnswerAsync(question.Choices[0].Text);
        var pending = (await fixture.RowsAsync(
            NativeSliceFixture.UserA, planId)).Session!.StateJson;

        fixture.FaultSessions.FailNextSnapshot = true;
        Assert.Equal(NativePhase.Error, (await quiz.NextAsync()).Phase);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(NativeDestination.TodaysPlan, (await quiz.BackAsync()).Destination);
        Assert.Equal(pending, (await fixture.RowsAsync(
            NativeSliceFixture.UserA, planId)).Session!.StateJson);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var recovered = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await recovered.OpenAsync(launch!)).Phase);
        Assert.Equal(NativePhase.Summary, (await recovered.ResumeAsync()).Phase);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(NativeDestination.TodaysPlan, (await recovered.DoneAsync()).Destination);
        Assert.True((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planId)).Item.IsCompleted);
    }

    [Fact]
    public async Task SnapshotSaveError_DoesNotRevealFeedbackUntilPendingWriteSucceeds()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        fixture.FaultSessions.FailNextSnapshot = true;
        var failed = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Error, failed.Phase);
        Assert.Null(failed.CorrectAnswerAfterGrading);
        Assert.Null(failed.FeedbackKey);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        var retried = await quiz.RetryPendingSaveAsync();
        Assert.Equal(NativePhase.Feedback, retried.Phase);
        Assert.NotNull(retried.CorrectAnswerAfterGrading);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        await quiz.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task FailedOverrideSave_RollsBackInMemoryAndCannotCommitWrongVerdict()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal("Text", question.Mode);
        await quiz.AnswerAsync("incorrect");
        fixture.FaultSessions.FailNextSnapshot = true;
        var failed = await quiz.OverrideAsCorrectAsync();
        Assert.Equal(NativePhase.Error, failed.Phase);
        Assert.Equal(NativeText.QuizIncorrect, failed.FeedbackKey);
        Assert.Equal(0, failed.CorrectCount);
        Assert.True(failed.RequiresCorrection);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Error, (await quiz.NextAsync()).Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        var saved = await quiz.OverrideAsCorrectAsync();
        Assert.Equal(NativePhase.Feedback, saved.Phase);
        Assert.Equal(NativeText.QuizCorrect, saved.FeedbackKey);
        Assert.Equal(1, saved.CorrectCount);
        await quiz.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var scope = fixture.Scope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyLearningContexts.AsNoTracking().SingleAsync()).WasCorrect);
    }

    [Fact]
    public async Task FailedCorrectionSave_KeepsCorrectionRequiredUntilRetry()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal("Text", question.Mode);
        var wrong = await quiz.AnswerAsync("incorrect");
        var expected = wrong.CorrectAnswerAfterGrading!;
        fixture.FaultSessions.FailNextSnapshot = true;
        var failed = await quiz.SubmitCorrectionAsync(expected);
        Assert.Equal(NativePhase.Error, failed.Phase);
        Assert.True(failed.RequiresCorrection);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        var retried = await quiz.SubmitCorrectionAsync(expected);
        Assert.Equal(NativePhase.Feedback, retried.Phase);
        Assert.False(retried.RequiresCorrection);
        await quiz.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task InvalidSnapshotReferencesAndDirection_RequireExplicitStartFresh()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using var quiz = fixture.Quiz;
        await quiz.OpenAsync(launch!);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.ActivitySessions.SingleAsync();
            var snapshot = JsonNode.Parse(row.StateJson)!.AsObject();
            snapshot["userId"] = NativeSliceFixture.UserB;
            snapshot["snapshot"]!["batchPool"]![0]!["wordId"] =
                fixture.WordId(NativeSliceFixture.UserB, 0);
            row.StateJson = snapshot.ToJsonString();
            await db.SaveChangesAsync();
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var rejected = fixture.Quiz;
        var prompt = await rejected.OpenAsync(launch!);
        Assert.Equal(NativePhase.ResumeChoice, prompt.Phase);
        Assert.Equal(NativeText.ResumeUnavailable, prompt.ErrorKey);
        Assert.Equal(NativePhase.Ready, (await rejected.StartFreshAsync()).Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        using var changedDirection = fixture.Quiz;
        var directionMismatch = await changedDirection.OpenAsync(launch!);
        Assert.Equal(NativePhase.ResumeChoice, directionMismatch.Phase);
        Assert.Equal(NativeText.ResumeUnavailable, directionMismatch.ErrorKey);
    }

    [Fact]
    public async Task MalformedNestedSnapshotLists_AreRejectedWithoutReadingAnotherOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using var quiz = fixture.Quiz;
        await quiz.OpenAsync(launch!);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var session = await db.ActivitySessions.SingleAsync();
            var json = JsonNode.Parse(session.StateJson)!.AsObject();
            json["snapshot"]!["batchPool"] = null;
            session.StateJson = json.ToJsonString();
            await db.SaveChangesAsync();
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var afterRestart = fixture.Quiz;
        var rejected = await afterRestart.OpenAsync(launch!);
        Assert.Equal(NativePhase.ResumeChoice, rejected.Phase);
        Assert.Equal(NativeText.ResumeUnavailable, rejected.ErrorKey);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task WrongTextAnswer_RequiresCorrectionOrExplicitOverrideBeforeSingleAttempt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal("Text", question.Mode);
        Assert.True(question.CanSubmit);
        var wrong = await quiz.AnswerAsync("wrong response");
        Assert.True(wrong.RequiresCorrection);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Feedback, (await quiz.NextAsync()).Phase);
        Assert.Equal(NativeText.CorrectionRequired,
            (await quiz.SubmitCorrectionAsync("not the answer")).ErrorKey);
        var correction = await quiz.SubmitCorrectionAsync(wrong.CorrectAnswerAfterGrading!);
        Assert.False(correction.RequiresCorrection);
        await quiz.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var scope = fixture.Scope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyLearningContexts.AsNoTracking().SingleAsync()).WasCorrect);
    }

    [Fact]
    public async Task OverrideWrongText_ReusesSharedQuizItemRuleAndCreditsOnce()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal("Text", question.Mode);
        await quiz.AnswerAsync("wrong response");
        var overrideState = await quiz.OverrideAsCorrectAsync();
        Assert.False(overrideState.RequiresCorrection);
        Assert.Equal(1, overrideState.CorrectCount);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        await quiz.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var scope = fixture.Scope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyLearningContexts.AsNoTracking().SingleAsync()).WasCorrect);
    }

    [Theory]
    [InlineData("TargetToNative")]
    [InlineData("NativeToTarget")]
    [InlineData("Mixed")]
    public async Task PhotoOnlyWithoutRenderedImageFallsBackToWrittenPrompt(
        string direction)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = direction;
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = false;
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = false;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.NotNull(state.Prompt);
        Assert.True(state.Prompt.ShowWrittenPrompt);
        Assert.False(state.Prompt.ShowPhoto);
        Assert.True(state.CanSubmit);
        Assert.Equal(NativePhase.Ready, (await quiz.AnswerAsync("guess")).Phase);
        Assert.Equal(NativeDestination.TodaysPlan,
            (await quiz.BackAsync()).Destination);
        Assert.NotNull((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session);
        Assert.Equal(NativeQuizOverlay.None, quiz.OpenPhoto().Overlay);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task PhotoOnlyNativePrompt_TextOverrideUsesWrittenFallbackUntilImageRenders()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.UserProfiles.SingleAsync(profile =>
                profile.Id == NativeSliceFixture.UserA)).VocabQuizShowTextWithPhoto = true;
            await db.SaveChangesAsync();
        }
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = false;
        fixture.QuizPreferences.UsePhotoPrompt = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.NotNull(state.Prompt);
        Assert.True(state.Prompt.ShowWrittenPrompt);
        Assert.True(state.CanSubmit);
        Assert.NotNull((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session);
    }

    [Theory]
    [InlineData(false, NativePhase.Ready)]
    [InlineData(true, NativePhase.Ready)]
    public async Task NativePhotoPrompt_WithoutLoadedAsset_KeepsWrittenFallback(
        bool showTextWithPhoto, NativePhase expectedPhase)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.UserProfiles.SingleAsync(profile =>
                profile.Id == NativeSliceFixture.UserA)).VocabQuizShowTextWithPhoto =
                    showTextWithPhoto;
            await db.SaveChangesAsync();
        }
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = true;
        fixture.QuizPreferences.UseAudioPrompt = false;
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = false;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(expectedPhase, state.Phase);
        Assert.Equal("Text", state.Mode);
        Assert.StartsWith("native-", state.Prompt!.PromptText);
        Assert.True(state.CanSubmit);
        Assert.False(state.Prompt.ShowPhoto);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativePromptAudioChoicesUseTargetTextFallbackUntilAudioChoicesExist(
        bool useTextPrompt, bool photoEnabled)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = useTextPrompt;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.UsePhotoPrompt = photoEnabled;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var fallback = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Ready, fallback.Phase);
        Assert.NotNull(fallback.Prompt);
        Assert.True(fallback.Prompt.ShowWrittenPrompt);
        Assert.True(fallback.CanSubmit);
        Assert.All(fallback.Choices,
            option => Assert.StartsWith("target-", option.Text));
        Assert.Equal(NativePhase.Ready,
            (await quiz.AnswerAsync("written option")).Phase);
        Assert.Equal(NativeDestination.TodaysPlan, (await quiz.BackAsync()).Destination);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.NotNull((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session);
    }

    [Fact]
    public async Task NativePromptTypedResponse_AudioOnlyUnavailableShowsWrittenCue()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        fixture.QuizPreferences.DisplayDirection = "NativeToTarget";
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.True(state.Prompt!.ShowWrittenPrompt);
        Assert.True(state.CanSubmit);
        Assert.NotNull((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session);
    }

    [Theory]
    [InlineData("TargetToNative")]
    [InlineData("NativeToTarget")]
    [InlineData("Mixed")]
    public async Task AudioOnlyUnavailable_FallsBackToTextInEveryDirection(
        string direction)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        fixture.QuizPreferences.DisplayDirection = direction;
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.UsePhotoPrompt = false;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.True(state.Prompt!.ShowWrittenPrompt);
        Assert.True(state.CanSubmit);
        Assert.NotNull((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task AudioOnlyQuestion_DisablesAnswerUntilConfirmedPlayback()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.UsePhotoPrompt = false;
        fixture.QuizPreferences.AutoPlayVocabAudio = false;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var unheard = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.False(unheard.CanSubmit);
        var heard = await quiz.PlayPromptAsync();
        Assert.True(heard.CanSubmit);
        Assert.Equal("Korean", Assert.Single(fixture.Media.Played).Language);
        Assert.Equal(NativePhase.Feedback,
            (await quiz.AnswerAsync(heard.Choices[0].Text)).Phase);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.Media.PlaySucceeds = false;
        using var again = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await again.OpenAsync(
                (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!)).Phase);
        Assert.Equal(NativePhase.Feedback, (await again.ResumeAsync()).Phase);
    }

    [Theory]
    [InlineData("TargetToNative", "Korean", "target-")]
    [InlineData("NativeToTarget", "English", "native-")]
    public async Task AutoplayUsesThePromptSideOncePerTurnAndNeverTheAnswer(
        string direction, string expectedLanguage, string expectedPrefix)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.DisplayDirection = direction;
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.AutoPlayVocabAudio = true;
        fixture.Media.AudioAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.Equal(direction == "TargetToNative", question.CanSubmit);
        var played = Assert.Single(fixture.Media.Played);
        Assert.Equal(expectedLanguage, played.Language);
        Assert.StartsWith(expectedPrefix, played.Text);
        if (direction == "NativeToTarget")
        {
            var audioChoice = await quiz.PlayAnswerChoiceAsync(0, question.TurnId!);
            Assert.True(audioChoice.CanSubmit);
            Assert.Equal("Korean", fixture.Media.Played[^1].Language);
        }
        var heardCount = fixture.Media.Played.Count;
        quiz.OpenDetails();
        quiz.DismissOverlay();
        Assert.Equal(heardCount, fixture.Media.Played.Count);
        await quiz.AnswerAsync(question.Choices[0].Text);
        var next = await quiz.NextAsync();
        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.Equal(heardCount + 1, fixture.Media.Played.Count);
        Assert.Equal(expectedLanguage, fixture.Media.Played[^1].Language);
    }

    [Fact]
    public async Task CorrectFeedbackExposesMnemonicCandidatesOnlyUntilTheNextTurn()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var word in await db.VocabularyWords.ToListAsync())
                word.MnemonicText = "remember this meaning";
            await db.SaveChangesAsync();
        }
        fixture.QuizPreferences.ShowMnemonicImage = true;
        fixture.QuizPreferences.UsePhotoPrompt = false;
        fixture.Media.PhotoAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Null(question.FeedbackMnemonicText);
        Assert.Null(question.FeedbackPhotoCandidateUri);
        Assert.False(question.Prompt!.ShowPhoto);
        var correct = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal("remember this meaning", correct.FeedbackMnemonicText);
        Assert.StartsWith("opaque-photo-", correct.FeedbackPhotoCandidateUri);
        Assert.False(correct.Prompt!.ShowPhoto);
        Assert.True(correct.Prompt.PhotoRequested);
        var renderedFeedback = await quiz.PhotoRenderedAsync(correct.TurnId!);
        Assert.True(renderedFeedback.Prompt!.ShowPhoto);

        var next = await quiz.NextAsync();
        Assert.Null(next.FeedbackMnemonicText);
        Assert.Null(next.FeedbackPhotoCandidateUri);
        var wrong = await quiz.AnswerAsync(next.Choices[^1].Text);
        Assert.False(wrong.AnswerWasCorrect);
        Assert.Null(wrong.FeedbackMnemonicText);
        Assert.Null(wrong.FeedbackPhotoCandidateUri);
    }

    [Fact]
    public async Task LearningDetailsExposeOnlyOwnedProgressAndDismissWithoutLeakingTerms()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        await fixture.SeedAsync(NativeSliceFixture.UserB, known: true);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        var overlay = quiz.OpenDetails();
        Assert.Equal(NativeQuizOverlay.LearningDetails, overlay.Overlay);
        var details = overlay.Details!;
        Assert.NotNull(details);
        Assert.Equal(8, details.TotalAttempts);
        Assert.Equal(.9f, details.MasteryScore);
        Assert.Equal("Text", details.Mode);
        Assert.False(details.DueOnly);
        Assert.DoesNotContain("target-", details.ToString());
        Assert.DoesNotContain("native-", details.ToString());
        var dismissed = quiz.DismissOverlay();
        Assert.Equal(NativeQuizOverlay.None, dismissed.Overlay);
        Assert.Null(dismissed.Details);
        Assert.Equal(question.Prompt!.WordId, dismissed.Prompt!.WordId);
    }

    [Fact]
    public async Task FuzzyAcceptedTextShowsCompleteFormAndRestoresItWithoutRegrading()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        fixture.SetPlanFocus(NativeSliceFixture.UserA,
            fixture.WordId(NativeSliceFixture.UserA, 0));
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.VocabularyWords.SingleAsync(word =>
                word.Id == fixture.WordId(NativeSliceFixture.UserA, 0)))
                .NativeLanguageTerm = "remaining/leftover";
            await db.SaveChangesAsync();
        }
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!;
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch);
            Assert.Equal("Text", question.Mode);
            Assert.Null(question.CompleteForm);
            var feedback = await quiz.AnswerAsync("leftover");
            Assert.Equal(NativePhase.Feedback, feedback.Phase);
            Assert.True(feedback.AnswerWasCorrect);
            Assert.Equal("remaining/leftover", feedback.CompleteForm);
            Assert.Equal(NativeDestination.TodaysPlan, (await quiz.BackAsync()).Destination);
        }
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await resumed.OpenAsync(launch)).Phase);
        Assert.Equal("remaining/leftover", (await resumed.ResumeAsync()).CompleteForm);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task UnavailableListeningAndPhotoPortsFallBackWithoutPretendingMediaPlayed()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UseTextPrompt = false;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.QuizPreferences.AutoPlayVocabAudio = false;
        fixture.Media.AudioAvailabilityOffline = true;
        fixture.Media.PhotoAvailabilityOffline = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var fallback = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Ready, fallback.Phase);
        Assert.True(fallback.Prompt!.ShowWrittenPrompt);
        Assert.False(fallback.Prompt.CanPlayPromptAudio);
        Assert.False(fallback.Prompt.ShowPhoto);
        Assert.True(fallback.CanSubmit);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.NotNull((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session);
        fixture.Media.AudioAvailabilityOffline = false;
        fixture.Media.PhotoAvailabilityOffline = false;
        fixture.Media.AudioAvailable = true;
        fixture.QuizPreferences.UsePhotoPrompt = false;
        using var later = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await later.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!)).Phase);
        var unheard = await later.ResumeAsync();
        Assert.Equal(NativePhase.Ready, unheard.Phase);
        Assert.False(unheard.CanSubmit);
        fixture.Media.PlaybackOffline = true;
        var playbackFailure = await later.PlayPromptAsync();
        Assert.Equal(NativePhase.Ready, playbackFailure.Phase);
        Assert.True(playbackFailure.Prompt!.ShowWrittenPrompt);
        Assert.False(playbackFailure.Prompt.CanPlayPromptAudio);
        Assert.True(playbackFailure.CanSubmit);
        Assert.Equal(NativePhase.Ready,
            (await later.AnswerAsync("guessed without hearing")).Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Theory]
    [InlineData("TargetToNative", false, false)]
    [InlineData("NativeToTarget", true, true)]
    public async Task ExplicitWrittenCue_AllowsOnlySourceDefinedMediaFallback(
        string direction, bool nativePrompt, bool showTextWithPhoto)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.UserProfiles.SingleAsync(profile =>
                profile.Id == NativeSliceFixture.UserA)).VocabQuizShowTextWithPhoto =
                    showTextWithPhoto;
            await db.SaveChangesAsync();
        }
        fixture.QuizPreferences.DisplayDirection = direction;
        fixture.QuizPreferences.UseTextPrompt = true;
        fixture.QuizPreferences.UseAudioPrompt = true;
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.AudioAvailable = false;
        fixture.Media.PhotoAvailable = false;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal("Text", state.Mode);
        Assert.True(state.Prompt!.ShowWrittenPrompt);
        Assert.False(state.Prompt.ShowPhoto);
        Assert.True(state.CanSubmit);
        Assert.Equal(nativePrompt, !state.Prompt.IsTargetLanguagePrompt);
    }

    [Fact]
    public async Task LatePhotoCallback_DuringFinalSnapshotCannotIndexPastLastQuestion()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        question = await quiz.NextAsync();
        var finalFeedback = await quiz.AnswerAsync(question.Choices[0].Text);
        var snapshotEntered = fixture.FaultSessions.HoldNextSnapshot();
        var finalNext = quiz.NextAsync();
        await snapshotEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        var photoFailed = quiz.PhotoFailedAsync(finalFeedback.TurnId!);
        Assert.False(photoFailed.IsCompleted);

        fixture.FaultSessions.ReleaseHeldSnapshot();
        Assert.Equal(NativePhase.Summary, (await finalNext).Phase);
        Assert.Equal(NativePhase.Summary, (await photoFailed).Phase);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task UnavailablePhotoAction_DoesNotOpenAnOverlayOrScoreTheQuiz()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = false;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.False(question.Prompt!.ShowPhoto);
        Assert.Equal(NativeQuizOverlay.None, quiz.OpenPhoto().Overlay);
        Assert.Equal(NativeText.QuizUnavailable, quiz.State.ErrorKey);
        Assert.Equal(NativeDestination.TodaysPlan, (await quiz.BackAsync()).Destination);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planId)).Item.IsCompleted);
    }

    [Fact]
    public async Task SentenceShortcut_ShowsTargetLanguageAndFailsWithoutARealGrader()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        await quiz.OpenAsync((await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        var sentence = quiz.OpenSentenceShortcut();
        Assert.NotNull(sentence.Sentence);
        Assert.Contains("target-", sentence.Sentence.TargetLanguageTerm);
        Assert.Equal("Korean", sentence.Sentence.TargetLanguage);
        Assert.Equal(NativePhase.Ready, quiz.CancelSentenceShortcut().Phase);
        quiz.OpenSentenceShortcut();
        var unavailable = await quiz.SubmitSentenceAsync("synthetic target sentence");
        Assert.Equal(NativePhase.Error, unavailable.Phase);
        Assert.Equal(NativeText.SaveFailed, unavailable.ErrorKey);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task NoLocalProfile_BlocksDashboardUntilConfirmedSync()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Sync.Outcome = NativeSyncOutcome.Unconfirmed;
        var auth = fixture.Auth;
        var signedIn = await auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativePhase.PendingSync, signedIn.Phase);
        Assert.Equal(NativeDestination.SignIn, signedIn.Destination);
        var scopedRead = await fixture.Dashboard.LoadAsync();
        Assert.Equal(NativePhase.Blocked, scopedRead.Phase);
        Assert.True(scopedRead.Cards.IsDefaultOrEmpty);
        Assert.Equal(NativePhase.PendingSync, auth.State.Phase);
        Assert.Equal(NativeDestination.SignIn, auth.State.Destination);
        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        var onboard = await auth.RetryReadinessAsync();
        Assert.Equal(NativeDestination.Onboarding, onboard.Destination);
        Assert.False(onboard.HasPendingSync);
        Assert.Equal(NativePhase.Empty, (await fixture.Dashboard.LoadAsync()).Phase);
    }

    [Fact]
    public async Task CompletedCardWithInterruptedSession_ReconcilesOnDashboardRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        for (var i = 0; i < 2; i++)
        {
            await quiz.AnswerAsync(question.Choices[0].Text);
            question = await quiz.NextAsync();
        }
        Assert.Equal(NativePhase.Summary, question.Phase);
        fixture.FaultSessions.FailNextComplete = true;
        Assert.Equal(NativePhase.Error, (await quiz.DoneAsync()).Phase);
        var halfCommitted = await fixture.RowsAsync(NativeSliceFixture.UserA, planId);
        Assert.True(halfCommitted.Item.IsCompleted);
        Assert.Equal(ActivitySessionStatus.InProgress, halfCommitted.Session?.Status);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = await fixture.Dashboard.LoadAsync();
        Assert.True(Assert.Single(dashboard.Cards).IsCompleted);
        Assert.Equal(ActivitySessionStatus.Completed,
            (await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session?.Status);
        Assert.NotNull(await fixture.Dashboard.SelectAssignedQuizAsync(planId));
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ConfirmedSyncThenLocalPlanWrite_SetsPendingAgainUntilConfirmedRetry()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        var createdDuringRefresh = await dashboard.RefreshAsync();
        Assert.Equal(NativePhase.Ready, createdDuringRefresh.Phase);
        Assert.True(createdDuringRefresh.HasPendingSync);

        fixture.Sync.Outcome = NativeSyncOutcome.Offline;
        var local = await dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, local.Phase);
        Assert.True(local.HasPendingSync);
        Assert.True((await dashboard.LoadAsync()).HasPendingSync);
        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
    }

    [Fact]
    public async Task WriteDuringConfirmedSync_RemainsPendingUntilAnotherVerifiedSync()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        Assert.True((await dashboard.RefreshAsync()).HasPendingSync);
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
        fixture.Sync.DuringSynchronization = owner => fixture.MarkPendingForTest(owner);
        Assert.True((await dashboard.RefreshAsync()).HasPendingSync);
        fixture.Sync.DuringSynchronization = null;
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
    }

    [Fact]
    public async Task QuizCompletionAfterConfirmedDashboardSync_MarksNextLoadPending()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await dashboard.SelectAssignedQuizAsync(planId))!);
        for (var i = 0; i < 2; i++)
        {
            await quiz.AnswerAsync(question.Choices[0].Text);
            question = await quiz.NextAsync();
        }
        Assert.Equal(NativeDestination.TodaysPlan, (await quiz.DoneAsync()).Destination);
        Assert.True((await dashboard.LoadAsync()).HasPendingSync);
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
    }

    [Fact]
    public async Task LocalOnlySnapshotAndQ25aPendingResult_AreNotClaimedAsSyncedWrites()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.False(question.HasPendingSync);
        var graded = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, graded.Phase);
        Assert.False(graded.HasPendingSync);
        Assert.False((await dashboard.LoadAsync()).HasPendingSync);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        var committed = await quiz.NextAsync();
        Assert.True(committed.HasPendingSync);
        Assert.True((await dashboard.LoadAsync()).HasPendingSync);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task OwnerChangeDuringStatePublication_RedactsReadPlanOnReturn()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        dashboard.StateChanged += state =>
        {
            if (state.Phase == NativePhase.Ready)
                fixture.Activate(NativeSliceFixture.UserB);
        };

        var result = await dashboard.LoadAsync();
        Assert.Equal(NativePhase.Blocked, result.Phase);
        Assert.True(result.Cards.IsDefaultOrEmpty);
        Assert.True(dashboard.State.Cards.IsDefaultOrEmpty);
    }

    [Fact]
    public async Task ExpiredOfflineToken_DoesNotBecomeOptimisticAuthenticatedPrincipal()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password");
        await fixture.SecureStorage.SetAsync(
            AuthTokenStore.ExpiresKey, DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        fixture.SetAuthOffline(true);
        fixture.Restart();

        var restored = await fixture.Auth.RestoreAsync();
        Assert.Equal(NativePhase.PendingSync, restored.Phase);
        Assert.Equal(NativeDestination.SignIn, restored.Destination);
        Assert.Null(restored.UserId);
        Assert.Equal(NativePhase.Blocked, (await fixture.Dashboard.LoadAsync()).Phase);
        Assert.NotNull(await fixture.SecureStorage.GetAsync(AuthTokenStore.RefreshKey));
    }

    [Fact]
    public async Task ServerFailureAndOfflineSignIn_DoNotMasqueradeAsAuthenticated()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.SetAuthStatus(HttpStatusCode.ServiceUnavailable);
        var server = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativePhase.Error, server.Phase);
        Assert.Equal(NativeText.UnexpectedError, server.ErrorKey);
        Assert.Equal(NativePhase.Blocked, (await fixture.Dashboard.LoadAsync()).Phase);

        fixture.SetAuthStatus(null);
        fixture.SetAuthOffline(true);
        var offline = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativeText.ConnectionError, offline.ErrorKey);
        Assert.Equal(NativeDestination.SignIn, offline.Destination);
        Assert.Equal(NativePhase.Blocked, (await fixture.Dashboard.LoadAsync()).Phase);
    }

    [Fact]
    public async Task AdHocIdentifierOrForeignFocus_CannotCompleteAssignedCard()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var owned = (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!;
        using var quiz = fixture.Quiz;
        Assert.Equal(NativePhase.Blocked, (await quiz.OpenAsync(owned with {
            PlanItemId = $"adhoc-{planId}" })).Phase);
        Assert.Equal(NativePhase.Blocked, (await quiz.OpenAsync(owned with {
            FocusVocabularyIds = [fixture.WordId(NativeSliceFixture.UserB, 0)] })).Phase);
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planId)).Item.IsCompleted);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task FixedOwnerPlanLease_NeverRedirectsSamePlanIdToSwitchedAccount()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var planA = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var planB = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserB);
        Assert.Equal(planA, planB);

        fixture.Activate(NativeSliceFixture.UserA);
        using var lease = fixture.PlanLease(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserB);
        var result = await lease.Service.MarkCompleteAsync(
            new DateOnly(2026, 9, 28), planA, 0);
        Assert.True(result!.IsCompleted);
        Assert.True((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planA)).Item.IsCompleted);
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserB, planB)).Item.IsCompleted);
        Assert.Throws<ArgumentException>(() => fixture.PlanLease(string.Empty));
    }

    [Fact]
    public async Task AccountSwitch_RedactsOldDashboardAndQuizStateWithoutLeakingPrompts()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var dashboardA = fixture.Dashboard;
        Assert.Equal(NativePhase.Ready, (await dashboardA.LoadAsync()).Phase);
        using var quizA = fixture.Quiz;
        Assert.Equal(NativePhase.Ready, (await quizA.OpenAsync(
            (await dashboardA.SelectAssignedQuizAsync(planId))!)).Phase);

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativePhase.Blocked, dashboardA.State.Phase);
        Assert.True(dashboardA.State.Cards.IsDefaultOrEmpty);
        Assert.Equal(NativePhase.Blocked, quizA.State.Phase);
        Assert.Null(quizA.State.Prompt);
        Assert.Equal(NativePhase.Blocked, quizA.OpenDetails().Phase);
        Assert.Null(quizA.State.Prompt);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task SwitchingIntoOtherOwnersResumableQuiz_NeverCarriesPreviousOwnersTime()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var planB = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserB);
        using (var firstB = fixture.Quiz)
        {
            await firstB.OpenAsync((await fixture.Dashboard.SelectAssignedQuizAsync(planB))!);
            await firstB.BackAsync();
        }

        var planA = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var reused = fixture.Quiz;
        Assert.Equal(NativePhase.Ready,
            (await reused.OpenAsync(
                (await fixture.Dashboard.SelectAssignedQuizAsync(planA))!)).Phase);
        await Task.Delay(700);
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativePhase.ResumeChoice,
            (await reused.OpenAsync(
                (await fixture.Dashboard.SelectAssignedQuizAsync(planB))!)).Phase);
        var restoredB = await reused.ResumeAsync();
        Assert.Equal(NativePhase.Ready, restoredB.Phase);
        Assert.True(restoredB.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.All(restoredB.Choices,
            choice => Assert.Contains(NativeSliceFixture.UserB, choice.Text));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task RegenerationWithCompletedPartialAndUntouchedCards_PreservesDisplacedHistory()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.VocabularyReview), null, null, 10, 1),
            (nameof(PlanActivityType.Reading), fixture.ResourceId(NativeSliceFixture.UserA), null, 12, 2),
            (nameof(PlanActivityType.Writing), null, null, 8, 3));

        var planA = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var planB = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
        {
            var plan = (await lease.Service.GetTodayAsync())!;
            Assert.Equal(3, plan.Items.Count);
            await lease.Service.MarkCompleteAsync(plan.GeneratedForDate, planA, 4);
            await lease.Service.UpdateProgressAsync(plan.GeneratedForDate,
                plan.Items.Single(item => item.ActivityType == nameof(PlanActivityType.Reading)).Id, 2);
        }

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.UserProfileId == NativeSliceFixture.UserA)
            .OrderBy(row => row.PlanItemId).ToListAsync();
        Assert.Equal(3, before.Count);
        Assert.Single(before, row => row.IsCompleted);
        Assert.Single(before, row => row.MinutesSpent == 2);
        Assert.Single(before, row => !row.IsCompleted && row.MinutesSpent == 0);

        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.VocabularyReview), null, null, 10, 1),
            (nameof(PlanActivityType.Translation), null, null, 10, 2));
        var regenerated = await fixture.Dashboard.RegenerateAsync();
        Assert.Equal(NativePhase.Ready, regenerated.Phase);
        Assert.Equal(2, regenerated.Cards.Length);
        Assert.True(regenerated.Cards.Single(card =>
            card.Activity == PlanActivityType.VocabularyReview).IsCompleted);
        Assert.Equal(0, regenerated.Cards.Single(card =>
            card.Activity == PlanActivityType.Translation).MinutesSpent);
        var after = await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.UserProfileId == NativeSliceFixture.UserA)
            .OrderBy(row => row.PlanItemId).ToListAsync();
        Assert.Equal(4, after.Count);
        foreach (var old in before)
        {
            var persisted = Assert.Single(after, row => row.Id == old.Id);
            Assert.Equal(old.MinutesSpent, persisted.MinutesSpent);
            Assert.Equal(old.IsCompleted, persisted.IsCompleted);
            Assert.Equal(old.ActivityType != nameof(PlanActivityType.VocabularyReview),
                persisted.IsArchived);
        }
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserB, planB)).Item.IsCompleted);
    }

    [Fact]
    public async Task ResourceAssignedQuiz_UsesOnlyWordsMappedToThatResource()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(new LearningResource
            {
                Id = "other-resource",
                UserProfileId = NativeSliceFixture.UserA,
                Title = "Other owned resource",
                Language = "Korean",
                MediaType = "Vocabulary List"
            });
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = "other-word",
                NativeLanguageTerm = "other native",
                TargetLanguageTerm = "other target",
                Language = "Korean"
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = "other-mapping",
                ResourceId = "other-resource",
                VocabularyWordId = "other-word"
            });
            await db.SaveChangesAsync();
        }

        fixture.SetPlanFocus(NativeSliceFixture.UserA);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.VocabularyReview),
                fixture.ResourceId(NativeSliceFixture.UserA), null, 10, 1));
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.NotEqual("other-word", question.Prompt!.WordId);
        Assert.DoesNotContain(question.Choices,
            option => option.Text is "other target" or "other native");
    }

    [Fact]
    public async Task SingleReachableWord_CannotBeCreditedByOneObviousMcChoice()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var blocked = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        Assert.Equal(NativePhase.Blocked, blocked.Phase);
        Assert.False(blocked.CanSubmit);
        Assert.Null(blocked.Prompt);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Null((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session);
    }
}
