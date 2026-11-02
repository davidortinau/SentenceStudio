using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Translation source contract (activities.md 1.1-1.4 and 5) on the shared
/// activity foundation, against real SQLite and shared progress services.
/// </summary>
public sealed class NativeTranslationPresenterTests
{
    private const string Route = NativeActivityCatalog.Translation;
    private const string SkillId = "translation-skill";
    private const string SkillDescription =
        "- Use -고 싶다 to express wants\n* Polite -아요/어요 endings\n\nExamples:\n- 가고 싶어요";

    [Fact]
    public void Catalog_RegistersTranslationForItsPlanTypeWithTheSkillOnTheTimer()
    {
        var definition = NativeActivityCatalog.ForRoute(Route);
        Assert.NotNull(definition);
        Assert.Equal(PlanActivityType.Translation, definition.TimerType);
        Assert.True(definition.TimerRecordsSkill);
        Assert.True(NativeActivityCatalog.IsImplemented(PlanActivityType.Translation));
        Assert.False(NativeActivityCatalog.IsVocabularyDriven(PlanActivityType.Translation));
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Route);
    }

    [Fact]
    public void GrammarBullets_FollowTheSourceParser()
    {
        var bullets = NativeTranslationPresenter.ParseGrammarBullets(SkillDescription);
        Assert.Equal(["Use -고 싶다 to express wants", "Polite -아요/어요 endings"], bullets.ToArray());
        Assert.Empty(NativeTranslationPresenter.ParseGrammarBullets(null));
        Assert.Equal(8, NativeTranslationPresenter.ParseGrammarBullets(
            string.Join('\n', Enumerable.Range(1, 12).Select(i => $"- rule {i}"))).Length);
        Assert.Equal(NativeScoreTone.Success, NativeTranslationFeedback.Tone(80));
        Assert.Equal(NativeScoreTone.Warning, NativeTranslationFeedback.Tone(50));
        Assert.Equal(NativeScoreTone.Danger, NativeTranslationFeedback.Tone(49.9));
    }

    [Fact]
    public async Task PlanCard_IntroSentenceBlocksGradeSummaryContinueAndExit()
    {
        using var fixture = new NativeSliceFixture();
        var ai = new ScratchTranslationAi(NativeSliceFixture.UserA);
        var (card, dashboard) = await PlanCardAsync(fixture, NativeSliceFixture.UserA);
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(card.Id);
        Assert.NotNull(launch);
        Assert.Equal(Route, launch.Route);
        // Translation is resource-driven: the plan resource and skill travel with the launch.
        Assert.Equal([fixture.ResourceId(NativeSliceFixture.UserA)], launch.ResourceIds.ToArray());
        Assert.Equal(SkillId, launch.SkillId);
        Assert.False(launch.DueOnly);
        using var translation = Presenter(fixture, ai);

        var state = await translation.OpenAsync(launch);

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.True(state.ShowIntro);
        Assert.Equal("Synthetic vocabulary", state.IntroResourceTitle);
        Assert.Equal(["Use -고 싶다 to express wants", "Polite -아요/어요 endings"],
            state.IntroGrammar.ToArray());
        // Focus words come first in the intro vocabulary, capped at 12.
        Assert.Equal($"target-{NativeSliceFixture.UserA}-0", state.IntroVocabulary[0].Target);
        Assert.True(state.IntroVocabulary.Length <= NativeTranslationPresenter.MaxIntroVocabulary);
        var generation = Assert.Single(ai.GeneratePrompts);
        Assert.Contains("REQUIRED FOCUS VOCABULARY", generation);
        Assert.Contains($"target-{NativeSliceFixture.UserA}-0 = native-{NativeSliceFixture.UserA}-0", generation);
        Assert.Contains("Use -고 싶다", generation);
        Assert.Contains("Generate 5 short, natural Korean sentences", generation);
        Assert.Equal(NativeSliceFixture.UserA, ai.LastOwner);

        // Commands other than Start are ignored on the intro.
        Assert.Equal(state, await translation.NextAsync());
        state = await translation.StartAsync();
        Assert.False(state.ShowIntro);
        Assert.Equal(0, state.Index);
        Assert.Equal(5, state.Count);
        Assert.Equal("I want sentence 0.", state.Prompt);
        Assert.Equal("Korean", state.TargetLanguage);
        Assert.False(state.BlocksMode);
        Assert.False(state.CanGoPrevious);
        Assert.False(state.CanGrade);

        state = await translation.ToggleInputModeAsync();
        Assert.True(state.ShowsBlocks);
        Assert.Contains($"target-{NativeSliceFixture.UserA}-0", state.Blocks);
        state = await translation.UseBlockAsync(0);
        state = await translation.UseBlockAsync(1);
        Assert.Equal($"{state.Blocks[0]} {state.Blocks[1]}", state.Input);

        translation.SetInput("가고 싶어요");
        state = await translation.GradeAsync();

        Assert.False(state.IsGrading);
        var feedback = Assert.IsType<NativeTranslationFeedback>(state.Feedback);
        Assert.Equal("가고 싶어요", feedback.SubmittedInput);
        Assert.Equal(85, feedback.Accuracy);
        Assert.Equal(70, feedback.Fluency);
        // The suggestion falls back to GrammarNotes.RecommendedTranslation.
        Assert.Equal("가고 싶어요.", feedback.RecommendedTranslation);
        Assert.True(feedback.HasDetails);
        Assert.Equal(new NativeTranslationNotice(NativeTranslationText.GradedAccuracy, false, "85"),
            state.Notice);
        var grade = Assert.Single(ai.GradePrompts);
        Assert.Contains("Given this sentence: \\\"I want sentence 0.\\\"", grade);
        Assert.Contains("this translation: \\\"target sentence 0\\\"", grade);
        Assert.Contains("I translated it as: \\\"가고 싶어요\\\"", grade);
        state = await translation.ToggleDetailsAsync();
        Assert.True(state.DetailsExpanded);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var activity = Assert.Single(await db.UserActivities.AsNoTracking().ToListAsync());
            Assert.Equal(NativeSliceFixture.UserA, activity.UserProfileId);
            Assert.Equal(nameof(Activity.Translation), activity.Activity);
            Assert.Equal("가고 싶어요", activity.Input);
            Assert.Equal(85, activity.Accuracy);
            Assert.Equal(70, activity.Fluency);
            var contexts = await db.VocabularyLearningContexts.AsNoTracking()
                .Where(context => context.Activity == NativeTranslationPresenter.AttemptActivity)
                .Join(db.VocabularyProgresses, context => context.VocabularyProgressId,
                    progress => progress.Id, (context, progress) => new { context, progress })
                .ToListAsync();
            // Two owned challenge words scored; the AI-only word is never recorded.
            Assert.Equal(2, contexts.Count);
            Assert.All(contexts, row =>
            {
                Assert.Equal(NativeSliceFixture.UserA, row.progress.UserId);
                Assert.Equal("Text", row.context.InputMode);
                Assert.Equal("Application", row.context.ContextType);
            });
            Assert.Single(contexts, row => !row.context.WasCorrect
                && row.progress.VocabularyWordId == fixture.WordId(NativeSliceFixture.UserA, 1));
            Assert.All(await db.VocabularyProgresses.Select(progress => progress.VocabularyWordId)
                .ToListAsync(), id => Assert.StartsWith($"word-{NativeSliceFixture.UserA}-", id));
        }

        // Navigation resets the sentence: Type mode, empty input, no feedback.
        state = await translation.NextAsync();
        Assert.Equal(1, state.Index);
        Assert.Null(state.Feedback);
        Assert.Equal(string.Empty, state.Input);
        Assert.False(state.BlocksMode);
        state = await translation.PreviousAsync();
        Assert.Equal(0, state.Index);
        Assert.Null(state.Feedback);
        for (var i = 0; i < 5; i++)
            state = await translation.NextAsync();

        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(1, state.GradedCount);
        Assert.Equal(85, state.AverageAccuracy);
        Assert.Equal(70, state.AverageFluency);

        state = await translation.ContinueAsync();
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(2, ai.GeneratePrompts.Count);
        Assert.Equal(0, state.GradedCount);

        var exit = await translation.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.True(exit.HasPendingSync);
        // Commands after exit change nothing.
        Assert.Equal(NativeDestination.TodaysPlan, (await translation.NextAsync()).Destination);
        Assert.Equal(NativeDestination.TodaysPlan, (await translation.GradeAsync()).Destination);
        var reloaded = await dashboard.LoadAsync();
        Assert.Contains(reloaded.Cards, item => item.Id == card.Id);
    }

    [Fact]
    public async Task ChooseOwn_WithoutSkillOrFocusShowsNoSentencesWithoutCallingAi()
    {
        using var fixture = new NativeSliceFixture();
        var ai = new ScratchTranslationAi(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var translation = Presenter(fixture, ai);

        var state = await translation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA, skillId: null));

        Assert.Equal(NativePhase.Empty, state.Phase);
        Assert.Equal(NativeTranslationText.NoSentences, state.ErrorKey);
        Assert.Equal(NativeTranslationText.NoSentencesReturned, state.Notice?.Key);
        Assert.Empty(ai.GeneratePrompts);
        Assert.Equal(NativeDestination.TodaysPlan, (await translation.ExitAsync()).Destination);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var adHoc = Assert.Single(await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.PlanItemId.StartsWith("adhoc-")).ToListAsync());
        Assert.Equal(NativeSliceFixture.UserA, adHoc.UserProfileId);
        Assert.Equal(nameof(PlanActivityType.Translation), adHoc.ActivityType);
    }

    [Fact]
    public async Task GenerationFailureAndEmptyReplyAreDistinctStates()
    {
        using var fixture = new NativeSliceFixture();
        await SeedSkillAsync(fixture, NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);

        var failing = new ScratchTranslationAi(NativeSliceFixture.UserA) { GenerateFails = true };
        using (var translation = Presenter(fixture, failing))
        {
            var state = await translation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
            Assert.Equal(NativePhase.Error, state.Phase);
            Assert.Equal(NativeTranslationText.LoadFailed, state.ErrorKey);
            await translation.ExitAsync();
        }

        var empty = new ScratchTranslationAi(NativeSliceFixture.UserA) { SentenceTotal = 0 };
        using (var translation = Presenter(fixture, empty))
        {
            var state = await translation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
            Assert.Equal(NativePhase.Empty, state.Phase);
            Assert.Single(empty.GeneratePrompts);
            await translation.ExitAsync();
        }
    }

    [Fact]
    public async Task GradeFailureOrNoVerdictIsReportedAndRecordsNothing()
    {
        using var fixture = new NativeSliceFixture();
        await SeedSkillAsync(fixture, NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new ScratchTranslationAi(NativeSliceFixture.UserA) { GradeFails = true };
        using var translation = Presenter(fixture, ai);
        await translation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await translation.StartAsync();

        // Blank input is not graded (source GradeMe guard).
        translation.SetInput("   ");
        Assert.Empty((await translation.GradeAsync()).Notice?.Key ?? string.Empty);
        Assert.Empty(ai.GradePrompts);

        translation.SetInput("오늘 가요");
        var failed = await translation.GradeAsync();
        Assert.False(failed.IsGrading);
        Assert.Null(failed.Feedback);
        Assert.Equal(new NativeTranslationNotice(NativeTranslationText.GradeFailed, true), failed.Notice);
        Assert.Equal("오늘 가요", failed.Input);

        ai.GradeFails = false;
        ai.GradeReturnsNull = true;
        var none = await translation.GradeAsync();
        Assert.Equal(NativeTranslationText.GradeFailed, none.Notice?.Key);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.UserActivities.AnyAsync());
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task AbsentForeignOrChangedOwner_FailsClosedWithoutRows()
    {
        using var fixture = new NativeSliceFixture();
        await SeedSkillAsync(fixture, NativeSliceFixture.UserA, wordCount: 3);
        await SeedSkillAsync(fixture, NativeSliceFixture.UserB, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new ScratchTranslationAi(NativeSliceFixture.UserA);

        using (var foreign = Presenter(fixture, ai))
        {
            var state = await foreign.OpenAsync(Launch(fixture, NativeSliceFixture.UserB));
            Assert.Equal(NativePhase.Blocked, state.Phase);
            Assert.Empty(ai.GeneratePrompts);
        }

        // The owner changes while the grade is in flight: nothing is recorded.
        ai.GradeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var translation = Presenter(fixture, ai);
        await translation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await translation.StartAsync();
        translation.SetInput("가요");
        var pending = translation.GradeAsync();
        Assert.True(translation.State.IsGrading);
        Assert.False(translation.State.CanGrade);
        Assert.Equal(0, (await translation.NextAsync()).Index);
        fixture.LoseOwner();
        ai.GradeGate.SetResult(true);
        var blocked = await pending;

        Assert.Equal(NativePhase.Blocked, blocked.Phase);
        Assert.Equal(NativePhase.Blocked, translation.State.Phase);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.UserActivities.AnyAsync());
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.False(await db.DailyPlanCompletions.AnyAsync(row =>
            row.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task ExitDuringGrading_CancelsTheGradeAndRecordsNothing()
    {
        using var fixture = new NativeSliceFixture();
        await SeedSkillAsync(fixture, NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new ScratchTranslationAi(NativeSliceFixture.UserA)
        {
            GradeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var translation = Presenter(fixture, ai);
        await translation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await translation.StartAsync();
        translation.SetInput("가요");
        var pending = translation.GradeAsync();

        var exit = await translation.ExitAsync();
        await pending;

        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Equal(NativeDestination.TodaysPlan, translation.State.Destination);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.UserActivities.AnyAsync());
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ExitDuringAHungSentenceLoad_ReturnsAtOnceAndSavesTheTimerRow()
    {
        using var fixture = new NativeSliceFixture();
        await SeedSkillAsync(fixture, NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var hang = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new ScratchTranslationAi(NativeSliceFixture.UserA) { GenerateHang = hang };
        using var translation = Presenter(fixture, ai);

        var open = translation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        for (var i = 0; i < 500 && ai.GeneratePrompts.Count == 0; i++)
            await Task.Delay(10);
        Assert.Single(ai.GeneratePrompts);
        Assert.Equal(NativePhase.Loading, translation.State.Phase);

        var exit = await translation.ExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        // The abandoned load never reopens the activity.
        hang.SetResult(true);
        await open;
        Assert.Equal(NativeDestination.TodaysPlan, translation.State.Destination);

        using var scope = fixture.Scope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DailyPlanCompletions.AsNoTracking()
            .SingleAsync(item => item.UserProfileId == NativeSliceFixture.UserA);
        Assert.StartsWith("adhoc-", row.PlanItemId);
        Assert.Equal(nameof(PlanActivityType.Translation), row.ActivityType);
    }

    [Fact]
    public async Task Back_ReturnsItsDestinationToTheLoadAndGrade_EvenWhenTheyResumeFirst()
    {
        using var fixture = new NativeSliceFixture();
        await SeedSkillAsync(fixture, NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var hang = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new ScratchTranslationAi(NativeSliceFixture.UserA) { GenerateHang = hang };
        using (var loading = Presenter(fixture, ai))
        {
            var published = new List<NativeTranslationState>();
            loading.StateChanged += published.Add;
            var open = loading.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
            for (var i = 0; i < 500 && ai.GeneratePrompts.Count == 0; i++)
                await Task.Delay(10);
            ExitRace.HoldExitUntil(loading.ExitFence, () =>
            {
                hang.SetResult(true);
                return open;
            });
            Assert.Equal(NativeDestination.TodaysPlan,
                (await loading.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10))).Destination);
            Assert.Equal(NativeDestination.TodaysPlan,
                (await open.WaitAsync(TimeSpan.FromSeconds(10))).Destination);
            Assert.Equal(NativeDestination.TodaysPlan, published[^1].Destination);
        }

        ai.GenerateHang = null;
        ai.GradeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var translation = Presenter(fixture, ai);
        await translation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await translation.StartAsync();
        translation.SetInput("가요");
        var pending = translation.GradeAsync();
        Assert.True(translation.State.IsGrading);
        ExitRace.HoldExitUntil(translation.ExitFence, () => pending);
        Assert.Equal(NativeDestination.TodaysPlan,
            (await translation.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10))).Destination);
        var late = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(NativeDestination.TodaysPlan, late.Destination);
        Assert.False(late.IsGrading);
        using var scope = fixture.Scope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserActivities.AnyAsync());
    }

    private static NativeTranslationPresenter Presenter(NativeSliceFixture fixture, INativeTranslationAi ai) =>
        ActivatorUtilities.CreateInstance<NativeTranslationPresenter>(fixture.Provider, ai);

    private static NativeActivityLaunch Launch(NativeSliceFixture fixture, string owner,
        string? skillId = SkillId) =>
        new(Route, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], skillId, [], false, fixture.ResourceId(owner),
            PlanActivityType.Translation);

    private static async Task SeedSkillAsync(NativeSliceFixture fixture, string owner, int wordCount)
    {
        await fixture.SeedAsync(owner, wordCount);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SkillProfiles.Add(new SkillProfile
        {
            Id = owner == NativeSliceFixture.UserA ? SkillId : $"{SkillId}-{owner}",
            UserProfileId = owner,
            Title = "Wants",
            Description = SkillDescription,
            Language = "Korean"
        });
        await db.SaveChangesAsync();
    }

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);

    private static async Task<(NativePlanCard Card, NativeDashboardPresenter Dashboard)>
        PlanCardAsync(NativeSliceFixture fixture, string owner)
    {
        await SeedSkillAsync(fixture, owner, wordCount: 10);
        fixture.SetPlanActivities(owner,
            (nameof(PlanActivityType.VocabularyReview), null, null, 6, 1),
            (nameof(PlanActivityType.Translation), fixture.ResourceId(owner), SkillId, 10, 2));
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var dashboard = fixture.Dashboard;
        var plan = await dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, plan.Phase);
        return (plan.Cards.Single(card => card.Activity == PlanActivityType.Translation), dashboard);
    }
}

/// <summary>Deterministic Translation AI: sentences use the owner's first words plus one AI-only term.</summary>
internal sealed class ScratchTranslationAi(string owner) : INativeTranslationAi
{
    public List<string> GeneratePrompts { get; } = [];
    public List<string> GradePrompts { get; } = [];
    public string? LastOwner { get; private set; }
    public int SentenceTotal { get; set; } = 5;
    public bool GenerateFails { get; set; }
    public bool GradeFails { get; set; }
    public bool GradeReturnsNull { get; set; }
    public TaskCompletionSource<bool>? GradeGate { get; set; }
    /// <summary>A hung gateway: generation waits for it and ignores cancellation.</summary>
    public TaskCompletionSource<bool>? GenerateHang { get; set; }

    private string Term(int index) => $"target-{owner}-{index}";

    public async Task<TranslationResponse?> GenerateAsync(
        string userId, string renderedPrompt, CancellationToken cancellationToken)
    {
        LastOwner = userId;
        GeneratePrompts.Add(renderedPrompt);
        if (GenerateHang is { } hang)
            await hang.Task;
        if (GenerateFails)
            throw new HttpRequestException("Synthetic generation offline.");
        return new TranslationResponse
        {
            Sentences = Enumerable.Range(0, SentenceTotal).Select(i => new TranslationDto
            {
                SentenceText = $"target sentence {i}",
                RecommendedTranslation = $"I want sentence {i}.",
                TranslationVocabulary = [Term(0), Term(1), "싶다"]
            }).ToList()
        };
    }

    public async Task<GradeResponse?> GradeAsync(
        string userId, string renderedPrompt, CancellationToken cancellationToken)
    {
        LastOwner = userId;
        GradePrompts.Add(renderedPrompt);
        if (GradeGate is not null)
            await GradeGate.Task.WaitAsync(cancellationToken);
        if (GradeFails)
            throw new HttpRequestException("Synthetic grader offline.");
        if (GradeReturnsNull)
            return null;
        return new GradeResponse
        {
            Accuracy = 85,
            Fluency = 70,
            AccuracyExplanation = "Close.",
            FluencyExplanation = "Natural.",
            GrammarNotes = new GrammarNotes
            {
                RecommendedTranslation = "가고 싶어요.",
                Explanation = "Use -고 싶다."
            },
            VocabularyAnalysis =
            [
                new() { UsedForm = Term(0), DictionaryForm = Term(0), UsageCorrect = true },
                new() { UsedForm = Term(1), DictionaryForm = Term(1), UsageCorrect = false },
                new() { UsedForm = "싶어요", DictionaryForm = "싶다", UsageCorrect = true },
            ]
        };
    }
}
