#if DEBUG
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Tests;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativePreviewRestartTests
{
    [Fact]
    public async Task PreviewDueDatesUseThePlanClockRatherThanWallClock()
    {
        using var fixture = new NativeSliceFixture(
            productionAdapters: true, realPlanGenerator: true);
        await SelectFixture("mc").SeedAsync(fixture.Provider);

        using var scope = fixture.Scope();
        var planNow = scope.ServiceProvider.GetRequiredService<IPlanDateContext>().UtcNow;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var due = await db.VocabularyProgresses.AsNoTracking()
            .Where(row => row.UserId == NativeSliceFixture.UserA)
            .Select(row => row.NextReviewDate)
            .FirstAsync();
        Assert.Equal(planNow.AddDays(-3), due);
        Assert.Equal(NativePhase.Ready, (await fixture.Dashboard.LoadAsync()).Phase);
    }

    [Fact]
    public async Task McFixture_RestoresPersistedSummaryAndCompletedCardAcrossColdStarts()
    {
        using var fixture = new NativeSliceFixture(
            productionAdapters: true, realPlanGenerator: true);
        var preview = SelectFixture("mc");
        await preview.SeedAsync(fixture.Provider);

        var dashboard = await fixture.Dashboard.LoadAsync();
        Assert.Equal(NativePhase.Ready, dashboard.Phase);
        var card = preview.SelectAssignedQuizCard(dashboard)!;
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(launch);
        for (var turn = 0; turn < 10; turn++)
        {
            Assert.Equal(NativePhase.Ready, state.Phase);
            Assert.Equal("MultipleChoice", state.Mode);
            var word = await WordAsync(fixture, state.Prompt!.WordId);
            var answer = state.Prompt.IsTargetLanguagePrompt
                ? word.NativeLanguageTerm! : word.TargetLanguageTerm!;
            Assert.Contains(state.Choices, choice => choice.Text == answer);
            Assert.Equal(NativePhase.Feedback,
                (await quiz.AnswerAsync(answer)).Phase);
            state = await quiz.NextAsync();
        }
        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(10, state.Results.Length);
        var summary = state.Results.ToArray();
        Assert.All(summary, result => Assert.StartsWith(
            $"ios-native-preview-word-{NativeSliceFixture.UserA}-", result.WordId));
        Assert.Equal(10, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.False((await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id))
            .Item.IsCompleted);

        fixture.Restart();
        await preview.SeedAsync(fixture.Provider);
        dashboard = await fixture.Dashboard.LoadAsync();
        var restoredCard = preview.SelectAssignedQuizCard(dashboard)!;
        Assert.Equal(card.Id, restoredCard.Id);
        using var resumed = fixture.Quiz;
        var restoredLaunch = (await fixture.Dashboard.SelectAssignedQuizAsync(
            restoredCard.Id))!;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(restoredLaunch)).Phase);
        var restoredSummary = await resumed.ResumeAsync();
        Assert.True(preview.ShouldShowRestoredSummary(restoredSummary));
        Assert.Equal(NativeDestination.QuizSummary, restoredSummary.Destination);
        Assert.Equal(summary, restoredSummary.Results.ToArray());
        Assert.Equal(10, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        Assert.Equal(NativeDestination.TodaysPlan,
            (await resumed.DoneAsync()).Destination);
        var completed = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
        Assert.True(completed.Item.IsCompleted);
        Assert.Equal(ActivitySessionStatus.Completed, completed.Session?.Status);
        var completedAt = completed.Item.CompletedAt;

        fixture.Restart();
        await preview.SeedAsync(fixture.Provider);
        dashboard = await fixture.Dashboard.LoadAsync();
        Assert.Null(preview.SelectAssignedQuizCard(dashboard));
        Assert.True(Assert.Single(dashboard.Cards, item =>
            item.Id == card.Id).IsCompleted);
        Assert.Equal(completedAt,
            (await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id)).Item.CompletedAt);
        Assert.Equal(10, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task NativeBackDuringFeedback_SuspendsTheAssignedQuizForColdResume()
    {
        using var fixture = new NativeSliceFixture(
            productionAdapters: true, realPlanGenerator: true);
        var preview = SelectFixture("mc");
        await preview.SeedAsync(fixture.Provider);
        var card = preview.SelectAssignedQuizCard(await fixture.Dashboard.LoadAsync())!;
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
        string wordId;

        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch);
            wordId = question.Prompt!.WordId;
            var word = await WordAsync(fixture, wordId);
            var answer = question.Prompt.IsTargetLanguagePrompt
                ? word.NativeLanguageTerm! : word.TargetLanguageTerm!;
            Assert.Equal(NativePhase.Feedback, (await quiz.AnswerAsync(answer)).Phase);

            var back = await quiz.BackAsync();
            Assert.Equal(NativeDestination.TodaysPlan, back.Destination);
            Assert.Equal(NativePhase.Ready, back.Phase);
            Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
            var rows = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
            Assert.False(rows.Item.IsCompleted);
            Assert.Equal(ActivitySessionStatus.InProgress, rows.Session?.Status);
        }

        fixture.Restart();
        await preview.SeedAsync(fixture.Provider);
        var restoredCard = preview.SelectAssignedQuizCard(
            await fixture.Dashboard.LoadAsync())!;
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync((await fixture.Dashboard.SelectAssignedQuizAsync(
                restoredCard.Id))!)).Phase);
        var feedback = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        Assert.Equal(wordId, feedback.Prompt?.WordId);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ReadyBackThenResumedSummaryBack_PreservesOneCompletionAcrossColdStarts()
    {
        using var fixture = new NativeSliceFixture(
            productionAdapters: true, realPlanGenerator: true);
        var preview = SelectFixture("mc");
        await preview.SeedAsync(fixture.Provider);
        var card = preview.SelectAssignedQuizCard(await fixture.Dashboard.LoadAsync())!;
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;

        using (var quiz = fixture.Quiz)
        {
            Assert.Equal(NativePhase.Ready, (await quiz.OpenAsync(launch)).Phase);
            var back = await quiz.BackAsync();
            Assert.Equal(NativeDestination.TodaysPlan, back.Destination);
            Assert.Equal(NativePhase.Ready, back.Phase);
            Assert.True(back.HasPendingSync);
            Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
            var rows = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
            Assert.False(rows.Item.IsCompleted);
            Assert.Equal(ActivitySessionStatus.InProgress, rows.Session?.Status);
        }

        fixture.Restart();
        await preview.SeedAsync(fixture.Provider);
        var savedSnapshot = Assert.IsType<string>(
            (await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id)).Session?.StateJson);
        using (var unanswered = fixture.Quiz)
        {
            var restoredLaunch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
            Assert.Equal(NativePhase.ResumeChoice,
                (await unanswered.OpenAsync(restoredLaunch)).Phase);
        }
        Assert.Equal(savedSnapshot,
            (await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id)).Session?.StateJson);
        using (var resumed = fixture.Quiz)
        {
            var restoredLaunch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
            Assert.Equal(NativePhase.ResumeChoice,
                (await resumed.OpenAsync(restoredLaunch)).Phase);
            var state = await resumed.ResumeAsync();
            for (var turn = 0; turn < 10; turn++)
            {
                Assert.Equal(NativePhase.Ready, state.Phase);
                var word = await WordAsync(fixture, state.Prompt!.WordId);
                var answer = state.Prompt.IsTargetLanguagePrompt
                    ? word.NativeLanguageTerm! : word.TargetLanguageTerm!;
                Assert.Equal(NativePhase.Feedback,
                    (await resumed.AnswerAsync(answer)).Phase);
                state = await resumed.NextAsync();
            }
            Assert.Equal(NativePhase.Summary, state.Phase);
            var back = await resumed.BackAsync();
            Assert.Equal(NativeDestination.TodaysPlan, back.Destination);
            Assert.Equal(NativePhase.Ready, back.Phase);
            Assert.True(back.HasPendingSync);
        }

        var completed = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
        Assert.True(completed.Item.IsCompleted);
        Assert.Equal(ActivitySessionStatus.Completed, completed.Session?.Status);
        Assert.Equal(10, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        fixture.Restart();
        await preview.SeedAsync(fixture.Provider);
        Assert.True(Assert.Single((await fixture.Dashboard.LoadAsync()).Cards,
            item => item.Id == card.Id).IsCompleted);
        Assert.Equal(completed.Item.CompletedAt,
            (await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id)).Item.CompletedAt);
        Assert.Equal(10, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task TextFeedback_ColdResumeRetainsSubmittedAnswerForReview()
    {
        using var fixture = new NativeSliceFixture(
            productionAdapters: true, realPlanGenerator: true);
        var preview = SelectFixture("question");
        await preview.SeedAsync(fixture.Provider);
        var card = preview.SelectAssignedQuizCard(await fixture.Dashboard.LoadAsync())!;
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
        const string submitted = "incorrect response";

        using (var quiz = fixture.Quiz)
        {
            var ready = await quiz.OpenAsync(launch);
            Assert.Equal("Text", ready.Mode);
            var feedback = await quiz.AnswerAsync(submitted);
            Assert.Equal(NativePhase.Feedback, feedback.Phase);
            Assert.True(feedback.RequiresCorrection);
            Assert.Equal(submitted, feedback.SelectedAnswer);
            Assert.Equal(NativeDestination.TodaysPlan,
                (await quiz.BackAsync()).Destination);
        }

        fixture.Restart();
        await preview.SeedAsync(fixture.Provider);
        var restoredCard = preview.SelectAssignedQuizCard(
            await fixture.Dashboard.LoadAsync())!;
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync((await fixture.Dashboard.SelectAssignedQuizAsync(
                restoredCard.Id))!)).Phase);
        var restoredFeedback = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Feedback, restoredFeedback.Phase);
        Assert.Equal(submitted, restoredFeedback.SelectedAnswer);
        Assert.True(restoredFeedback.RequiresCorrection);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Theory]
    [InlineData("mc")]
    [InlineData("mc-resume")]
    [InlineData("complete")]
    public async Task CompletedQuizCard_SelectsDashboardNotAnotherLaunch(string surface)
    {
        using var fixture = new NativeSliceFixture(
            productionAdapters: true, realPlanGenerator: true);
        var preview = SelectFixture(surface);
        await preview.SeedAsync(fixture.Provider);
        var dashboard = await fixture.Dashboard.LoadAsync();
        var card = preview.SelectAssignedQuizCard(dashboard)!;
        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!);
        for (var turn = 0; turn < 10; turn++)
        {
            var word = await WordAsync(fixture, state.Prompt!.WordId);
            await quiz.AnswerAsync(word.NativeLanguageTerm!);
            state = await quiz.NextAsync();
        }
        Assert.Equal(NativePhase.Summary, state.Phase);
        await quiz.DoneAsync();
        var before = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);

        fixture.Restart();
        await preview.SeedAsync(fixture.Provider);
        dashboard = await fixture.Dashboard.LoadAsync();
        Assert.Null(preview.SelectAssignedQuizCard(dashboard));
        Assert.Equal(before.Item.CompletedAt,
            (await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id)).Item.CompletedAt);
        Assert.Equal(10, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task FixtureRefusesForeignOwnerInsteadOfAdoptingOrClearingDatabase()
    {
        using var fixture = new NativeSliceFixture(
            productionAdapters: true, realPlanGenerator: true);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var preview = SelectFixture("mc");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => preview.SeedAsync(fixture.Provider));

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(NativeSliceFixture.UserB,
            (await db.UserProfiles.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(await db.DailyPlanCompletions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void FixtureSelection_RequiresOwnerIsolatedSchemaAndLoopbackEndpoint()
    {
        Assert.Null(NativePreviewFixture.FromAttributes(
            [], NativeApiEndpoint.Validate("http://127.0.0.1:5081/", true)));
        Assert.Throws<InvalidOperationException>(() =>
            NativePreviewFixture.FromAttributes(
                Metadata("mc").Where(item =>
                    item.Key != "SentenceStudio.Native.PreviewSchema"),
                NativeApiEndpoint.Validate("http://127.0.0.1:5081/", true)));
        Assert.Throws<InvalidOperationException>(() =>
            NativePreviewFixture.FromAttributes(
                Metadata("mc"), NativeApiEndpoint.Validate(
                    "https://api.example.invalid/", false)));
    }

    private static NativePreviewFixture SelectFixture(string surface) =>
        NativePreviewFixture.FromAttributes(
            Metadata(surface), NativeApiEndpoint.Validate(
                "http://127.0.0.1:5081/", true))!;

    private static AssemblyMetadataAttribute[] Metadata(string surface) =>
    [
        new("SentenceStudio.Native.PreviewSurface", surface),
        new("SentenceStudio.Native.PreviewSchema", "true"),
        new("SentenceStudio.Native.PreviewOwnerId", NativeSliceFixture.UserA),
        new("SentenceStudio.Native.PreviewEmail", NativeSliceFixture.EmailA)
    ];

    private static async Task<SentenceStudio.Shared.Models.VocabularyWord> WordAsync(
        NativeSliceFixture fixture, string wordId)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == wordId);
    }
}
#endif
