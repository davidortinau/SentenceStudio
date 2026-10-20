using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Native.Tests;

public sealed class NativeDashboardProjectionTests
{
    [Fact]
    public async Task ModeDefaultsToTodayAndPersistsSeparatelyForEachOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Preferences.Set("DashboardMode", "ChooseOwn");
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        Assert.Equal(NativeDashboardMode.TodaysPlan, (await dashboard.LoadAsync()).Mode);
        Assert.Equal(NativeDashboardMode.ChooseOwn,
            dashboard.SetMode(NativeDashboardMode.ChooseOwn).Mode);
        Assert.Equal("ChooseOwn", fixture.Preferences.Get(
            "DashboardMode:" + NativeSliceFixture.UserA, string.Empty));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.Equal(NativeDashboardMode.ChooseOwn,
            (await fixture.Dashboard.LoadAsync()).Mode);
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativeDashboardMode.TodaysPlan,
            (await fixture.Dashboard.LoadAsync()).Mode);
    }

    [Fact]
    public async Task ChooseOwnInitialLoadDoesNotCreateTodaysPlanUntilTodayIsSelected()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        dashboard.SetMode(NativeDashboardMode.ChooseOwn);

        var state = await dashboard.LoadAsync();
        Assert.Equal(NativeDashboardMode.ChooseOwn, state.Mode);
        Assert.NotNull(state.ChooseOwn);
        await dashboard.RegenerateAsync();
        using (var scope = fixture.Scope())
            Assert.Empty(await scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>()
                .DailyPlans.ToListAsync());

        dashboard.SetMode(NativeDashboardMode.TodaysPlan);
        var today = await dashboard.LoadAsync();
        Assert.Equal(NativeDashboardMode.TodaysPlan, today.Mode);
        using var verify = fixture.Scope();
        Assert.Single(await verify.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .DailyPlans.ToListAsync());
    }

    [Fact]
    public async Task OwnerWithVocabularyProgressButNoResourcesDoesNotSeeFalseWelcome()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = "orphaned-progress-word",
                Language = "Korean",
                TargetLanguageTerm = "안녕",
                NativeLanguageTerm = "hello"
            });
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = "owned-progress-without-resource",
                UserId = NativeSliceFixture.UserA,
                VocabularyWordId = "orphaned-progress-word"
            });
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);

        var returningOwner = await fixture.Dashboard.LoadAsync();
        Assert.False(returningOwner.IsNewUser);

        fixture.Activate(NativeSliceFixture.UserB);
        var otherOwner = await fixture.Dashboard.LoadAsync();
        Assert.True(otherOwner.IsNewUser);
    }

    [Fact]
    public async Task ChooseOwnLoadsOwnedResourcesAndSkillsReconcilesStaleSelectionsAfterRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SkillProfiles.AddRange(
                new SkillProfile {
                    Id = "skill-a", UserProfileId = NativeSliceFixture.UserA,
                    Title = "Words", Language = "Korean"
                },
                new SkillProfile {
                    Id = "skill-archived", UserProfileId = NativeSliceFixture.UserA,
                    Title = "Archived", Language = "Korean", IsArchived = true
                },
                new SkillProfile {
                    Id = "skill-b", UserProfileId = NativeSliceFixture.UserB,
                    Title = "Foreign", Language = "Korean"
                });
            await db.SaveChangesAsync();
        }
        fixture.Preferences.Set(ChooseOwnSelectionPreferences.LegacyResourceIdsKey,
            fixture.ResourceId(NativeSliceFixture.UserB));
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        dashboard.SetMode(NativeDashboardMode.ChooseOwn);
        var loaded = await dashboard.LoadChooseOwnAsync();
        var own = loaded.ChooseOwn;
        Assert.NotNull(own);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA),
            Assert.Single(own.Resources).Id);
        Assert.Equal("skill-a", Assert.Single(own.Skills).Id);
        Assert.Empty(own.SelectedResourceIds);
        Assert.Null(own.SelectedSkillId);
        Assert.Equal(14, NativeDashboardPresenter.ChooseOwnActivities.Length);
        Assert.Equal("conversation", NativeDashboardPresenter.ChooseOwnActivities[0].Route);

        var selected = dashboard.SelectChooseOwn(
            [fixture.ResourceId(NativeSliceFixture.UserA)], "skill-a");
        Assert.Equal("skill-a", selected.ChooseOwn!.SelectedSkillId);
        Assert.Single(selected.ChooseOwn.SelectedResourceIds);
        Assert.True(dashboard.SetChooseOwnExpanded(true).ChooseOwn!.ShowAllActivities);
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var restored = await fixture.Dashboard.LoadAsync();
        Assert.Equal(NativeDashboardMode.ChooseOwn, restored.Mode);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA),
            Assert.Single(restored.ChooseOwn!.SelectedResourceIds));
        Assert.Equal("skill-a", restored.ChooseOwn.SelectedSkillId);

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Null(dashboard.State.ChooseOwn);
        var other = fixture.Dashboard;
        other.SetMode(NativeDashboardMode.ChooseOwn);
        var foreign = await other.LoadChooseOwnAsync();
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserB),
            Assert.Single(foreign.ChooseOwn!.Resources).Id);
        Assert.Empty(foreign.ChooseOwn.SelectedResourceIds);
        Assert.Equal("NativeDashboard_SelectionUnavailable",
            other.SelectChooseOwn([fixture.ResourceId(NativeSliceFixture.UserA)],
                "skill-a").ChooseOwn!.ErrorKey);
        Assert.Empty(other.State.ChooseOwn!.SelectedResourceIds);
    }

    [Fact]
    public async Task ChooseOwnQuizLaunchRevalidatesOwnerAndNeverGeneratesPlan()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        dashboard.SetMode(NativeDashboardMode.ChooseOwn);
        await dashboard.LoadChooseOwnAsync();

        var selected = dashboard.SelectChooseOwn(
            [fixture.ResourceId(NativeSliceFixture.UserA)], null);
        Assert.Null(selected.ChooseOwn!.ErrorKey);
        var launch = await dashboard.SelectChooseOwnQuizAsync();
        Assert.NotNull(launch);
        Assert.Equal(NativeSliceFixture.UserA, launch.UserId);
        Assert.Equal(string.Empty, launch.PlanItemId);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA),
            Assert.Single(launch.ResourceIds));
        using (var scope = fixture.Scope())
            Assert.Empty(await scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>()
                .DailyPlans.ToListAsync());

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(new LearningResource
            {
                Id = "second-owned-resource",
                UserProfileId = NativeSliceFixture.UserA,
                Title = "Second owned resource",
                Language = "Korean",
                MediaType = "Text"
            });
            await db.SaveChangesAsync();
        }
        await dashboard.LoadChooseOwnAsync();
        dashboard.SelectChooseOwn(
            [fixture.ResourceId(NativeSliceFixture.UserA),
             "second-owned-resource"], null);
        var multiple = await dashboard.SelectChooseOwnQuizAsync();
        Assert.NotNull(multiple);
        Assert.Equal(2, multiple.ResourceIds.Length);
        Assert.Contains("second-owned-resource", multiple.ResourceIds);
        using (var scope = fixture.Scope())
            Assert.Empty(await scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>()
                .DailyPlans.ToListAsync());

        dashboard.SelectChooseOwn(
            [fixture.ResourceId(NativeSliceFixture.UserB)], null);
        Assert.Null(await dashboard.SelectChooseOwnQuizAsync());
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Null(await dashboard.SelectChooseOwnQuizAsync());
    }

    [Fact]
    public async Task NewOwnerWithoutResourcesWaitsForSyncBeforeShowingGettingStarted()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.Sync.Outcome = NativeSyncOutcome.Unconfirmed;
        var dashboard = fixture.Dashboard;
        var pending = await dashboard.LoadAsync();
        Assert.Equal(NativePhase.PendingSync, pending.Phase);
        Assert.False(pending.IsNewUser);
        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        var welcome = await dashboard.RefreshAsync();
        Assert.Equal(NativePhase.Empty, welcome.Phase);
        Assert.True(welcome.IsNewUser);
        Assert.False(welcome.HasPendingSync);
        Assert.Empty(welcome.Cards.IsDefault ? [] : welcome.Cards);
        using var scope = fixture.Scope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DailyPlans.ToListAsync());
    }

    [Fact]
    public async Task NumberInsightsAreScopedAndShownOnlyForSupportedTargetLanguage()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NumberMasteryProgresses.AddRange(
                new NumberMasteryProgress {
                    UserProfileId = NativeSliceFixture.UserA, LanguageCode = "ko",
                    ContextCode = "time", CorrectCount = 3, TotalCount = 5,
                    DueDate = DateTime.UtcNow.AddMinutes(-5)
                },
                new NumberMasteryProgress {
                    UserProfileId = NativeSliceFixture.UserA, LanguageCode = "ko",
                    ContextCode = "time", CorrectCount = 1, TotalCount = 5,
                    DueDate = DateTime.UtcNow.AddDays(1)
                },
                new NumberMasteryProgress {
                    UserProfileId = NativeSliceFixture.UserB, LanguageCode = "ko",
                    ContextCode = "time", CorrectCount = 9, TotalCount = 10,
                    DueDate = DateTime.UtcNow.AddDays(-2)
                });
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        var owned = await dashboard.LoadAsync();
        var number = Assert.Single(owned.NumberContexts);
        Assert.Equal("time", number.ContextCode);
        Assert.Equal(4, number.CorrectCount);
        Assert.Equal(10, number.TotalCount);
        Assert.Equal(1, number.DueCount);
        Assert.Equal(40, number.MasteryPercentage);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.UserProfiles.SingleAsync(user =>
                user.Id == NativeSliceFixture.UserA)).TargetLanguage = "Spanish";
            await db.SaveChangesAsync();
        }
        Assert.Empty((await dashboard.LoadAsync()).NumberContexts);
    }

    [Fact]
    public void InsightCategoriesUseOriginalThreeTierMasteryRules()
    {
        var narrative = new PlanNarrativeDto {
            Story = "Focus", FocusAreas = [], Resources = [],
            VocabInsight = new VocabInsightDto {
                TotalDue = 3, ReviewCount = 2, NewCount = 1, AverageMastery = .5f,
                SampleStrugglingWords = [],
                StrugglingCategories =
                [
                    new TagInsightDto { Tag = "new", WordCount = 1,
                        AverageAccuracy = 0, TotalAttempts = 0 },
                    new TagInsightDto { Tag = "struggling", WordCount = 1,
                        AverageAccuracy = .59f, TotalAttempts = 4 },
                    new TagInsightDto { Tag = "doing-well", WordCount = 1,
                        AverageAccuracy = .6f, TotalAttempts = 2 }
                ]
            }
        };
        var state = new NativeDashboardState(NativePhase.Ready, Narrative: narrative);
        Assert.Equal("struggling", Assert.Single(state.StrugglingCategories).Tag);
        Assert.Equal("new", Assert.Single(state.NewCategories).Tag);
        Assert.Equal("doing-well", Assert.Single(state.DoingWellCategories).Tag);
    }

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
        var activeLaunch = await dashboard.SelectAssignedQuizAsync(plan.Cards[0].Id);
        Assert.NotNull(activeLaunch);
        Assert.False(activeLaunch.IsRepractice);
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
        var replay = await dashboard.SelectAssignedQuizAsync(updated.Cards[0].Id);
        Assert.NotNull(replay);
        Assert.True(replay.IsRepractice);
        int completedMinutes;
        DateTime? completedAt;
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var completed = await db.DailyPlanCompletions.AsNoTracking()
                .SingleAsync(row => row.UserProfileId == NativeSliceFixture.UserA
                    && row.PlanItemId == replay.PlanItemId);
            completedMinutes = completed.MinutesSpent;
            completedAt = completed.CompletedAt;
        }
        using (var quiz = fixture.Quiz)
        {
            Assert.NotEqual(NativePhase.Blocked,
                (await quiz.OpenAsync(replay)).Phase);
            Assert.Equal(NativeDestination.TodaysPlan,
                (await quiz.BackAsync()).Destination);
        }
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var after = await db.DailyPlanCompletions.AsNoTracking()
                .SingleAsync(row => row.UserProfileId == NativeSliceFixture.UserA
                    && row.PlanItemId == replay.PlanItemId);
            Assert.Equal(completedMinutes, after.MinutesSpent);
            Assert.Equal(completedAt, after.CompletedAt);
        }

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
    public async Task AutoGeneratedPlanVocabularySummary_UsesExplicitOwnerForCountsAndAccuracy()
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
        var generated = await fixture.Dashboard.LoadAsync();
        Assert.Equal(NativePhase.Ready, generated.Phase);
        Assert.Single(generated.Cards);
        Assert.NotNull(generated.VocabSummary);
        Assert.Equal(3, generated.VocabSummary.New);
        Assert.Equal(1, generated.VocabSummary.Learning);
        Assert.Equal(0, generated.VocabSummary.Known);
        Assert.Equal(0.75, generated.VocabSummary.SuccessRate7d, 2);

        fixture.LoseOwner();
        Assert.Equal(NativePhase.Blocked, (await fixture.Dashboard.LoadAsync()).Phase);
        Assert.Null(fixture.Dashboard.State.VocabSummary);
    }
}
