using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Vocabulary Matching source contract (activities.md 1.1-1.4 and 3) on the
/// shared activity foundation, against real SQLite and shared services.
/// </summary>
public sealed class NativeMatchingPresenterTests
{
    private const string Matching = NativeActivityCatalog.VocabularyMatching;

    [Fact]
    public void Catalog_RegistersOnlyMatchingAndKeepsOtherActivitiesDisabled()
    {
        Assert.True(NativeActivityCatalog.IsImplemented(Matching));
        Assert.True(NativeActivityCatalog.IsImplemented(PlanActivityType.VocabularyGame));
        Assert.False(NativeActivityCatalog.IsImplemented(PlanActivityType.Translation));
        Assert.False(NativeActivityCatalog.IsImplemented("cloze"));
        // The quiz keeps its dedicated launch path.
        Assert.False(NativeActivityCatalog.IsImplemented(PlanActivityType.VocabularyReview));
        Assert.Equal(PlanActivityType.VocabularyGame,
            NativeActivityCatalog.ForRoute(Matching)!.TimerType);
        Assert.All(NativeActivityCatalog.Implemented, definition =>
            Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities,
                tile => tile.Route == definition.Route));
    }

    [Fact]
    public async Task PlanCard_LaunchUsesSourceContextAndRefusesUnimplementedOrForeignCards()
    {
        using var fixture = new NativeSliceFixture();
        var (cards, _) = await SignInWithPlanAsync(fixture, NativeSliceFixture.UserA);
        var launches = fixture.Provider.GetRequiredService<NativeActivityLaunchService>();
        var game = cards.Single(card => card.Activity == PlanActivityType.VocabularyGame);

        var launch = await launches.FromPlanCardAsync(game.Id);

        Assert.NotNull(launch);
        Assert.Equal(Matching, launch.Route);
        Assert.Equal(NativeSliceFixture.UserA, launch.UserId);
        Assert.Equal(game.Id, launch.PlanItemId);
        // Vocabulary-driven plan types omit the resource and set DueOnly.
        Assert.Empty(launch.ResourceIds);
        Assert.True(launch.DueOnly);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA), launch.TimerResourceId);
        Assert.Equal(game.FocusVocabularyIds.ToArray(), launch.FocusVocabularyIds.ToArray());
        // The card's own plan type drives the owner-validated timer (one route can serve several types).
        Assert.Equal(PlanActivityType.VocabularyGame, launch.ActivityType);
        Assert.Null(await launches.FromPlanCardAsync(
            cards.Single(card => card.Activity == PlanActivityType.Translation).Id));
        Assert.Null(await launches.FromPlanCardAsync("not-a-card"));

        fixture.LoseOwner();
        Assert.Null(await launches.FromPlanCardAsync(game.Id));
    }

    [Fact]
    public async Task PlanCard_PlaysEightPairsToCompletionRecordingOwnedAttemptsAndReturns()
    {
        using var fixture = new NativeSliceFixture();
        var (cards, dashboard) = await SignInWithPlanAsync(fixture, NativeSliceFixture.UserA);
        var game = cards.Single(card => card.Activity == PlanActivityType.VocabularyGame);
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(game.Id);
        using var matching = Presenter(fixture);

        var state = await matching.OpenAsync(launch!);

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(NativeMatchingPresenter.PairCount, state.TotalPairs);
        Assert.Equal(16, state.Tiles.Length);
        Assert.True(state.HideNativeWords);
        // Focus words come first: the first eight plan focus words.
        Assert.Equal(game.FocusVocabularyIds.Take(8).Order(),
            state.Tiles.Where(tile => tile.IsTarget).Select(tile => tile.WordId).Order());
        Assert.All(state.Tiles.Where(tile => !tile.IsTarget), tile => Assert.False(tile.IsVisible));

        // Hide-native mode: a native tile can never be the first pick.
        var hiddenNative = state.Tiles.First(tile => !tile.IsTarget);
        state = await matching.TapAsync(hiddenNative.Id);
        Assert.DoesNotContain(state.Tiles, tile => tile.IsSelected);

        // One mismatch: target then the wrong native tile.
        var target = state.Tiles.First(tile => tile.IsTarget);
        state = await matching.TapAsync(target.Id);
        Assert.All(state.Tiles.Where(tile => !tile.IsTarget), tile => Assert.True(tile.IsVisible));
        var wrong = state.Tiles.First(tile => !tile.IsTarget && tile.WordId != target.WordId);
        state = await matching.TapAsync(wrong.Id);
        Assert.Equal(1, state.Misses);
        Assert.Equal(0, state.MatchedPairs);
        Assert.All(state.Tiles.Where(tile => !tile.IsTarget), tile => Assert.False(tile.IsVisible));

        // Deselecting the only target hides native tiles again.
        state = await matching.TapAsync(target.Id);
        Assert.True(state.Tiles.Single(tile => tile.Id == target.Id).IsSelected);
        state = await matching.TapAsync(target.Id);
        Assert.DoesNotContain(state.Tiles, tile => tile.IsSelected);
        Assert.All(state.Tiles.Where(tile => !tile.IsTarget), tile => Assert.False(tile.IsVisible));

        state = await MatchAllAsync(matching, state);

        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(8, state.MatchedPairs);
        Assert.Equal(1, state.Misses);
        Assert.All(state.Tiles, tile => Assert.True(tile.IsVisible && tile.IsMatched));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var contexts = await db.VocabularyLearningContexts.AsNoTracking()
                .Where(context => context.Activity == NativeMatchingPresenter.AttemptActivity)
                .Join(db.VocabularyProgresses, context => context.VocabularyProgressId,
                    progress => progress.Id, (context, progress) => new { context, progress })
                .ToListAsync();
            Assert.Equal(9, contexts.Count);
            Assert.All(contexts, row =>
            {
                Assert.Equal(NativeSliceFixture.UserA, row.progress.UserId);
                Assert.Equal("MultipleChoice", row.context.InputMode);
                Assert.Equal("Isolated", row.context.ContextType);
            });
            Assert.Equal(1, contexts.Count(row => !row.context.WasCorrect
                && row.progress.VocabularyWordId == target.WordId));
            Assert.Equal(8, contexts.Count(row => row.context.WasCorrect));
        }

        var exit = await matching.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Equal(NativePhase.Ready, exit.Phase);
        Assert.True(exit.HasPendingSync);
        var reloaded = await dashboard.LoadAsync();
        Assert.Equal(NativePhase.Ready, reloaded.Phase);
        Assert.Contains(reloaded.Cards, card => card.Id == game.Id);
    }

    [Fact]
    public async Task PlanCard_ResumesSavedMinutesAndExitSavesTheTimerRow()
    {
        using var fixture = new NativeSliceFixture();
        var (cards, dashboard) = await SignInWithPlanAsync(fixture, NativeSliceFixture.UserA);
        var game = cards.Single(card => card.Activity == PlanActivityType.VocabularyGame);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            Assert.True(await lease.Service.UpdateProgressAsync(
                dashboard.State.PlanDate!.Value, game.Id, 3));
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(game.Id);
        using var matching = Presenter(fixture);

        Assert.Equal(NativePhase.Ready, (await matching.OpenAsync(launch!)).Phase);
        await matching.ExitAsync();

        var (row, _) = await fixture.RowsAsync(NativeSliceFixture.UserA, game.Id);
        // The validated timer resumes from 3 saved minutes; exit saves, never resets.
        Assert.Equal(3, row.MinutesSpent);
        Assert.False(row.IsCompleted);
        var reloaded = await dashboard.LoadAsync();
        Assert.Equal(3, reloaded.Cards.Single(card => card.Id == game.Id).MinutesSpent);
    }

    [Fact]
    public async Task ChooseOwnTile_RevalidatesSelectionAndCreatesOwnersAdHocRow()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 3);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var dashboard = fixture.Dashboard;
        dashboard.SetMode(NativeDashboardMode.ChooseOwn);
        await dashboard.LoadChooseOwnAsync();
        dashboard.SelectChooseOwn([fixture.ResourceId(NativeSliceFixture.UserA)], null);
        var selection = await dashboard.SelectChooseOwnQuizAsync();
        var launches = fixture.Provider.GetRequiredService<NativeActivityLaunchService>();

        var launch = await launches.FromChooseOwnAsync(selection!, Matching);

        Assert.NotNull(launch);
        Assert.Null(launch.PlanItemId);
        Assert.Equal([fixture.ResourceId(NativeSliceFixture.UserA)], launch.ResourceIds.ToArray());
        Assert.False(launch.DueOnly);
        Assert.Null(await launches.FromChooseOwnAsync(selection!, "cloze"));
        // A foreign resource is refused even if a stale selection carries it.
        Assert.Null(await launches.FromChooseOwnAsync(selection! with {
            ResourceIds = [fixture.ResourceId(NativeSliceFixture.UserB)] }, Matching));

        using var matching = Presenter(fixture);
        var state = await matching.OpenAsync(launch);
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(3, state.TotalPairs);
        state = await MatchAllAsync(matching, state);
        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(NativeDestination.TodaysPlan, (await matching.ExitAsync()).Destination);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var adHoc = await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.PlanItemId.StartsWith("adhoc-")).ToListAsync();
        var row = Assert.Single(adHoc);
        Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId);
        Assert.Equal(nameof(PlanActivityType.VocabularyGame), row.ActivityType);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA), row.ResourceId);
        // Source VocabMatching starts its timer without the skill.
        Assert.Null(row.SkillId);
        Assert.Equal(10, row.EstimatedMinutes);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
        Assert.Equal(3, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedWithoutTimerRowsOrAttempts()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var foreign = ChooseOwnLaunch(fixture, NativeSliceFixture.UserB);

        using (var refused = Presenter(fixture))
        {
            var state = await refused.OpenAsync(foreign);
            Assert.Equal(NativePhase.Blocked, state.Phase);
            Assert.Equal(NativeMatchingText.Unavailable, state.ErrorKey);
        }

        using var matching = Presenter(fixture);
        var open = await matching.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Ready, open.Phase);
        var target = open.Tiles.First(tile => tile.IsTarget);
        await matching.TapAsync(target.Id);
        fixture.LoseOwner();
        var native = open.Tiles.First(tile => !tile.IsTarget && tile.WordId == target.WordId);
        var blocked = await matching.TapAsync(native.Id);

        Assert.Equal(NativePhase.Blocked, blocked.Phase);
        Assert.Equal(NativePhase.Blocked, matching.State.Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
        Assert.Equal(NativeDestination.TodaysPlan, (await matching.ExitAsync()).Destination);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.DailyPlanCompletions.CountAsync(row =>
            row.PlanItemId.StartsWith("adhoc-")));
        Assert.False(await db.DailyPlanCompletions.AnyAsync(row =>
            row.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task ShowAllMode_AllowsNativeFirstAndHideNativeRestoresTheTargetFirstRule()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var matching = Presenter(fixture);
        var state = await matching.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));

        state = await matching.ToggleHideNativeAsync();
        Assert.False(state.HideNativeWords);
        Assert.All(state.Tiles, tile => Assert.True(tile.IsVisible));
        var native = state.Tiles.First(tile => !tile.IsTarget);
        state = await matching.TapAsync(native.Id);
        Assert.True(state.Tiles.Single(tile => tile.Id == native.Id).IsSelected);
        var target = state.Tiles.Single(tile => tile.IsTarget && tile.WordId == native.WordId);
        state = await matching.TapAsync(target.Id);
        Assert.Equal(1, state.MatchedPairs);
        Assert.All(state.Tiles, tile => Assert.True(tile.IsVisible));

        state = await matching.ToggleHideNativeAsync();
        Assert.True(state.HideNativeWords);
        Assert.All(state.Tiles.Where(tile => !tile.IsTarget && !tile.IsMatched),
            tile => Assert.False(tile.IsVisible));
        // Matched tiles stay visible (faded) after hiding.
        Assert.True(state.Tiles.Single(tile => tile.Id == native.Id).IsVisible);
        var unmatchedNative = state.Tiles.First(tile => !tile.IsTarget && !tile.IsMatched);
        state = await matching.TapAsync(unmatchedNative.Id);
        Assert.DoesNotContain(state.Tiles, tile => tile.IsSelected);
    }

    [Fact]
    public async Task EmptyVocabulary_ShowsEmptyStateAndNewGameReloadsAFreshSet()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using (var empty = Presenter(fixture))
        {
            var state = await empty.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
            Assert.Equal(NativePhase.Empty, state.Phase);
            Assert.Equal(NativeMatchingText.NoVocabulary, state.ErrorKey);
            Assert.Equal(NativeDestination.TodaysPlan, (await empty.ExitAsync()).Destination);
        }

        using var second = new NativeSliceFixture();
        await second.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(second, NativeSliceFixture.EmailA);
        using var matching = Presenter(second);
        var played = await MatchAllAsync(matching,
            await matching.OpenAsync(ChooseOwnLaunch(second, NativeSliceFixture.UserA)));
        Assert.Equal(NativePhase.Summary, played.Phase);

        var restarted = await matching.RestartAsync();

        Assert.Equal(NativePhase.Ready, restarted.Phase);
        Assert.Equal(0, restarted.MatchedPairs);
        Assert.Equal(0, restarted.Misses);
        Assert.Equal(3, restarted.TotalPairs);
        Assert.DoesNotContain(restarted.Tiles, tile => tile.IsMatched);
        Assert.True(second.Provider.GetRequiredService<NativeActivityOwner>()
            .IsActive(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task PairCheck_WaitsForTheSourceDelayAndADeselectDuringItCancelsTheAttempt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var matching = fixture.Provider.GetRequiredService<NativeMatchingPresenter>();
        Assert.Equal(TimeSpan.FromMilliseconds(600), matching.CheckDelay);
        var state = await matching.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        var target = state.Tiles.First(tile => tile.IsTarget);
        var native = state.Tiles.Single(tile => !tile.IsTarget && tile.WordId == target.WordId);

        await matching.TapAsync(target.Id);
        var pending = matching.TapAsync(native.Id);
        Assert.True(matching.State.IsChecking);
        Assert.Equal(0, matching.State.MatchedPairs);
        // Tapping a selected tile during the delay deselects it, as in the source.
        var deselected = await matching.TapAsync(native.Id);
        Assert.False(deselected.IsChecking);
        await pending;
        Assert.Equal(0, matching.State.MatchedPairs);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        // The target is still selected; picking its native tile now completes the check.
        var checkedState = await matching.TapAsync(native.Id);
        Assert.False(checkedState.IsChecking);
        Assert.Equal(1, checkedState.MatchedPairs);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task Timer_RefusesAPlanTypeTheRouteDoesNotServe()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var matching = Presenter(fixture);

        var state = await matching.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA)
            with { ActivityType = PlanActivityType.Translation });

        Assert.Equal(NativePhase.Blocked, state.Phase);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.PlanItemId.StartsWith("adhoc-")).ToListAsync());
    }

    [Fact]
    public async Task AfterExit_TapsToggleAndRestartChangeNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var matching = Presenter(fixture);
        var state = await matching.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        var target = state.Tiles.First(tile => tile.IsTarget);
        var native = state.Tiles.Single(tile => !tile.IsTarget && tile.WordId == target.WordId);
        await matching.TapAsync(target.Id);

        var exited = await matching.ExitAsync();
        await matching.TapAsync(native.Id);
        await matching.ToggleHideNativeAsync();
        await matching.RestartAsync();

        Assert.Equal(NativeDestination.TodaysPlan, matching.State.Destination);
        Assert.Equal(exited, matching.State);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    private static NativeMatchingPresenter Presenter(NativeSliceFixture fixture)
    {
        var presenter = fixture.Provider.GetRequiredService<NativeMatchingPresenter>();
        presenter.CheckDelay = TimeSpan.Zero;
        return presenter;
    }

    private static NativeActivityLaunch ChooseOwnLaunch(NativeSliceFixture fixture, string owner) =>
        new(Matching, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner));

    private static async Task<NativeMatchingState> MatchAllAsync(
        NativeMatchingPresenter matching, NativeMatchingState state)
    {
        while (state.Phase == NativePhase.Ready)
        {
            var target = state.Tiles.First(tile => tile.IsTarget && !tile.IsMatched);
            state = await matching.TapAsync(target.Id);
            var native = state.Tiles.Single(tile =>
                !tile.IsTarget && tile.WordId == target.WordId);
            Assert.True(native.IsVisible);
            state = await matching.TapAsync(native.Id);
        }
        return state;
    }

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);

    private static async Task<(IReadOnlyList<NativePlanCard> Cards, NativeDashboardPresenter Dashboard)>
        SignInWithPlanAsync(NativeSliceFixture fixture, string owner)
    {
        await fixture.SeedAsync(owner, wordCount: 10);
        fixture.SetPlanActivities(owner,
            (nameof(PlanActivityType.VocabularyReview), null, null, 6, 1),
            (nameof(PlanActivityType.VocabularyGame),
                fixture.ResourceId(owner), null, 5, 2),
            (nameof(PlanActivityType.Translation),
                fixture.ResourceId(owner), null, 10, 3));
        await SignInAsync(fixture, owner == NativeSliceFixture.UserA
            ? NativeSliceFixture.EmailA : NativeSliceFixture.EmailB);
        var dashboard = fixture.Dashboard;
        var plan = await dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, plan.Phase);
        return (plan.Cards, dashboard);
    }
}
