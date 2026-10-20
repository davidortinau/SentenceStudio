using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeQuizSourceLaunchTests
{
    [Fact]
    public async Task ChooseOwnMultiResource_ScoresRealOwnedTurnWithoutGeneratingDailyPlan()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        await AddOwnedResourceAsync(fixture);
        fixture.Activate(NativeSliceFixture.UserA);
        var launch = await fixture.Provider.GetRequiredService<NativeQuizLaunchService>()
            .CreateChooseOwnAsync(NativeSliceFixture.UserA,
                [fixture.ResourceId(NativeSliceFixture.UserA), "second-owner-a"],
                skillId: null);
        Assert.NotNull(launch);
        Assert.Equal("", launch.PlanItemId);
        Assert.Equal(2, launch.ResourceIds.Length);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.Equal(0, await CountDailyPlansAsync(fixture));
        var feedback = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        var next = await quiz.NextAsync();
        Assert.True(next.Phase is NativePhase.Ready or NativePhase.Summary);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt!.WordId));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
        Assert.Equal(0, await CountDailyPlansAsync(fixture));
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var adHoc = Assert.Single(await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.UserProfileId == NativeSliceFixture.UserA)
            .ToListAsync());
        Assert.StartsWith("adhoc-", adHoc.PlanItemId);
        Assert.Null(adHoc.ResourceId);
        Assert.False(adHoc.IsArchived);
    }

    [Fact]
    public async Task DueOnlyWithSelectedResource_UsesOwnersActualDueVocabulary()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await AddOwnedResourceAsync(fixture);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var allWords = await db.VocabularyWords
                .Where(word => word.Id.StartsWith("word-native-owner-a")
                    || word.Id == "word-in-second").ToListAsync();
            foreach (var word in allWords)
            {
                db.VocabularyProgresses.Add(new VocabularyProgress
                {
                    UserId = NativeSliceFixture.UserA,
                    VocabularyWordId = word.Id,
                    TotalAttempts = 2, CorrectAttempts = 1,
                    MasteryScore = 0.1f,
                    NextReviewDate = DateTime.Now.AddDays(
                        word.Id == fixture.WordId(NativeSliceFixture.UserA, 0) ? -1 : 5)
                });
            }
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var launch = await fixture.Provider.GetRequiredService<NativeQuizLaunchService>()
            .CreateChooseOwnAsync(NativeSliceFixture.UserA,
                ["second-owner-a"], null, dueOnly: true);
        Assert.NotNull(launch);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.Equal(fixture.WordId(NativeSliceFixture.UserA, 0),
            question.Prompt!.WordId);
        await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.True((await quiz.NextAsync()).Phase is NativePhase.Ready
            or NativePhase.Summary);
        Assert.Equal(1, await CountDailyPlansAsync(fixture, onlyAdHoc: true));
        Assert.Equal(0, await CountDailyPlansAsync(fixture));
    }

    [Fact]
    public async Task ForeignOrStaleChoice_RefusesBeforeCreatingActivityOrWritingProgress()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var launcher = fixture.Provider.GetRequiredService<NativeQuizLaunchService>();
        Assert.Null(await launcher.CreateChooseOwnAsync(
            NativeSliceFixture.UserA,
            [fixture.ResourceId(NativeSliceFixture.UserB)], null));
        Assert.Null(await launcher.CreateChooseOwnAsync(
            NativeSliceFixture.UserA,
            [fixture.ResourceId(NativeSliceFixture.UserA), "removed-owner-a"], null));
        var owned = await launcher.CreateChooseOwnAsync(
            NativeSliceFixture.UserA,
            [fixture.ResourceId(NativeSliceFixture.UserA)], null);
        Assert.NotNull(owned);
        using var quiz = fixture.Quiz;
        Assert.Equal(NativePhase.Blocked,
            (await quiz.OpenAsync(owned with { PlanDate = owned.PlanDate.AddDays(-1) }))
                .Phase);
        Assert.Equal(0, await CountDailyPlansAsync(fixture));
        Assert.Equal(0, await CountDailyPlansAsync(fixture, onlyAdHoc: true));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task CompletedAssignedCard_RepracticeCreditsVocabularyWithoutRecreditingThePlan()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            Assert.True((await lease.Service.MarkCompleteAsync(
                lease.LocalDate, itemId, 2))?.IsCompleted);
        var before = (await fixture.RowsAsync(
            NativeSliceFixture.UserA, itemId)).Item;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        Assert.NotNull(launch);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch with { IsRepractice = true });
        Assert.Equal(NativePhase.Ready, question.Phase);

        for (var index = 0; index < 10 && question.Phase == NativePhase.Ready; index++)
        {
            var feedback = await quiz.AnswerAsync(question.Choices[0].Text);
            Assert.Equal(NativePhase.Feedback, feedback.Phase);
            question = await quiz.NextAsync();
        }
        Assert.Equal(NativePhase.Summary, question.Phase);
        Assert.Equal(NativeDestination.TodaysPlan,
            (await quiz.DoneAsync()).Destination);
        var after = (await fixture.RowsAsync(
            NativeSliceFixture.UserA, itemId)).Item;
        Assert.True(after.IsCompleted);
        Assert.Equal(before.MinutesSpent, after.MinutesSpent);
        Assert.Equal(before.CompletedAt, after.CompletedAt);
        Assert.True(await fixture.AttemptCountAsync(NativeSliceFixture.UserA) > 0);
        Assert.Equal(0, await CountDailyPlansAsync(fixture, onlyAdHoc: true));
    }

    [Fact]
    public async Task ChooseOwnOwnedSkillAndFocus_KeepSelectedContextAndRejectForeignWords()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SkillProfiles.Add(new SkillProfile
            {
                Id = "owned-skill", UserProfileId = NativeSliceFixture.UserA,
                Title = "Current skill", Language = "Korean"
            });
            db.SkillProfiles.Add(new SkillProfile
            {
                Id = "foreign-skill", UserProfileId = NativeSliceFixture.UserB,
                Title = "Foreign skill", Language = "Korean"
            });
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var launcher = fixture.Provider.GetRequiredService<NativeQuizLaunchService>();
        var ownFocus = fixture.WordId(NativeSliceFixture.UserA, 0);
        Assert.Null(await launcher.CreateChooseOwnAsync(
            NativeSliceFixture.UserA, [], "foreign-skill",
            focusVocabularyIds: [ownFocus]));
        Assert.Null(await launcher.CreateChooseOwnAsync(
            NativeSliceFixture.UserA, [], "owned-skill",
            focusVocabularyIds: [fixture.WordId(NativeSliceFixture.UserB, 0)]));
        var launch = await launcher.CreateChooseOwnAsync(
            NativeSliceFixture.UserA, [fixture.ResourceId(NativeSliceFixture.UserA)],
            "owned-skill", focusVocabularyIds: [ownFocus]);
        Assert.NotNull(launch);
        Assert.Equal("owned-skill", launch.SkillId);
        Assert.Equal(ownFocus, Assert.Single(launch.FocusVocabularyIds));
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch);
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.Equal(ownFocus, question.Prompt!.WordId);
        Assert.Equal(0, await CountDailyPlansAsync(fixture));
    }

    private static async Task AddOwnedResourceAsync(NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.LearningResources.Add(new LearningResource
        {
            Id = "second-owner-a",
            UserProfileId = NativeSliceFixture.UserA,
            MediaType = "Vocabulary List",
            Language = "Korean",
            Title = "Second owned resource"
        });
        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = "word-in-second",
            TargetLanguageTerm = "second target",
            NativeLanguageTerm = "second gloss",
            Language = "Korean"
        });
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = "second-owned-mapping",
            ResourceId = "second-owner-a",
            VocabularyWordId = "word-in-second"
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> CountDailyPlansAsync(
        NativeSliceFixture fixture, bool onlyAdHoc = false)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return onlyAdHoc
            ? await db.DailyPlanCompletions.AsNoTracking()
                .CountAsync(row => row.UserProfileId == NativeSliceFixture.UserA
                    && row.PlanItemId.StartsWith("adhoc-"))
            : await db.DailyPlans.AsNoTracking()
                .CountAsync(row => row.UserProfileId == NativeSliceFixture.UserA);
    }
}
