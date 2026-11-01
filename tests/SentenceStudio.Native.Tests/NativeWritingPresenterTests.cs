using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Writing source contract (activities.md 1.1-1.4 and 6) on the shared
/// activity foundation, against real SQLite and shared services, with a fake
/// owner-bound AI port.
/// </summary>
public sealed class NativeWritingPresenterTests
{
    private const string Writing = NativeActivityCatalog.Writing;

    [Fact]
    public void Catalog_RegistersWritingForTheWritingPlanType()
    {
        Assert.True(NativeActivityCatalog.IsImplemented(Writing));
        Assert.True(NativeActivityCatalog.IsImplemented(PlanActivityType.Writing));
        var definition = NativeActivityCatalog.ForRoute(Writing)!;
        Assert.Equal(PlanActivityType.Writing, definition.TimerType);
        // Source Writing passes its skill to ActivityTimer.StartSession.
        Assert.True(definition.TimerRecordsSkill);
        Assert.False(NativeActivityCatalog.IsVocabularyDriven(PlanActivityType.Writing));
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Writing);
    }

    [Fact]
    public async Task PlanCard_LaunchKeepsTheResourceAndGradesOwnedSentences()
    {
        using var fixture = new NativeSliceFixture();
        await SeedWordsAsync(fixture, NativeSliceFixture.UserA, 6);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.VocabularyReview), null, null, 6, 1),
            (nameof(PlanActivityType.Writing), fixture.ResourceId(NativeSliceFixture.UserA), null, 10, 2));
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var dashboard = fixture.Dashboard;
        var plan = await dashboard.GenerateAsync();
        var card = plan.Cards.Single(item => item.Activity == PlanActivityType.Writing);
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(card.Id);
        Assert.NotNull(launch);
        Assert.Equal(Writing, launch.Route);
        Assert.Equal([fixture.ResourceId(NativeSliceFixture.UserA)], launch.ResourceIds.ToArray());
        Assert.False(launch.DueOnly);

        var ai = new FakeWritingAi();
        using var writing = Presenter(fixture, ai);
        var state = await writing.OpenAsync(launch);

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(NativeWritingPresenter.BlockCount, state.Blocks.Length);
        Assert.Empty(state.Sentences);
        Assert.False(state.CanGrade);
        Assert.False(state.CanEndSession);

        // Tapping blocks appends target words separated by a space.
        state = await writing.UseBlockAsync(0);
        state = await writing.UseBlockAsync(1);
        Assert.Equal($"{state.Blocks[0].Text} {state.Blocks[1].Text}", state.Draft);
        Assert.True(state.CanGrade);

        var firstWord = WordFor(fixture, state.Blocks[0].Text);
        ai.Analysis = [new VocabularyAnalysis
        {
            DictionaryForm = state.Blocks[0].Text, UsedForm = state.Blocks[0].Text,
            UsageCorrect = true
        }];
        writing.SetDraft("나는 학교에 가요");
        state = await writing.GradeAsync();

        Assert.Equal(NativePhase.Ready, state.Phase);
        var sentence = Assert.Single(state.Sentences);
        Assert.Equal("나는 학교에 가요", sentence.Answer);
        Assert.Equal(82, sentence.Accuracy);
        Assert.Equal(64, sentence.Fluency);
        Assert.Equal("Recommended", sentence.RecommendedSentence);
        Assert.True(sentence.HasDetails);
        Assert.Equal(string.Empty, state.Draft);
        Assert.Contains(state.Notices, notice => notice.Key == NativeWritingText.GradedAccuracy
            && notice.Argument == "82");
        // Source GradeSentence(userInput, ""): no meaning and no target word; profile languages.
        var prompt = Assert.Single(ai.Graded);
        Assert.Equal((NativeSliceFixture.UserA, "나는 학교에 가요", "Korean", "English"), prompt);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var activity = await db.UserActivities.AsNoTracking().SingleAsync();
            Assert.Equal(nameof(Activity.Writer), activity.Activity);
            Assert.Equal(NativeSliceFixture.UserA, activity.UserProfileId);
            Assert.Equal("나는 학교에 가요", activity.Input);
            Assert.Equal(82, activity.Accuracy);
            var attempt = await db.VocabularyLearningContexts.AsNoTracking()
                .Where(context => context.Activity == NativeWritingPresenter.ScoringActivity)
                .Join(db.VocabularyProgresses, context => context.VocabularyProgressId,
                    progress => progress.Id, (context, progress) => new { context, progress })
                .SingleAsync();
            Assert.Equal(NativeSliceFixture.UserA, attempt.progress.UserId);
            Assert.Equal(firstWord, attempt.progress.VocabularyWordId);
            Assert.Equal("Text", attempt.context.InputMode);
            Assert.True(attempt.context.WasCorrect);
        }

        // Three graded sentences unlock End session; the summary averages them.
        ai.Analysis = null;
        foreach (var (text, accuracy, fluency) in new[] { ("둘", 70, 50), ("셋", 64, 72) })
        {
            ai.Accuracy = accuracy;
            ai.Fluency = fluency;
            writing.SetDraft(text);
            state = await writing.GradeAsync();
        }
        Assert.Equal(3, state.Sentences.Length);
        Assert.True(state.CanEndSession);
        state = await writing.EndSessionAsync();
        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(3, state.GradedCount);
        Assert.Equal((82 + 70 + 64) / 3, state.AverageAccuracy);
        Assert.Equal((64 + 50 + 72) / 3, state.AverageFluency);

        state = await writing.ContinueAsync();
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Empty(state.Sentences);
        Assert.Equal(0, state.GradedCount);
        Assert.Equal(4, state.Blocks.Length);

        var exit = await writing.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        Assert.True(exit.HasPendingSync);
        // Commands after exit are ignored.
        writing.SetDraft("late");
        Assert.Equal(NativeDestination.TodaysPlan, (await writing.GradeAsync()).Destination);
        Assert.Equal(3, ai.Graded.Count);
        Assert.Equal(3, await CountUserActivitiesAsync(fixture, NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ChooseOwn_UsesFirstResourceOnlyWordsAndCreatesAnAdHocRow()
    {
        using var fixture = new NativeSliceFixture();
        await SeedWordsAsync(fixture, NativeSliceFixture.UserA, 6, phrases: 2);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var writing = Presenter(fixture, new FakeWritingAi());

        var state = await writing.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(4, state.Blocks.Length);
        // Phrases are filtered out (WritingActivityVocabularyFilter).
        Assert.DoesNotContain(state.Blocks, block => block.Text.StartsWith("phrase", StringComparison.Ordinal));
        for (var i = 0; i < 10; i++)
        {
            state = await writing.ShuffleAsync();
            Assert.Equal(4, state.Blocks.Length);
            Assert.Equal(4, state.Blocks.Select(block => block.WordId).Distinct().Count());
        }
        await writing.ExitAsync();

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(item => item.PlanItemId.StartsWith("adhoc-"));
        Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId);
        Assert.Equal(nameof(PlanActivityType.Writing), row.ActivityType);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA), row.ResourceId);
    }

    [Fact]
    public async Task FocusVocabulary_KeepsTheFirstFourBlocksOnShuffle()
    {
        using var fixture = new NativeSliceFixture();
        await SeedWordsAsync(fixture, NativeSliceFixture.UserA, 6);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var focus = Enumerable.Range(0, 6).Select(i => fixture.WordId(NativeSliceFixture.UserA, i))
            .Reverse().ToArray();
        using var writing = Presenter(fixture, new FakeWritingAi());
        var launch = ChooseOwnLaunch(fixture, NativeSliceFixture.UserA) with
        {
            ResourceIds = [], FocusVocabularyIds = [.. focus]
        };

        var state = await writing.OpenAsync(launch);
        Assert.Equal(focus.Take(4), state.Blocks.Select(block => block.WordId));
        state = await writing.ShuffleAsync();
        Assert.Equal(focus.Take(4), state.Blocks.Select(block => block.WordId));
    }

    [Fact]
    public async Task EmptyStates_ShowSourceWarningsAndGoBack()
    {
        using var fixture = new NativeSliceFixture();
        await SeedWordsAsync(fixture, NativeSliceFixture.UserA, 0);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);

        using (var noResource = Presenter(fixture, new FakeWritingAi()))
        {
            var state = await noResource.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA)
                with { ResourceIds = [] });
            Assert.Equal(NativePhase.Empty, state.Phase);
            Assert.Equal(NativeWritingText.NoVocabulary, state.ErrorKey);
            Assert.Equal(NativeWritingText.SelectResource, Assert.Single(state.Notices).Key);
            Assert.Equal(NativeDestination.TodaysPlan, (await noResource.ExitAsync()).Destination);
        }

        using var emptyResource = Presenter(fixture, new FakeWritingAi());
        var empty = await emptyResource.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Empty, empty.Phase);
        Assert.Equal(NativeWritingText.NoResourceVocabulary, Assert.Single(empty.Notices).Key);
    }

    [Fact]
    public async Task AiFailures_KeepTheDraftAndWriteNothing()
    {
        using var fixture = new NativeSliceFixture();
        await SeedWordsAsync(fixture, NativeSliceFixture.UserA, 4);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeWritingAi { Fail = true };
        using var writing = Presenter(fixture, ai);
        await writing.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));

        writing.SetDraft("문장");
        var state = await writing.GradeAsync();
        Assert.Equal("문장", state.Draft);
        Assert.Empty(state.Sentences);
        Assert.False(state.IsGrading);
        Assert.Equal(NativeWritingText.GradeFailed, Assert.Single(state.Notices).Key);

        ai.Fail = false;
        ai.ReturnNull = true;
        state = await writing.GradeAsync();
        Assert.Equal(NativeWritingText.GradeFailed, Assert.Single(state.Notices).Key);

        ai.Fail = true;
        state = await writing.TranslateAsync();
        Assert.Equal(NativeWritingText.TranslateFailed, Assert.Single(state.Notices).Key);
        Assert.False(state.IsTranslating);
        Assert.Equal(0, await CountUserActivitiesAsync(fixture, NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task Translate_UsesOwnedVocabularyFirstThenAiAndSavesForTheOwner()
    {
        using var fixture = new NativeSliceFixture();
        await SeedWordsAsync(fixture, NativeSliceFixture.UserA, 4);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeWritingAi();
        using var writing = Presenter(fixture, ai);
        var state = await writing.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));

        var block = state.Blocks[0];
        writing.SetDraft(block.Text);
        state = await writing.TranslateAsync();
        var cached = Assert.Single(state.Notices);
        Assert.Equal(NativeWritingNoticeKind.Info, cached.Kind);
        Assert.Null(cached.Key);
        Assert.StartsWith("native-", cached.Argument);
        Assert.Empty(ai.Translated);

        writing.SetDraft("새 문장");
        state = await writing.TranslateAsync();
        Assert.Equal("translated: 새 문장", Assert.Single(state.Notices).Argument);
        Assert.Equal([(NativeSliceFixture.UserA, "새 문장")], ai.Translated);
        // The draft stays for grading.
        Assert.Equal("새 문장", state.Draft);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.NativeLanguageTerm == "새 문장");
        Assert.Equal(NativeSliceFixture.UserA, saved.UserProfileId);
        Assert.Equal("translated: 새 문장", saved.TargetLanguageTerm);
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedWithoutRows()
    {
        using var fixture = new NativeSliceFixture();
        await SeedWordsAsync(fixture, NativeSliceFixture.UserA, 4);
        await SeedWordsAsync(fixture, NativeSliceFixture.UserB, 4);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeWritingAi();

        using (var foreign = Presenter(fixture, ai))
        {
            var state = await foreign.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserB));
            Assert.Equal(NativePhase.Blocked, state.Phase);
        }

        using var writing = Presenter(fixture, ai);
        await writing.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        writing.SetDraft("문장");
        fixture.LoseOwner();
        var blocked = await writing.GradeAsync();
        Assert.Equal(NativePhase.Blocked, blocked.Phase);
        Assert.Empty(ai.Graded);

        // An owner change while the AI call is in flight discards the result.
        fixture.Activate(NativeSliceFixture.UserA);
        using var second = Presenter(fixture, ai);
        await second.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        second.SetDraft("문장");
        ai.OnGrade = fixture.LoseOwner;
        Assert.Equal(NativePhase.Blocked, (await second.GradeAsync()).Phase);

        Assert.Equal(0, await CountUserActivitiesAsync(fixture, NativeSliceFixture.UserA));
        Assert.Equal(0, await CountUserActivitiesAsync(fixture, NativeSliceFixture.UserB));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.DailyPlanCompletions.AnyAsync(row =>
            row.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task WritingAi_RendersTheSourceGradeSentencePromptWithoutTargetWord()
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await SeedWordsAsync(fixture, NativeSliceFixture.UserA, 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = fixture.Provider.GetRequiredService<INativeWritingAi>();

        var grade = await ai.GradeAsync(NativeSliceFixture.UserA, "저는 학생이에요",
            "Korean", "English", CancellationToken.None);

        Assert.NotNull(grade);
        var prompt = Assert.Single(fixture.SentenceGateway.Prompts);
        Assert.Contains("I've written this sentence: \"저는 학생이에요\".", prompt);
        Assert.Contains("I am a student that speaks English, and I'm learning Korean.", prompt);
        Assert.DoesNotContain("which I mean to express", prompt);
        Assert.DoesNotContain("The target word to evaluate", prompt);
    }

    private static NativeWritingPresenter Presenter(NativeSliceFixture fixture, INativeWritingAi ai) =>
        ActivatorUtilities.CreateInstance<NativeWritingPresenter>(fixture.Provider, ai);

    private static NativeActivityLaunch ChooseOwnLaunch(NativeSliceFixture fixture, string owner) =>
        new(Writing, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner),
            PlanActivityType.Writing);

    /// <summary>Seeds words as LexicalUnitType.Word (Writing filters everything else) plus optional phrases.</summary>
    private static async Task SeedWordsAsync(NativeSliceFixture fixture, string owner,
        int words, int phrases = 0)
    {
        await fixture.SeedAsync(owner, wordCount: words + phrases);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        for (var i = 0; i < words + phrases; i++)
        {
            var word = await db.VocabularyWords.SingleAsync(item => item.Id == fixture.WordId(owner, i));
            word.LexicalUnitType = i < words ? LexicalUnitType.Word : LexicalUnitType.Phrase;
            if (i >= words)
                word.TargetLanguageTerm = $"phrase-{owner}-{i}";
        }
        await db.SaveChangesAsync();
    }

    private static string WordFor(NativeSliceFixture fixture, string target)
    {
        using var scope = fixture.Scope();
        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyWords.AsNoTracking().Single(word => word.TargetLanguageTerm == target).Id;
    }

    private static async Task<int> CountUserActivitiesAsync(NativeSliceFixture fixture, string owner)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserActivities.CountAsync(row => row.UserProfileId == owner);
    }

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);

    private sealed class FakeWritingAi : INativeWritingAi
    {
        public List<(string Owner, string Sentence, string Target, string Native)> Graded { get; } = [];
        public List<(string Owner, string Text)> Translated { get; } = [];
        public bool Fail { get; set; }
        public bool ReturnNull { get; set; }
        public double Accuracy { get; set; } = 82;
        public double Fluency { get; set; } = 64;
        public List<VocabularyAnalysis>? Analysis { get; set; }
        public Action? OnGrade { get; set; }

        public Task<GradeResponse?> GradeAsync(string owner, string sentence,
            string targetLanguage, string nativeLanguage, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new HttpRequestException("Synthetic grader offline.");
            Graded.Add((owner, sentence, targetLanguage, nativeLanguage));
            OnGrade?.Invoke();
            return Task.FromResult(ReturnNull ? null : new GradeResponse
            {
                Accuracy = Accuracy,
                Fluency = Fluency,
                AccuracyExplanation = "Accuracy note",
                FluencyExplanation = "Fluency note",
                GrammarNotes = new GrammarNotes
                {
                    RecommendedTranslation = "Recommended",
                    Explanation = "Grammar note"
                },
                VocabularyAnalysis = Analysis
            });
        }

        public Task<string?> TranslateAsync(string owner, string text, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new HttpRequestException("Synthetic translator offline.");
            Translated.Add((owner, text));
            return Task.FromResult<string?>($"translated: {text}");
        }
    }
}
