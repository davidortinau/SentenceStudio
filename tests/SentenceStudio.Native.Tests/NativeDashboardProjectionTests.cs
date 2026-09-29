using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeDashboardProjectionTests
{
    [Fact]
    public async Task PlanCardsAndProgress_UseOwnedResourceDataAndPersistedMinutes()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.VocabularyReview),
                fixture.ResourceId(NativeSliceFixture.UserA), null, 6, 1),
            (nameof(PlanActivityType.Listening),
                fixture.ResourceId(NativeSliceFixture.UserA), null, 10, 2),
            (nameof(PlanActivityType.Writing),
                fixture.ResourceId(NativeSliceFixture.UserA), null, 10, 3));

        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Phase);
        var dashboard = fixture.Dashboard;
        var plan = await dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, plan.Phase);
        Assert.Equal(3, plan.TotalCount);
        Assert.Equal(26, plan.EstimatedTotalMinutes);
        Assert.Equal(0, plan.CompletedCount);
        Assert.Equal(0, plan.MinutesSpent);
        Assert.Equal(0, plan.CompletionPercentage);
        Assert.Equal(4, plan.VocabSummary!.New);
        Assert.Equal(0, plan.VocabSummary.Learning);
        Assert.Equal(
            ["Activity_VocabularyReview", "Activity_Listening", "Activity_Writing"],
            plan.Cards.Select(card => card.TitleKey));
        Assert.All(plan.Cards, card => Assert.Equal("Synthetic vocabulary", card.ResourceTitle));
        Assert.NotNull(await dashboard.SelectAssignedQuizAsync(plan.Cards[0].Id));
        Assert.Null(await dashboard.SelectAssignedQuizAsync(plan.Cards[1].Id));

        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            Assert.NotNull(await lease.Service.MarkCompleteAsync(
                plan.PlanDate!.Value, plan.Cards[0].Id, 6));

        var updated = await dashboard.LoadAsync();
        Assert.Equal(1, updated.CompletedCount);
        Assert.Equal(3, updated.TotalCount);
        Assert.Equal(6, updated.MinutesSpent);
        Assert.Equal(26, updated.EstimatedTotalMinutes);
        Assert.Equal(100d / 3, updated.CompletionPercentage, 3);
        Assert.True(updated.Cards[0].IsCompleted);
        Assert.Null(await dashboard.SelectAssignedQuizAsync(updated.Cards[0].Id));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var foreign = await db.DailyPlanCompletions.SingleAsync(item =>
                item.UserProfileId == NativeSliceFixture.UserA
                && item.PlanItemId == plan.Cards[1].Id);
            foreign.ResourceId = fixture.ResourceId(NativeSliceFixture.UserB);
            await db.SaveChangesAsync();
        }

        var scoped = await dashboard.LoadAsync();
        Assert.Null(scoped.Cards[1].ResourceTitle);
        Assert.Equal("Synthetic vocabulary", scoped.Cards[2].ResourceTitle);

        fixture.LoseOwner();
        Assert.Equal(NativePhase.Blocked, (await dashboard.LoadAsync()).Phase);
        Assert.Empty(dashboard.State.Cards.IsDefault ? [] : dashboard.State.Cards);
        Assert.Null(dashboard.State.VocabSummary);
    }

    [Fact]
    public async Task EmptyPlanVocabularySummary_UsesExplicitOwnerForCountsAndAccuracy()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.AddRange(
                new VocabularyProgress
                {
                    Id = "dashboard-vocab-owner-a",
                    UserId = NativeSliceFixture.UserA,
                    VocabularyWordId = fixture.WordId(NativeSliceFixture.UserA, 0),
                    TotalAttempts = 4,
                    CorrectAttempts = 3,
                    LastPracticedAt = DateTime.Now.AddDays(-1),
                    NextReviewDate = DateTime.Now.AddDays(1),
                },
                new VocabularyProgress
                {
                    Id = "dashboard-vocab-owner-b",
                    UserId = NativeSliceFixture.UserB,
                    VocabularyWordId = fixture.WordId(NativeSliceFixture.UserB, 0),
                    TotalAttempts = 4,
                    CorrectAttempts = 4,
                    LastPracticedAt = DateTime.Now.AddDays(-1),
                });
            await db.SaveChangesAsync();
        }

        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Phase);
        var empty = await fixture.Dashboard.LoadAsync();
        Assert.Equal(NativePhase.Empty, empty.Phase);
        Assert.NotNull(empty.VocabSummary);
        Assert.Equal(3, empty.VocabSummary.New);
        Assert.Equal(1, empty.VocabSummary.Learning);
        Assert.Equal(0, empty.VocabSummary.Known);
        Assert.Equal(0.75, empty.VocabSummary.SuccessRate7d, 2);

        fixture.LoseOwner();
        Assert.Equal(NativePhase.Blocked, (await fixture.Dashboard.LoadAsync()).Phase);
        Assert.Null(fixture.Dashboard.State.VocabSummary);
    }
}
