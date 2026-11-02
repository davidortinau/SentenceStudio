using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Flashcard Preview source contract (activities.md 16, FlashcardActivity.razor)
/// against real SQLite: cards from the owner's cached plan PreviewWords, flip,
/// answer, counter/tallies, summary, empty state, owner refusal, Back during the
/// throw, and no side-effect rows (no timer, progress, session or activity).
/// </summary>
public sealed class NativeFlashcardPresenterTests
{
    [Fact]
    public void Catalog_KeepsFlashcardsOutOfPlanTypesTilesAndTimers()
    {
        Assert.Equal("flashcard-activity", NativeActivityCatalog.FlashcardPreview);
        Assert.False(NativeActivityCatalog.IsImplemented(NativeActivityCatalog.FlashcardPreview));
        Assert.DoesNotContain(NativeDashboardPresenter.ChooseOwnActivities,
            tile => tile.Route == NativeActivityCatalog.FlashcardPreview);
    }

    [Theory]
    [InlineData(0d, NativeFlashcardRelease.Flip)]
    [InlineData(12d, NativeFlashcardRelease.Flip)]
    [InlineData(-12.5d, NativeFlashcardRelease.None)]
    [InlineData(109.9d, NativeFlashcardRelease.None)]
    [InlineData(110d, NativeFlashcardRelease.Correct)]
    [InlineData(-110d, NativeFlashcardRelease.Incorrect)]
    public void Gesture_UsesSourceSwipeAndTapThresholds(double delta, NativeFlashcardRelease expected)
    {
        Assert.Equal(expected, NativeFlashcardGesture.Classify(delta));
        Assert.Equal(18d, NativeFlashcardGesture.RotationDegrees(1000));
        Assert.Equal(-2d, NativeFlashcardGesture.RotationDegrees(-36));
    }

    [Fact]
    public async Task Launch_IsOwnerBoundWithoutPlanItemOrTimerContext()
    {
        using var fixture = new NativeSliceFixture();
        var (dashboard, _) = await SignInWithPreviewAsync(fixture, NativeSliceFixture.UserA);
        var launches = fixture.Provider.GetRequiredService<NativeActivityLaunchService>();

        var launch = launches.FromPlanPreview(dashboard.PlanDate!.Value);

        Assert.NotNull(launch);
        Assert.Equal(NativeActivityCatalog.FlashcardPreview, launch.Route);
        Assert.Equal(NativeSliceFixture.UserA, launch.UserId);
        Assert.Equal(dashboard.PlanDate, launch.PlanDate);
        Assert.False(launch.IsPlanItem);
        Assert.Empty(launch.ResourceIds);
        Assert.Null(launch.TimerResourceId);
        Assert.Null(launch.ActivityType);

        fixture.LoseOwner();
        Assert.Null(launches.FromPlanPreview(dashboard.PlanDate!.Value));
    }

    [Fact]
    public async Task PreviewWords_FlipAnswerAndSummarizeInMemoryWithoutRows()
    {
        using var fixture = new NativeSliceFixture();
        var (dashboard, words) = await SignInWithPreviewAsync(fixture, NativeSliceFixture.UserA);
        // The Dashboard insight sees the same preview words the button opens.
        Assert.Equal(3, dashboard.Narrative?.VocabInsight?.PreviewWords.Count);
        var before = await SideEffectsAsync(fixture, NativeSliceFixture.UserA);
        using var flashcards = Presenter(fixture);

        var state = await flashcards.OpenAsync(Launch(fixture, dashboard));

        Assert.Equal(NativePhase.Ready, state.Phase);
        // A preview word without a native term is skipped (source filter).
        Assert.Equal(3, state.Total);
        Assert.Equal(words.Select(word => word.Target), state.Cards.Select(card => card.TargetTerm));
        Assert.Equal(0, state.CurrentIndex);
        Assert.False(state.IsFlipped);
        Assert.Equal("사과", state.Current!.TargetTerm);

        state = await flashcards.FlipAsync();
        Assert.True(state.IsFlipped);
        Assert.Equal("apple", state.Current!.NativeTerm);
        state = await flashcards.FlipAsync();
        Assert.False(state.IsFlipped);
        state = await flashcards.FlipAsync();

        state = await flashcards.AnswerAsync(false);
        Assert.Equal(1, state.CurrentIndex);
        Assert.False(state.IsFlipped);
        Assert.Equal(0, state.CorrectCount);
        Assert.Equal(1, state.IncorrectCount);

        state = await flashcards.AnswerAsync(true);
        state = await flashcards.AnswerAsync(true);

        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(2, state.CorrectCount);
        Assert.Equal(1, state.IncorrectCount);
        Assert.Equal(new[] { false, true, true }, state.Results.Select(result => result.WasCorrect));
        Assert.Equal(words.Select(word => word.Target),
            state.Results.Select(result => result.Card.TargetTerm));
        // Answers after the last card are ignored.
        Assert.Equal(NativePhase.Summary, (await flashcards.AnswerAsync(true)).Phase);

        state = await flashcards.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, state.Destination);
        Assert.Equal(before, await SideEffectsAsync(fixture, NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task NoPreviewWordsOrAnotherDate_ShowsSourceEmptyState()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 4);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var plan = await fixture.Dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, plan.Phase);
        Assert.Empty(plan.Narrative?.VocabInsight?.PreviewWords ?? []);

        using (var flashcards = Presenter(fixture))
        {
            var state = await flashcards.OpenAsync(Launch(fixture, plan));
            Assert.Equal(NativePhase.Empty, state.Phase);
            Assert.Equal(NativeFlashcardText.NoVocabulary, state.ErrorKey);
            Assert.Equal(NativeDestination.TodaysPlan, (await flashcards.ExitAsync()).Destination);
        }

        await WritePreviewAsync(fixture, NativeSliceFixture.UserA, ("사과", "apple"));
        using var other = Presenter(fixture);
        var launch = Launch(fixture, plan) with { PlanDate = plan.PlanDate!.Value.AddDays(-1) };
        Assert.Equal(NativePhase.Empty, (await other.OpenAsync(launch)).Phase);
    }

    [Fact]
    public async Task OwnerChange_BlocksAndForeignLaunchIsRefused()
    {
        using var fixture = new NativeSliceFixture();
        var (dashboard, _) = await SignInWithPreviewAsync(fixture, NativeSliceFixture.UserA);
        using var flashcards = Presenter(fixture);
        var state = await flashcards.OpenAsync(Launch(fixture, dashboard));
        Assert.Equal(NativePhase.Ready, state.Phase);

        fixture.LoseOwner();

        Assert.Equal(NativePhase.Blocked, flashcards.State.Phase);
        Assert.Equal(NativePhase.Blocked, (await flashcards.FlipAsync()).Phase);
        Assert.Equal(NativePhase.Blocked, (await flashcards.AnswerAsync(true)).Phase);
        Assert.Equal(NativeDestination.TodaysPlan, (await flashcards.ExitAsync()).Destination);

        fixture.Activate(NativeSliceFixture.UserA);
        using var foreign = Presenter(fixture);
        var refused = await foreign.OpenAsync(Launch(fixture, dashboard) with
        {
            UserId = NativeSliceFixture.UserB
        });
        Assert.Equal(NativePhase.Blocked, refused.Phase);
        Assert.Empty(refused.Cards.IsDefault ? [] : refused.Cards);
    }

    [Fact]
    public async Task BackDuringTheThrow_ReturnsAtOnceAndDropsTheAnswer()
    {
        using var fixture = new NativeSliceFixture();
        var (dashboard, _) = await SignInWithPreviewAsync(fixture, NativeSliceFixture.UserA);
        using var flashcards = Presenter(fixture);
        flashcards.AnswerDelay = TimeSpan.FromMinutes(5);
        await flashcards.OpenAsync(Launch(fixture, dashboard));

        var answer = flashcards.AnswerAsync(true);
        Assert.True(flashcards.State.IsAnimating);
        Assert.True(flashcards.State.PendingAnswer);
        // A second answer or flip during the throw is ignored (buttons disabled).
        Assert.True((await flashcards.FlipAsync()).IsAnimating);
        Assert.False((await flashcards.FlipAsync()).IsFlipped);

        var watch = Stopwatch.StartNew();
        var exited = await flashcards.ExitAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(NativeDestination.TodaysPlan, exited.Destination);
        Assert.False(exited.IsAnimating);

        flashcards.Dispose();
        var late = await answer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, late.CorrectCount);
        Assert.Equal(NativeDestination.TodaysPlan, flashcards.State.Destination);
    }

    [Fact]
    public async Task Back_ReturnsItsDestinationToTheThrow_EvenWhenTheThrowResumesFirst()
    {
        using var fixture = new NativeSliceFixture();
        var (dashboard, _) = await SignInWithPreviewAsync(fixture, NativeSliceFixture.UserA);
        using var flashcards = Presenter(fixture);
        await flashcards.OpenAsync(Launch(fixture, dashboard));
        var published = new List<NativeFlashcardState>();
        flashcards.StateChanged += published.Add;
        flashcards.AnswerDelay = TimeSpan.FromMilliseconds(100);

        var answer = flashcards.AnswerAsync(true);
        // Back is held between its start and its gate until the throw has resumed.
        ExitRace.HoldExitUntil(flashcards.ExitFence, () => answer);
        var exited = await flashcards.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var late = await answer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeDestination.TodaysPlan, exited.Destination);
        Assert.Equal(NativeDestination.TodaysPlan, late.Destination);
        Assert.Equal(0, late.CorrectCount);
        Assert.Equal(NativeDestination.TodaysPlan, published[^1].Destination);
        Assert.Single(published, state => state.Destination == NativeDestination.TodaysPlan);
    }

    private static NativeFlashcardPresenter Presenter(NativeSliceFixture fixture)
    {
        var presenter = fixture.Provider.GetRequiredService<NativeFlashcardPresenter>();
        presenter.AnswerDelay = TimeSpan.Zero;
        return presenter;
    }

    private static NativeActivityLaunch Launch(NativeSliceFixture fixture, NativeDashboardState dashboard) =>
        fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanPreview(dashboard.PlanDate!.Value)!;

    private static async Task<(NativeDashboardState Dashboard, (string Target, string Native)[] Words)>
        SignInWithPreviewAsync(NativeSliceFixture fixture, string owner)
    {
        await fixture.SeedAsync(owner, wordCount: 4);
        await SignInAsync(fixture, owner == NativeSliceFixture.UserA
            ? NativeSliceFixture.EmailA : NativeSliceFixture.EmailB);
        Assert.Equal(NativePhase.Ready, (await fixture.Dashboard.GenerateAsync()).Phase);
        (string, string)[] words = [("사과", "apple"), ("물", "water"), ("책", "book")];
        await WritePreviewAsync(fixture, owner, [.. words, ("빈칸", " ")]);
        var dashboard = await fixture.Dashboard.LoadAsync();
        Assert.Equal(NativePhase.Ready, dashboard.Phase);
        return (dashboard, words);
    }

    /// <summary>Stores PreviewWords in the owner's plan NarrativeFacts (PlanFactsSerializer camelCase wire format).</summary>
    private static async Task WritePreviewAsync(
        NativeSliceFixture fixture, string owner, params (string Target, string Native)[] words)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan = await db.DailyPlans.SingleAsync(row => row.UserProfileId == owner);
        plan.NarrativeFacts = System.Text.Json.JsonSerializer.Serialize(new
        {
            story = "synthetic",
            focusAreas = Array.Empty<string>(),
            resources = Array.Empty<object>(),
            vocabInsight = new
            {
                totalDue = words.Length,
                reviewCount = 0,
                newCount = words.Length,
                averageMastery = 0f,
                strugglingCategories = Array.Empty<object>(),
                sampleStrugglingWords = Array.Empty<string>(),
                previewWords = words.Select((word, index) => new
                {
                    wordId = fixture.WordId(owner, index),
                    targetTerm = word.Target,
                    nativeTerm = word.Native,
                }).ToArray(),
            },
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> SideEffectsAsync(NativeSliceFixture fixture, string owner)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var completions = await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.UserProfileId == owner)
            .OrderBy(row => row.PlanItemId)
            .Select(row => $"{row.PlanItemId}:{row.MinutesSpent}:{row.IsCompleted}")
            .ToListAsync();
        var activities = await db.UserActivities.AsNoTracking()
            .CountAsync(row => row.UserProfileId == owner);
        var sessions = await db.ActivitySessions.AsNoTracking().CountAsync();
        var contexts = await db.VocabularyLearningContexts.AsNoTracking().CountAsync();
        return $"{string.Join(",", completions)}|{activities}|{sessions}|{contexts}";
    }

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);
}
