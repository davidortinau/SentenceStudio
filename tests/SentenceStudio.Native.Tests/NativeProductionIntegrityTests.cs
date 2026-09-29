using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeProductionIntegrityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecreatedSameIdCard_CannotReceiveOldPendingAttemptOrSummary(
        bool reachSummary)
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        fixture.Activate(NativeSliceFixture.UserA);
        var card = Assert.Single((await fixture.Dashboard.GenerateAsync()).Cards);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch);
        var previousRowId = (await fixture.RowsAsync(
            NativeSliceFixture.UserA, card.Id)).Item.Id;

        var turns = reachSummary ? 2 : 1;
        for (var turn = 0; turn < turns; turn++)
        {
            var word = await WordAsync(fixture, question.Prompt!.WordId);
            question = await quiz.AnswerAsync(word.NativeLanguageTerm!);
            Assert.Equal(NativePhase.Feedback, question.Phase);
            if (reachSummary || turn < turns - 1)
                question = await quiz.NextAsync();
        }
        Assert.Equal(reachSummary ? NativePhase.Summary : NativePhase.Feedback,
            question.Phase);
        var scoredBeforeRegeneration = await fixture.AttemptCountAsync(
            NativeSliceFixture.UserA);

        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.Reading), fixture.ResourceId(NativeSliceFixture.UserA),
                null, 8, 1));
        Assert.Equal(NativePhase.Ready,
            (await fixture.Dashboard.RegenerateAsync()).Phase);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.VocabularyReview), null, null, 10, 1));
        var replacement = await fixture.Dashboard.RegenerateAsync();
        Assert.Equal(NativePhase.Ready, replacement.Phase);
        Assert.Equal(card.Id, Assert.Single(replacement.Cards).Id);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var original = await db.DailyPlanCompletions.AsNoTracking()
                .SingleAsync(row => row.Id == previousRowId);
            var active = await db.DailyPlanCompletions.AsNoTracking()
                .SingleAsync(row => row.UserProfileId == NativeSliceFixture.UserA
                    && row.PlanItemId == card.Id && !row.IsArchived);
            Assert.True(original.IsArchived);
            Assert.NotEqual(previousRowId, active.Id);
            Assert.False(active.IsCompleted);
        }

        if (reachSummary)
        {
            Assert.Equal(NativePhase.Blocked, (await quiz.DoneAsync()).Phase);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => quiz.NextAsync());
        }
        Assert.Equal(scoredBeforeRegeneration,
            await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        var nextLaunch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
        var rejected = await resumed.OpenAsync(nextLaunch);
        Assert.Equal(NativePhase.ResumeChoice, rejected.Phase);
        Assert.Equal(NativeText.ResumeUnavailable, rejected.ErrorKey);
        Assert.Equal(NativePhase.Ready, (await resumed.StartFreshAsync()).Phase);
    }

    [Fact]
    public async Task AllMasteredSummary_CannotCompleteAnArchivedCardAfterRegeneration()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2, known: true);
        fixture.SetPlanFocus(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var card = Assert.Single((await fixture.Dashboard.GenerateAsync()).Cards);
        using var quiz = fixture.Quiz;
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
        Assert.Equal(NativePhase.Summary, (await quiz.OpenAsync(launch)).Phase);
        var originalRowId = (await fixture.RowsAsync(
            NativeSliceFixture.UserA, card.Id)).Item.Id;

        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.Reading), fixture.ResourceId(NativeSliceFixture.UserA),
                null, 8, 1));
        Assert.Equal(NativePhase.Ready,
            (await fixture.Dashboard.RegenerateAsync()).Phase);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.VocabularyReview), null, null, 10, 1));
        Assert.Equal(NativePhase.Ready,
            (await fixture.Dashboard.RegenerateAsync()).Phase);
        Assert.Equal(NativePhase.Blocked, (await quiz.DoneAsync()).Phase);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var old = await db.DailyPlanCompletions.AsNoTracking()
                .SingleAsync(row => row.Id == originalRowId);
            var active = await db.DailyPlanCompletions.AsNoTracking()
                .SingleAsync(row => row.UserProfileId == NativeSliceFixture.UserA
                    && row.PlanItemId == card.Id && !row.IsArchived);
            Assert.True(old.IsArchived);
            Assert.False(old.IsCompleted);
            Assert.False(active.IsCompleted);
            Assert.Null(await db.ActivitySessions.AsNoTracking()
                .SingleOrDefaultAsync(row => row.UserId == NativeSliceFixture.UserA
                    && row.ActivityType == nameof(PlanActivityType.VocabularyReview)));
        }
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ArchivingAfterAnActiveRead_PreventsAStaleCompletionWrite()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var firstScope = fixture.Scope();
        using var secondScope = fixture.Scope();
        var first = firstScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var stale = await first.DailyPlanCompletions.SingleAsync(row =>
            row.UserProfileId == NativeSliceFixture.UserA
            && row.PlanItemId == planId && !row.IsArchived);
        var displaced = await second.DailyPlanCompletions.SingleAsync(row =>
            row.Id == stale.Id);
        displaced.IsArchived = true;
        await second.SaveChangesAsync();
        stale.IsCompleted = true;
        stale.MinutesSpent = 9;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => first.SaveChangesAsync());

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(row => row.Id == stale.Id);
        Assert.True(persisted.IsArchived);
        Assert.False(persisted.IsCompleted);
        Assert.Equal(0, persisted.MinutesSpent);
    }

    private static async Task<VocabularyWord> WordAsync(
        NativeSliceFixture fixture, string wordId)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == wordId);
    }
}
