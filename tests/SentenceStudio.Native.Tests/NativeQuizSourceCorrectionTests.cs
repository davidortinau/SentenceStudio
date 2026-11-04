using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;
using Xunit;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Source-fidelity corrections of 2026-11-03 (docs/native-source-fidelity-status.md, W4a):
/// AF-Q01 plan-card launch key, AF-Q16 save retry routing, AF-Q20 mnemonic image show/hide.
/// </summary>
public sealed class NativeQuizSourceCorrectionTests
{
    // AF-Q01 ------------------------------------------------------------------------------

    [Fact]
    public async Task PlanCardQuiz_SavesItsRunUnderTheKeyTheWebBuildsForTheSameCard()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var webKey = await WebLaunchKeyAsync(fixture, planId);

        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!;
        Assert.True(launch.DueOnly);
        Assert.Empty(launch.ResourceIds);
        using var quiz = fixture.Quiz;
        Assert.Equal(NativePhase.Ready, (await quiz.OpenAsync(launch)).Phase);

        var session = (await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session!;
        Assert.Equal(webKey, session.LaunchContextKey);
        Assert.Contains("|due=true|", webKey);
        Assert.Contains("|res=|", webKey);
        Assert.Equal(
            SyncIds.ActivitySession(NativeSliceFixture.UserA, "VocabularyReview", webKey),
            session.Id);
        var saved = VocabQuizSavedState.Deserialize(session.StateJson);
        Assert.Equal(webKey, saved.LaunchContextKey);
        Assert.True(saved.Snapshot.DueOnly);
        Assert.Empty(saved.Snapshot.ResourceIds);
    }

    [Fact]
    public async Task UnfinishedRunUnderTheEarlierNativeKey_ResumesAsSaved_AndStartOverUsesTheWebKey()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var webKey = await WebLaunchKeyAsync(fixture, planId);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!;

        // A run the previous native build saved today: same card, DueOnly=false.
        var earlier = launch with { DueOnly = false };
        var earlierKey = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            earlier.PlanItemId, earlier.FocusVocabularyIds, [], false, earlier.SkillId);
        Assert.NotEqual(webKey, earlierKey);
        using (var old = fixture.Quiz)
        {
            var question = await old.OpenAsync(earlier);
            await old.AnswerAsync(question.Choices[0].Text);
            await old.NextAsync();
            Assert.Equal(NativeDestination.TodaysPlan, (await old.BackAsync()).Destination);
        }
        var earlierRow = await SessionAsync(fixture, earlierKey);
        Assert.Equal(ActivitySessionStatus.InProgress, earlierRow!.Status);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        // The new build offers it and resumes it under its own key, unchanged in shape.
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using (var resumed = fixture.Quiz)
        {
            var offered = await resumed.OpenAsync(launch);
            Assert.Equal(NativePhase.ResumeChoice, offered.Phase);
            Assert.Null(offered.ErrorKey);
            var question = await resumed.ResumeAsync();
            Assert.Equal(NativePhase.Ready, question.Phase);
            Assert.Equal(1, question.TotalTurns);
            await resumed.AnswerAsync(question.Choices[0].Text);
            await resumed.NextAsync();
            Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
            await resumed.BackAsync();
        }
        Assert.Null(await SessionAsync(fixture, webKey));
        var continued = VocabQuizSavedState.Deserialize(
            (await SessionAsync(fixture, earlierKey))!.StateJson);
        Assert.Equal(earlierKey, continued.LaunchContextKey);
        Assert.False(continued.Snapshot.DueOnly);
        Assert.Equal(2, continued.Snapshot.TotalTurns);

        // Start over (the learner's choice) ends that run and begins one under the web's key.
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using (var fresh = fixture.Quiz)
        {
            Assert.Equal(NativePhase.ResumeChoice, (await fresh.OpenAsync(launch)).Phase);
            var started = await fresh.StartFreshAsync();
            Assert.Equal(NativePhase.Ready, started.Phase);
            Assert.Equal(0, started.TotalTurns);
        }
        Assert.Equal(ActivitySessionStatus.InProgress,
            (await SessionAsync(fixture, webKey))!.Status);
        var ended = await SessionAsync(fixture, earlierKey);
        Assert.Equal(ActivitySessionStatus.Abandoned, ended!.Status);
        Assert.Equal(2, VocabQuizSavedState.Deserialize(ended.StateJson).Snapshot.TotalTurns);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    // AF-Q16 ------------------------------------------------------------------------------

    [Fact]
    public async Task FailedOverrideSave_RetryRepeatsTheOverride()
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
        Assert.Equal(NativeText.SaveFailed, failed.ErrorKey);

        var retried = await quiz.RetryPendingSaveAsync();
        Assert.Equal(NativePhase.Feedback, retried.Phase);
        Assert.Null(retried.ErrorKey);
        Assert.Equal(NativeText.QuizCorrect, retried.FeedbackKey);
        Assert.Equal(1, retried.CorrectCount);
        Assert.False(retried.RequiresCorrection);
        Assert.Equal(question.TurnId, retried.TurnId);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        await quiz.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var scope = fixture.Scope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyLearningContexts.AsNoTracking().SingleAsync()).WasCorrect);
    }

    [Fact]
    public async Task FailedOverrideSave_RetryThatFailsAgainStaysRetryable()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        await quiz.OpenAsync((await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        await quiz.AnswerAsync("incorrect");
        fixture.FaultSessions.FailNextSnapshot = true;
        await quiz.OverrideAsCorrectAsync();
        fixture.FaultSessions.FailNextSnapshot = true;
        var again = await quiz.RetryPendingSaveAsync();
        Assert.Equal(NativePhase.Error, again.Phase);
        Assert.Equal(NativeText.SaveFailed, again.ErrorKey);
        Assert.Equal(0, again.CorrectCount);

        var saved = await quiz.RetryPendingSaveAsync();
        Assert.Equal(NativePhase.Feedback, saved.Phase);
        Assert.Equal(1, saved.CorrectCount);
    }

    [Fact]
    public async Task FailedCorrectionSave_RetryRepeatsTheCorrection()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        var wrong = await quiz.AnswerAsync("incorrect");
        fixture.FaultSessions.FailNextSnapshot = true;
        var failed = await quiz.SubmitCorrectionAsync(wrong.CorrectAnswerAfterGrading!);
        Assert.Equal(NativePhase.Error, failed.Phase);
        Assert.True(failed.RequiresCorrection);

        var retried = await quiz.RetryPendingSaveAsync();
        Assert.Equal(NativePhase.Feedback, retried.Phase);
        Assert.False(retried.RequiresCorrection);
        Assert.Equal(question.TurnId, retried.TurnId);
        Assert.False(VocabQuizSavedState.Deserialize(
            (await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session!.StateJson)
            .RequiresCorrection);
        await quiz.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task SnapshotFailureAfterAScoredAnswer_RetrySavesTheNextQuestion()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        fixture.FaultSessions.FailNextSnapshot = true;
        var failed = await quiz.NextAsync();
        Assert.Equal(NativePhase.Error, failed.Phase);
        Assert.Equal(NativeText.SaveFailed, failed.ErrorKey);
        Assert.NotNull(failed.Prompt);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        var next = await quiz.RetryPendingSaveAsync();
        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.Null(next.ErrorKey);
        Assert.NotEqual(question.TurnId, next.TurnId);
        Assert.Equal(2, next.CurrentTurn);
        Assert.Equal(1, next.TotalTurns);
        var saved = VocabQuizSavedState.Deserialize(
            (await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session!.StateJson);
        Assert.Equal(next.TurnId, saved.TurnId);
        Assert.Null(saved.PendingAttempt);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        await quiz.AnswerAsync(next.Choices[0].Text);
        await quiz.NextAsync();
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task SnapshotFailureAfterTheLastScoredAnswer_RetryShowsTheSummary()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        await quiz.AnswerAsync(question.Choices[0].Text);
        question = await quiz.NextAsync();
        await quiz.AnswerAsync(question.Choices[0].Text);
        fixture.FaultSessions.FailNextSnapshot = true;
        Assert.Equal(NativePhase.Error, (await quiz.NextAsync()).Phase);

        var summary = await quiz.RetryPendingSaveAsync();
        Assert.Equal(NativePhase.Summary, summary.Phase);
        Assert.Equal(NativeDestination.QuizSummary, summary.Destination);
        Assert.True(VocabQuizSavedState.Deserialize(
            (await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session!.StateJson)
            .SummaryVisible);
        Assert.Equal(NativeDestination.TodaysPlan, (await quiz.DoneAsync()).Destination);
        Assert.True((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planId)).Item.IsCompleted);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    // AF-Q20 ------------------------------------------------------------------------------

    [Fact]
    public async Task MnemonicImage_HideOnCorrectFeedbackStays_AndWrongAnswerOffersShowImage()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.ShowMnemonicImage = true;
        fixture.QuizPreferences.UsePhotoPrompt = false;
        fixture.Media.PhotoAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);

        // Before grading the image must not appear: no toggle, and a stray request is refused.
        Assert.NotNull(question.Prompt!.Photo);
        Assert.False(question.Prompt.CanTogglePhoto);
        Assert.False(question.Prompt.PhotoRequested);
        var early = await quiz.SetPhotoVisibleAsync(true, question.TurnId!);
        Assert.Equal(NativeText.QuizUnavailable, early.ErrorKey);
        Assert.False(early.Prompt!.PhotoRequested);
        Assert.False(early.Prompt.ShowPhoto);

        var correct = await quiz.AnswerAsync(await CorrectAnswerAsync(fixture, question));
        Assert.True(correct.AnswerWasCorrect);
        Assert.True(correct.Prompt!.PhotoRequested);
        Assert.True(correct.Prompt.CanTogglePhoto);
        Assert.True((await quiz.PhotoRenderedAsync(correct.TurnId!)).Prompt!.ShowPhoto);

        var hidden = await quiz.SetPhotoVisibleAsync(false, correct.TurnId!);
        Assert.Equal(NativePhase.Feedback, hidden.Phase);
        Assert.False(hidden.Prompt!.ShowPhoto);
        Assert.False(hidden.Prompt.PhotoRequested);
        Assert.True(hidden.Prompt.CanTogglePhoto);
        Assert.Null(hidden.Prompt.PhotoUri);

        var next = await quiz.NextAsync();
        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.False(next.Prompt!.CanTogglePhoto);
        Assert.False(next.Prompt.PhotoRequested);
        var correctNext = await CorrectAnswerAsync(fixture, next);
        var wrong = await quiz.AnswerAsync(
            next.Choices.First(choice => choice.Text != correctNext).Text);
        Assert.False(wrong.AnswerWasCorrect);
        Assert.False(wrong.Prompt!.PhotoRequested);
        Assert.False(wrong.Prompt.ShowPhoto);
        Assert.True(wrong.Prompt.CanTogglePhoto);

        var requested = await quiz.SetPhotoVisibleAsync(true, wrong.TurnId!);
        Assert.True(requested.Prompt!.PhotoRequested);
        var shown = await quiz.PhotoRenderedAsync(wrong.TurnId!);
        Assert.Equal(NativePhase.Feedback, shown.Phase);
        Assert.True(shown.Prompt!.ShowPhoto);
        Assert.NotNull(shown.Prompt.PhotoUri);
    }

    [Fact]
    public async Task MnemonicImage_WithTheSettingOff_IsNeverOfferedWithoutThePhotoPrompt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.ShowMnemonicImage = false;
        fixture.QuizPreferences.UsePhotoPrompt = false;
        fixture.Media.PhotoAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        var feedback = await quiz.AnswerAsync(await CorrectAnswerAsync(fixture, question));
        Assert.True(feedback.AnswerWasCorrect);
        Assert.False(feedback.Prompt!.CanTogglePhoto);
        Assert.False(feedback.Prompt.PhotoRequested);
        Assert.False(feedback.Prompt.ShowPhoto);
    }

    [Fact]
    public async Task MnemonicImage_WithThePhotoPrompt_ReappearsOnFeedbackAfterTheLearnerHidIt()
    {
        // Source: grading sets the image to shown when the photo prompt is on.
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.QuizPreferences.ShowMnemonicImage = true;
        fixture.QuizPreferences.UsePhotoPrompt = true;
        fixture.Media.PhotoAvailable = true;
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var awaiting = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(planId))!);
        var question = await quiz.PhotoRenderedAsync(awaiting.TurnId!);
        Assert.True(question.Prompt!.ShowPhoto);
        var hidden = await quiz.SetPhotoVisibleAsync(false, question.TurnId!);
        Assert.False(hidden.Prompt!.ShowPhoto);

        var correctText = await CorrectAnswerAsync(fixture, hidden);
        var wrong = await quiz.AnswerAsync(
            hidden.Choices.First(choice => choice.Text != correctText).Text);
        Assert.False(wrong.AnswerWasCorrect);
        Assert.True(wrong.Prompt!.PhotoRequested);
        Assert.True((await quiz.PhotoRenderedAsync(wrong.TurnId!)).Prompt!.ShowPhoto);
    }

    // Helpers -----------------------------------------------------------------------------

    /// <summary>
    /// The web's run key for this plan card: Index.razor LaunchPlanItem passes planItemId,
    /// skillId, FocusVocabularyIds and the card's route parameters (PlanConverter), and
    /// VocabQuiz.razor builds the key from them with its validated resources.
    /// </summary>
    private static async Task<string> WebLaunchKeyAsync(NativeSliceFixture fixture, string planId)
    {
        using var lease = fixture.PlanLease(NativeSliceFixture.UserA);
        var card = (await lease.Service.GetTodayAsync())!.Items.Single(item => item.Id == planId);
        var route = PlanConverter.BuildRouteParameters(
            PlanActivityType.VocabularyReview, card.ResourceId, card.SkillId,
            card.FocusVocabularyIds);
        Assert.DoesNotContain(route.Keys, key =>
            key.Equals("resourceId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("resourceIds", StringComparison.OrdinalIgnoreCase));
        var dueOnly = (bool)route["DueOnly"];
        var focus = FocusVocabularySelection.ParseFocusVocabularyIds(
            string.Join(",", card.FocusVocabularyIds));
        return VocabQuizSessionSnapshot.BuildLaunchContextKey(
            card.Id, focus, Array.Empty<string>(), dueOnly, card.SkillId);
    }

    private static async Task<ActivitySession?> SessionAsync(
        NativeSliceFixture fixture, string launchContextKey)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .ActivitySessions.AsNoTracking()
            .Where(row => row.UserId == NativeSliceFixture.UserA
                && row.ActivityType == "VocabularyReview"
                && row.LaunchContextKey == launchContextKey
                && !row.Id.Contains('@'))
            .SingleOrDefaultAsync();
    }

    private static async Task<string> CorrectAnswerAsync(
        NativeSliceFixture fixture, NativeQuizState state)
    {
        using var scope = fixture.Scope();
        var word = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyWords.AsNoTracking().SingleAsync(row => row.Id == state.Prompt!.WordId);
        return state.Prompt!.IsTargetLanguagePrompt
            ? word.NativeLanguageTerm! : word.TargetLanguageTerm!;
    }
}
