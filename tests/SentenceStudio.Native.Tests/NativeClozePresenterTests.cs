using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Cloze source contract (activities.md 1.1-1.4 and 4) on the shared
/// activity foundation, against real SQLite and shared services with an
/// isolated AI gateway.
/// </summary>
public sealed class NativeClozePresenterTests
{
    private const string Cloze = NativeActivityCatalog.Cloze;

    [Fact]
    public void Catalog_RegistersClozeForThePlanTypeAndTheChooseOwnTile()
    {
        Assert.True(NativeActivityCatalog.IsImplemented(Cloze));
        Assert.True(NativeActivityCatalog.IsImplemented(PlanActivityType.Cloze));
        Assert.Equal(PlanActivityType.Cloze, NativeActivityCatalog.ForRoute(Cloze)!.TimerType);
        Assert.True(NativeActivityCatalog.IsVocabularyDriven(PlanActivityType.Cloze));
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Cloze);
    }

    [Fact]
    public async Task PlanCard_TypedAndChoiceAnswersRecordOwnedSideEffectsAndReachTheSummary()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        var owner = NativeSliceFixture.UserA;
        fixture.ResourceAi.ClozeResponder = prompt => Reply(owner, prompt.Contains(
            $"target-{owner}-0", StringComparison.Ordinal) ? 8 : 0);
        var (cards, dashboard) = await SignInWithPlanAsync(fixture, owner);
        var card = cards.Single(item => item.Activity == PlanActivityType.Cloze);
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(card.Id);
        Assert.NotNull(launch);
        Assert.Equal(Cloze, launch.Route);
        Assert.Empty(launch.ResourceIds);
        Assert.True(launch.DueOnly);
        using var cloze = Presenter(fixture);
        var startedUtc = DateTime.UtcNow.AddSeconds(-5);

        var state = await cloze.OpenAsync(launch);

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(8, state.Total);
        Assert.Equal(0, state.Index);
        Assert.Equal(NativeClozeInputMode.Text, state.InputMode);
        Assert.Contains(NativeClozeChallenge.BlankMarker, state.DisplaySentence);
        Assert.DoesNotContain(Answer(owner, 0), state.DisplaySentence);
        Assert.Equal("I like word 0.", state.Translation);
        Assert.False(state.CanGoPrevious);
        // Required-first focus words reach the source prompt.
        Assert.Contains("REQUIRED focus vocabulary", fixture.ResourceAi.LastPrompt);
        Assert.Equal(owner, fixture.ResourceAi.LastOwner);

        // Wrong typed answer: graded, answer shown, input locked until navigation.
        cloze.SetTypedAnswer("nope");
        state = await cloze.SubmitAsync();
        Assert.True(state.IsGraded);
        Assert.False(state.LastWasCorrect);
        Assert.Equal(Answer(owner, 0), state.CorrectAnswer);
        Assert.Contains(NativeClozeChallenge.BlankMarker, state.DisplaySentence);
        cloze.SetTypedAnswer(Answer(owner, 0));
        state = await cloze.SubmitAsync();
        Assert.Equal(1, state.SessionTotal);
        Assert.Equal(0, state.Index);

        // Next clears grading; a particle-less typed answer is accepted.
        state = await cloze.NextAsync();
        Assert.Equal(1, state.Index);
        Assert.False(state.IsGraded);
        Assert.Equal(string.Empty, state.TypedAnswer);
        cloze.SetTypedAnswer($"  target-{owner}-1 ");
        state = await cloze.SubmitAsync();
        // Correct: sentence revealed, then auto-advance.
        Assert.Equal(2, state.Index);
        Assert.Equal(2, state.SessionTotal);
        Assert.Equal(1, state.SessionCorrect);

        // Choices mode: five target-language options including the answer.
        state = await cloze.ToggleModeAsync();
        Assert.Equal(NativeClozeInputMode.MultipleChoice, state.InputMode);
        Assert.Equal(5, state.Options.Length);
        Assert.Contains(Answer(owner, 2), state.Options);
        var wrongChoice = state.Options.First(option => option != Answer(owner, 2));
        state = await cloze.ChooseAsync(wrongChoice);
        Assert.False(state.LastWasCorrect);
        Assert.Equal(wrongChoice, state.SelectedOption);
        // Locked: another choice is ignored.
        state = await cloze.ChooseAsync(Answer(owner, 2));
        Assert.Equal(3, state.SessionTotal);
        state = await cloze.PreviousAsync();
        Assert.Equal(1, state.Index);
        Assert.False(state.IsGraded);
        state = await cloze.NextAsync();
        state = await cloze.NextAsync();
        Assert.Equal(3, state.Index);
        state = await cloze.ChooseAsync(Answer(owner, 3));
        Assert.Equal(4, state.Index);

        while (state.Phase == NativePhase.Ready)
            state = await cloze.NextAsync();
        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(4, state.SessionTotal);
        Assert.Equal(2, state.SessionCorrect);
        Assert.Equal(2, state.SessionIncorrect);
        Assert.Equal(50, state.AccuracyPercent);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var activity = await db.UserActivities.AsNoTracking()
                .Where(row => row.Activity == nameof(Activity.Clozure)).ToListAsync();
            Assert.Equal(4, activity.Count);
            Assert.All(activity, row => Assert.Equal(owner, row.UserProfileId));
            // UTC instants (ComputeStreak), not the device-local wall clock.
            Assert.All(activity, row => Assert.InRange(row.CreatedAt, startedUtc, DateTime.UtcNow.AddSeconds(5)));
            Assert.Equal(2, activity.Count(row => row.Accuracy == 100));
            var contexts = await db.VocabularyLearningContexts.AsNoTracking()
                .Where(row => row.Activity == NativeClozePresenter.AttemptActivity)
                .Join(db.VocabularyProgresses, context => context.VocabularyProgressId,
                    progress => progress.Id, (context, progress) => new { context, progress })
                .ToListAsync();
            Assert.Equal(4, contexts.Count);
            Assert.All(contexts, row =>
            {
                Assert.Equal(owner, row.progress.UserId);
                Assert.Equal("Sentence", row.context.ContextType);
            });
            Assert.Equal(2, contexts.Count(row => row.context.InputMode == NativeClozeInputMode.Text));
            Assert.Equal(2, contexts.Count(row =>
                row.context.InputMode == NativeClozeInputMode.MultipleChoice));
            Assert.Equal(2, contexts.Count(row => row.context.WasCorrect));
            Assert.Contains(contexts, row => row.context.WasCorrect
                && row.progress.VocabularyWordId == fixture.WordId(owner, 1));
        }

        var exit = await cloze.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.True(exit.HasPendingSync);
        var reloaded = await dashboard.LoadAsync();
        Assert.Contains(reloaded.Cards, item => item.Id == card.Id);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task ContinuePractice_ResetsCountersAndLoadsEightNewSentences()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        var owner = NativeSliceFixture.UserA;
        var calls = 0;
        fixture.ResourceAi.ClozeResponder = _ => { calls++; return Reply(owner, 8); };
        await fixture.SeedAsync(owner, wordCount: 10);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var cloze = Presenter(fixture);
        var state = await cloze.OpenAsync(ChooseOwnLaunch(fixture, owner));
        cloze.SetTypedAnswer(Answer(owner, 0));
        state = await cloze.SubmitAsync();
        while (state.Phase == NativePhase.Ready)
            state = await cloze.NextAsync();
        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(1, state.SessionCorrect);

        state = await cloze.ContinuePracticeAsync();

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(0, state.Index);
        Assert.Equal(8, state.Total);
        Assert.Equal(0, state.SessionTotal);
        Assert.Equal(0, state.SessionCorrect);
        Assert.Equal(2, calls);
        // The Choose My Own resource path sends no required focus block.
        Assert.DoesNotContain("REQUIRED focus vocabulary", fixture.ResourceAi.LastPrompt);
    }

    [Fact]
    public async Task AiFailureOrNoUsableReply_IsAnHonestErrorAndRecordsNothing()
    {
        foreach (var setup in new Action<ScratchResourceAiGateway>[]
        {
            ai => ai.ClozeFailure = new HttpRequestException("gateway down"),
            ai => ai.ClozeResponder = _ => null,
            ai => ai.ClozeResponder = _ => new ClozureResponse(),
        })
        {
            using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
            setup(fixture.ResourceAi);
            await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 4);
            await SignInAsync(fixture, NativeSliceFixture.EmailA);
            using var cloze = Presenter(fixture);

            var state = await cloze.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));

            Assert.Equal(NativePhase.Error, state.Phase);
            Assert.Equal(NativeClozeText.GenerationFailed, state.ErrorKey);
            Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
            Assert.Equal(NativeDestination.TodaysPlan, (await cloze.ExitAsync()).Destination);
        }
    }

    [Fact]
    public async Task NoVocabulary_ShowsTheEmptyStateWithoutCallingTheAi()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        var called = false;
        fixture.ResourceAi.ClozeResponder = _ => { called = true; return Reply(NativeSliceFixture.UserA, 8); };
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var cloze = Presenter(fixture);

        var state = await cloze.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));

        Assert.Equal(NativePhase.Empty, state.Phase);
        Assert.Equal(NativeClozeText.NoSentences, state.ErrorKey);
        Assert.False(called);
    }

    [Fact]
    public async Task SelfHealing_RepairsTheBlankAndGuessesAndDropsAnUnblankableSentence()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        var owner = NativeSliceFixture.UserA;
        var word = $"target-{owner}-0";
        fixture.ResourceAi.ClozeResponder = _ => new ClozureResponse
        {
            Sentences =
            [
                // As-used carries a particle the sentence does not have; guesses miss the answer.
                new ClozureDto
                {
                    SentenceText = $"나는 {word} 좋아해요.", RecommendedTranslation = "I like it.",
                    VocabularyWord = word, VocabularyWordAsUsed = $"{word}을",
                    VocabularyWordGuesses = ["가방을", "책을", "물을", "빵을", "차를"]
                },
                // Neither form appears: the answer would be visible, so it is dropped.
                new ClozureDto
                {
                    SentenceText = "아무것도 없어요.", RecommendedTranslation = "Nothing.",
                    VocabularyWord = word, VocabularyWordAsUsed = "없는말",
                    VocabularyWordGuesses = ["없는말", "a", "b", "c", "d"]
                },
            ]
        };
        await fixture.SeedAsync(owner, wordCount: 2);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var cloze = Presenter(fixture);

        var state = await cloze.OpenAsync(ChooseOwnLaunch(fixture, owner));

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(1, state.Total);
        Assert.Equal($"나는 {NativeClozeChallenge.BlankMarker} 좋아해요.", state.DisplaySentence);
        Assert.Equal(5, state.Options.Length);
        Assert.Contains(word, state.Options);
        state = await cloze.ToggleModeAsync();
        state = await cloze.ChooseAsync(word);
        // Correct on the only sentence: the auto-advance opens the summary.
        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(1, state.SessionCorrect);
        Assert.Equal(1, await fixture.AttemptCountAsync(owner));
    }

    [Fact]
    public async Task ManualNavigationDuringTheAdvanceDelaySupersedesTheAutoAdvance()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        var owner = NativeSliceFixture.UserA;
        fixture.ResourceAi.ClozeResponder = _ => Reply(owner, 8);
        await fixture.SeedAsync(owner, wordCount: 10);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var cloze = fixture.Provider.GetRequiredService<NativeClozePresenter>();
        Assert.Equal(TimeSpan.FromMilliseconds(1200), cloze.AdvanceDelay);
        cloze.AdvanceDelay = TimeSpan.FromMilliseconds(300);
        await cloze.OpenAsync(ChooseOwnLaunch(fixture, owner));

        cloze.SetTypedAnswer(Answer(owner, 0));
        var pending = cloze.SubmitAsync();
        await WaitForAsync(() => cloze.State.IsGraded);
        Assert.True(cloze.State.LastWasCorrect);
        // Correct reveals the full sentence while waiting.
        Assert.Contains(Answer(owner, 0), cloze.State.DisplaySentence);
        Assert.Equal(1, (await cloze.NextAsync()).Index);
        await pending;

        Assert.Equal(1, cloze.State.Index);
    }

    [Fact]
    public async Task ExitDuringAHungSentenceLoad_ReturnsAtOnceAndSavesTheTimerRow()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        var owner = NativeSliceFixture.UserA;
        var hang = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ResourceAi.ClozeHang = hang;
        fixture.ResourceAi.ClozeResponder = _ => Reply(owner, 8);
        await fixture.SeedAsync(owner, wordCount: 10);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var cloze = Presenter(fixture);

        var open = cloze.OpenAsync(ChooseOwnLaunch(fixture, owner));
        await WaitForAsync(() => fixture.ResourceAi.LastPrompt is not null);
        Assert.Equal(NativePhase.Loading, cloze.State.Phase);

        var exit = await cloze.ExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        // The abandoned load never reopens the round.
        hang.SetResult(true);
        await open;
        Assert.Equal(NativeDestination.TodaysPlan, cloze.State.Destination);
        Assert.Equal(0, cloze.State.Total);

        using var scope = fixture.Scope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DailyPlanCompletions.AsNoTracking().SingleAsync(item => item.UserProfileId == owner);
        Assert.StartsWith("adhoc-", row.PlanItemId);
        Assert.Equal(nameof(PlanActivityType.Cloze), row.ActivityType);
    }

    [Fact]
    public async Task FamiliarWord_GetsTheVerificationProbeAndItsNotice()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        var owner = NativeSliceFixture.UserA;
        fixture.ResourceAi.ClozeResponder = _ => Reply(owner, 2);
        await fixture.SeedAsync(owner, wordCount: 2);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = "familiar-progress", UserId = owner,
                VocabularyWordId = fixture.WordId(owner, 0),
                IsUserDeclared = true, VerificationState = VerificationStatus.Pending,
                UserDeclaredAt = DateTime.UtcNow.AddDays(-30), TotalAttempts = 1,
                NextReviewDate = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var cloze = Presenter(fixture);
        cloze.AdvanceDelay = TimeSpan.FromMilliseconds(200);
        await cloze.OpenAsync(ChooseOwnLaunch(fixture, owner));

        cloze.SetTypedAnswer(Answer(owner, 0));
        var pending = cloze.SubmitAsync();
        await WaitForAsync(() => cloze.State.NoticeKey is not null);
        Assert.Equal(NativeClozeText.FamiliarConfirmed, cloze.State.NoticeKey);
        Assert.True(cloze.State.NoticeIsSuccess);
        await pending;

        using var check = fixture.Scope();
        var row = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyProgresses.AsNoTracking().SingleAsync(item => item.Id == "familiar-progress");
        Assert.NotEqual(VerificationStatus.Pending, row.VerificationState);
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedWithoutRows()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        fixture.ResourceAi.ClozeResponder = _ => Reply(NativeSliceFixture.UserA, 8);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 10);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);

        using (var foreign = Presenter(fixture))
        {
            var refused = await foreign.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserB));
            Assert.Equal(NativePhase.Blocked, refused.Phase);
            Assert.Equal(NativeClozeText.Unavailable, refused.ErrorKey);
        }

        using var cloze = Presenter(fixture);
        Assert.Equal(NativePhase.Ready,
            (await cloze.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA))).Phase);
        fixture.LoseOwner();
        cloze.SetTypedAnswer(Answer(NativeSliceFixture.UserA, 0));
        var blocked = await cloze.SubmitAsync();

        Assert.Equal(NativePhase.Blocked, blocked.Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.UserActivities.AnyAsync());
        Assert.False(await db.DailyPlanCompletions.AnyAsync(row =>
            row.UserProfileId == NativeSliceFixture.UserB));
        Assert.Equal(NativeDestination.TodaysPlan, (await cloze.ExitAsync()).Destination);
    }

    private static string Answer(string owner, int index) => $"target-{owner}-{index}을";

    private static ClozureResponse Reply(string owner, int count) => new()
    {
        Sentences = Enumerable.Range(0, count).Select(index => new ClozureDto
        {
            SentenceText = $"나는 {Answer(owner, index)} 좋아해요.",
            RecommendedTranslation = $"I like word {index}.",
            VocabularyWord = $"target-{owner}-{index}",
            VocabularyWordAsUsed = Answer(owner, index),
            VocabularyWordGuesses = [Answer(owner, index), "가방을", "책을", "물을", "빵을"]
        }).ToList()
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the presenter.");
            await Task.Delay(10);
        }
    }

    private static NativeClozePresenter Presenter(NativeSliceFixture fixture)
    {
        var presenter = fixture.Provider.GetRequiredService<NativeClozePresenter>();
        presenter.AdvanceDelay = TimeSpan.Zero;
        return presenter;
    }

    private static NativeActivityLaunch ChooseOwnLaunch(NativeSliceFixture fixture, string owner) =>
        new(Cloze, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner));

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);

    private static async Task<(IReadOnlyList<NativePlanCard> Cards, NativeDashboardPresenter Dashboard)>
        SignInWithPlanAsync(NativeSliceFixture fixture, string owner)
    {
        await fixture.SeedAsync(owner, wordCount: 10);
        fixture.SetPlanActivities(owner,
            (nameof(PlanActivityType.VocabularyReview), null, null, 6, 1),
            (nameof(PlanActivityType.Cloze), fixture.ResourceId(owner), null, 10, 2));
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var dashboard = fixture.Dashboard;
        var plan = await dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, plan.Phase);
        return (plan.Cards, dashboard);
    }
}
