using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>W3 Dashboard source fidelity (AF-D01, AF-D10, AF-D11, AF-N08).</summary>
public sealed class NativeDashboardSourceFidelityTests
{
    [Fact]
    public async Task NewLearnerTestCountsOwnedWordsNotProgressRows()
    {
        // Source CheckNewUserAsync (Index.razor:778-782): no owned resources and no
        // owned vocabulary words. A progress row on a word the owner does not own
        // (a device never stamps an existing ownerless word) is not a returning learner.
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.AddRange(
                new VocabularyWord
                {
                    Id = "owned-word-a", UserProfileId = NativeSliceFixture.UserA,
                    Language = "Korean", TargetLanguageTerm = "안녕", NativeLanguageTerm = "hello"
                },
                new VocabularyWord
                {
                    Id = "ownerless-word", Language = "Korean",
                    TargetLanguageTerm = "물", NativeLanguageTerm = "water"
                });
            await db.SaveChangesAsync();
        }
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = "progress-b-on-ownerless-word",
                UserId = NativeSliceFixture.UserB,
                VocabularyWordId = "ownerless-word"
            });
            await db.SaveChangesAsync();
        }

        fixture.Activate(NativeSliceFixture.UserA);
        Assert.False((await fixture.Dashboard.LoadAsync()).IsNewUser);

        fixture.Activate(NativeSliceFixture.UserB);
        var progressOnly = await fixture.Dashboard.LoadAsync();
        Assert.True(progressOnly.IsNewUser);
        Assert.Equal(NativePhase.Empty, progressOnly.Phase);
    }

    [Fact]
    public async Task StatisticsPublishWhileThePlanIsStillLoading()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        var published = new List<NativeDashboardState>();
        dashboard.StateChanged += published.Add;

        var ready = await dashboard.LoadAsync();

        Assert.Equal(NativePhase.Ready, ready.Phase);
        Assert.False(ready.StatisticsLoading);
        var first = published.First();
        Assert.Equal(NativePhase.Loading, first.Phase);
        Assert.True(first.StatisticsLoading);
        // Source order: the statistics render before the plan (create-if-absent) ends.
        var statistics = Assert.Single(published, state =>
            state.Phase == NativePhase.Loading && !state.StatisticsLoading);
        Assert.NotNull(statistics.VocabSummary);
        Assert.Equal(4, statistics.VocabSummary!.New);
        Assert.True(statistics.NumbersApplicable);
        Assert.True(published.IndexOf(statistics) < published.IndexOf(ready));
    }

    [Fact]
    public async Task ALoadThatFailsOrIsBlockedBeforeTheStatisticsNeverClaimsNoVocabulary()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;

        // Blocked: no trusted owner, so no statistics were read.
        var blocked = await dashboard.LoadAsync();
        Assert.Equal(NativePhase.Blocked, blocked.Phase);
        Assert.Null(blocked.VocabSummary);
        Assert.Null(blocked.VocabErrorKey);
        Assert.Equal(NativeText.PlanError,
            NativeDashboardPresenter.VocabularySectionErrorKey(blocked));

        // Error: the refresh fails during sync, before the statistics are read.
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.Sync.DuringSynchronization = _ =>
            throw new HttpRequestException("offline");
        var failed = await dashboard.RefreshAsync();
        Assert.Equal(NativePhase.Error, failed.Phase);
        Assert.Equal(NativeText.PlanNetworkError, failed.ErrorKey);
        Assert.Null(failed.VocabSummary);
        Assert.Null(failed.VocabErrorKey);
        Assert.Equal(NativeText.PlanNetworkError,
            NativeDashboardPresenter.VocabularySectionErrorKey(failed));

        // Once the statistics load, they are shown even when a later load fails.
        fixture.Sync.DuringSynchronization = null;
        var ready = await dashboard.LoadAsync();
        Assert.NotNull(ready.VocabSummary);
        Assert.Null(NativeDashboardPresenter.VocabularySectionErrorKey(ready));

        // The section's own states keep their meaning.
        Assert.Null(NativeDashboardPresenter.VocabularySectionErrorKey(
            new NativeDashboardState(NativePhase.Error) { StatisticsLoading = true }));
        Assert.Equal(NativeText.VocabInsightError,
            NativeDashboardPresenter.VocabularySectionErrorKey(
                new NativeDashboardState(NativePhase.Error, ErrorKey: NativeText.PlanError)
                { VocabErrorKey = NativeText.VocabInsightError }));
        Assert.Null(NativeDashboardPresenter.VocabularySectionErrorKey(
            new NativeDashboardState(NativePhase.Ready)));
        Assert.Null(NativeDashboardPresenter.VocabularySectionErrorKey(
            new NativeDashboardState(NativePhase.Empty, ErrorKey: NativeText.NoPlan)));
    }

    [Fact]
    public async Task ChooseOwnChoicesStayVisibleWhileTheDashboardReloads()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        dashboard.SetMode(NativeDashboardMode.ChooseOwn);
        Assert.True(NativeDashboardPresenter.ShowsChooseOwn(await dashboard.LoadAsync()));

        var published = new List<NativeDashboardState>();
        dashboard.StateChanged += published.Add;
        await dashboard.RefreshAsync();
        Assert.All(published.Where(state => state.Phase == NativePhase.Loading),
            state => Assert.True(NativeDashboardPresenter.ShowsChooseOwn(state)));
        Assert.False(NativeDashboardPresenter.ShowsChooseOwn(
            new NativeDashboardState(NativePhase.Loading)));
        Assert.False(NativeDashboardPresenter.ShowsChooseOwn(
            new NativeDashboardState(NativePhase.Error) { ChooseOwn = published[0].ChooseOwn }));
    }

    [Fact]
    public async Task ProfileNameIsReadOnEveryLoadAndIsOwnerScoped()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        Assert.Equal(NativeSliceFixture.UserA, (await dashboard.LoadAsync()).ProfileName);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.UserProfiles.SingleAsync(
                row => row.Id == NativeSliceFixture.UserA);
            profile.Name = "Renamed learner";
            await db.SaveChangesAsync();
        }
        Assert.Equal("Renamed learner", (await dashboard.RefreshAsync()).ProfileName);

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativeSliceFixture.UserB, (await dashboard.LoadAsync()).ProfileName);
        fixture.LoseOwner();
        Assert.Null((await dashboard.LoadAsync()).ProfileName);
    }

    [Fact]
    public async Task StarterSkillIsCreatedOnceOnlyWhenTheOwnerHasNone()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SkillProfiles.Add(
                new SkillProfile
                {
                    Id = "skill-b", UserProfileId = NativeSliceFixture.UserB,
                    Title = "Existing", Language = "Korean"
                });
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .SaveChangesAsync();
        }

        fixture.LoseOwner();
        Assert.False(await fixture.Dashboard.EnsureStarterSkillAsync());

        fixture.Activate(NativeSliceFixture.UserA);
        Assert.True(await fixture.Dashboard.EnsureStarterSkillAsync());
        Assert.True(await fixture.Dashboard.EnsureStarterSkillAsync());
        Assert.True(fixture.Provider.GetRequiredService<NativePendingWrites>()
            .IsPending(NativeSliceFixture.UserA));

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.True(await fixture.Dashboard.EnsureStarterSkillAsync());

        using var verify = fixture.Scope();
        var skills = await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .SkillProfiles.AsNoTracking().ToListAsync();
        var created = Assert.Single(skills, skill =>
            skill.UserProfileId == NativeSliceFixture.UserA);
        Assert.Equal("Korean Basics", created.Title);
        Assert.Equal("Foundational Korean vocabulary and grammar", created.Description);
        Assert.Equal("Korean", created.Language);
        Assert.Equal("Existing", Assert.Single(skills, skill =>
            skill.UserProfileId == NativeSliceFixture.UserB).Title);
    }

    [Fact]
    public async Task MinimalPairsNavigationEntryOpensWithNoResourceOrSkill()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Preferences.Set(ChooseOwnSelectionPreferences.LegacyResourceIdsKey,
            fixture.ResourceId(NativeSliceFixture.UserA));
        fixture.LoseOwner();
        Assert.Null(fixture.Dashboard.SelectNavigationEntry());

        fixture.Activate(NativeSliceFixture.UserA);
        var selection = fixture.Dashboard.SelectNavigationEntry();
        Assert.NotNull(selection);
        Assert.Equal(NativeSliceFixture.UserA, selection.UserId);
        Assert.Empty(selection.ResourceIds);
        Assert.Null(selection.SkillId);

        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromChooseOwnAsync(selection, NativeActivityCatalog.MinimalPairs);
        Assert.NotNull(launch);
        Assert.Equal(NativeActivityCatalog.MinimalPairs, launch.Route);
        Assert.Empty(launch.ResourceIds);
        Assert.Null(launch.PlanItemId);
    }
}
