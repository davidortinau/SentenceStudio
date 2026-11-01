using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Video Watching source contract (activities.md 1.1-1.3 and 11) on the shared
/// activity foundation, against real SQLite and the shared repositories.
/// </summary>
public sealed class NativeVideoWatchingPresenterTests
{
    private const string Route = NativeActivityCatalog.VideoWatching;
    private const string VideoId = "dQw4w9WgXcQ";
    private const string Transcript = "안녕하세요. 오늘은 공원에 가요.";

    [Fact]
    public void Catalog_RegistersVideoWatchingForPlanCardsOnly()
    {
        var definition = NativeActivityCatalog.ForRoute(Route);
        Assert.NotNull(definition);
        Assert.Same(definition, NativeActivityCatalog.ForPlanType(PlanActivityType.VideoWatching));
        Assert.Equal(PlanActivityType.VideoWatching, definition.TimerType);
        Assert.True(definition.PlanOnly);
        // VideoWatching.razor passes no skill to ActivityTimer.StartSession.
        Assert.False(definition.TimerRecordsSkill);
        Assert.False(NativeActivityCatalog.IsVocabularyDriven(PlanActivityType.VideoWatching));
        Assert.False(NativeActivityCatalog.TakesResourceList(Route));
        Assert.DoesNotContain(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Route);
    }

    [Fact]
    public async Task PlanCard_ShowsTheVideoTogglesTheTranscriptAndBackSavesOnlyMinutes()
    {
        using var fixture = new NativeSliceFixture();
        var owner = NativeSliceFixture.UserA;
        await SeedVideoAsync(fixture, owner, $"https://www.youtube.com/watch?v={VideoId}&t=42s");
        fixture.SetPlanActivities(owner,
            (nameof(PlanActivityType.VocabularyReview), null, null, 6, 1),
            (nameof(PlanActivityType.VideoWatching), fixture.ResourceId(owner), null, 20, 2));
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var plan = await fixture.Dashboard.GenerateAsync();
        var card = plan.Cards.Single(item => item.Activity == PlanActivityType.VideoWatching);
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(card.Id);

        Assert.NotNull(launch);
        Assert.Equal(Route, launch.Route);
        Assert.Equal(card.Id, launch.PlanItemId);
        Assert.Equal([fixture.ResourceId(owner)], launch.ResourceIds.ToArray());
        Assert.Equal(fixture.ResourceId(owner), launch.TimerResourceId);
        Assert.False(launch.DueOnly);
        Assert.Equal(PlanActivityType.VideoWatching, launch.ActivityType);

        using var video = Presenter(fixture);
        var state = await video.OpenAsync(launch);
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal("Coast video", state.ResourceTitle);
        Assert.Equal(VideoId, state.VideoId);
        Assert.Equal(new Uri($"https://www.youtube.com/watch?v={VideoId}&t=42s"), state.ExternalUri);
        Assert.True(state.HasTranscript);
        Assert.False(state.ShowTranscript);
        Assert.Equal("ko-KR", state.TranscriptLanguageTag);

        Assert.True((await video.ToggleTranscriptAsync()).ShowTranscript);
        Assert.False((await video.ToggleTranscriptAsync()).ShowTranscript);

        var exit = await video.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        Assert.Equal(NativeDestination.TodaysPlan, (await video.ToggleTranscriptAsync()).Destination);

        var (row, _) = await fixture.RowsAsync(owner, card.Id);
        Assert.Equal(nameof(PlanActivityType.VideoWatching), row.ActivityType);
        Assert.Equal(fixture.ResourceId(owner), row.ResourceId);
        Assert.False(row.IsCompleted);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // Source: no UserActivity, no vocabulary progress, no ad-hoc row.
        Assert.False(await db.UserActivities.AnyAsync());
        Assert.False(await db.VocabularyProgresses.AnyAsync());
        Assert.False(await db.DailyPlanCompletions.AnyAsync(item => item.PlanItemId.StartsWith("adhoc-")));
    }

    [Fact]
    public async Task ChooseOwn_IsRefusedBecauseTheSourceHasNoTile()
    {
        using var fixture = new NativeSliceFixture();
        await SeedVideoAsync(fixture, NativeSliceFixture.UserA, $"https://youtu.be/{VideoId}");
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var dashboard = fixture.Dashboard;
        dashboard.SetMode(NativeDashboardMode.ChooseOwn);
        await dashboard.LoadChooseOwnAsync();
        dashboard.SelectChooseOwn([fixture.ResourceId(NativeSliceFixture.UserA)], null);
        var selection = await dashboard.SelectChooseOwnQuizAsync();

        Assert.NotNull(selection);
        Assert.Null(await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromChooseOwnAsync(selection, Route));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ&t=10s")]
    public async Task YouTubeUrls_ExtractTheIdLikeTheSource(string url)
    {
        using var fixture = new NativeSliceFixture();
        await SeedVideoAsync(fixture, NativeSliceFixture.UserA, url);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var video = Presenter(fixture);

        var state = await video.OpenAsync(DirectLaunch(fixture, NativeSliceFixture.UserA));

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(VideoId, state.VideoId);
    }

    [Theory]
    [InlineData("https://vimeo.com/123456")]
    [InlineData("the")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData(null)]
    public async Task NonYouTubeMedia_ShowsTheSourceErrorWithGoBack(string? url)
    {
        using var fixture = new NativeSliceFixture();
        await SeedVideoAsync(fixture, NativeSliceFixture.UserA, url);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var video = Presenter(fixture);

        var state = await video.OpenAsync(DirectLaunch(fixture, NativeSliceFixture.UserA));

        Assert.Equal(NativePhase.Error, state.Phase);
        Assert.Equal(NativeVideoWatchingText.NoVideoId, state.ErrorKey);
        Assert.Null(state.VideoId);
        var exit = await video.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
    }

    [Fact]
    public async Task MissingOrForeignResource_ShowsTheSourceErrors()
    {
        using var fixture = new NativeSliceFixture();
        var owner = NativeSliceFixture.UserA;
        await SeedVideoAsync(fixture, owner, $"https://youtu.be/{VideoId}");
        await SeedVideoAsync(fixture, NativeSliceFixture.UserB, $"https://youtu.be/{VideoId}");
        await SignInAsync(fixture, NativeSliceFixture.EmailA);

        using (var noResource = Presenter(fixture))
        {
            var state = await noResource.OpenAsync(DirectLaunch(fixture, owner) with { ResourceIds = [] });
            Assert.Equal(NativePhase.Error, state.Phase);
            Assert.Equal(NativeVideoWatchingText.NoResource, state.ErrorKey);
            await noResource.ExitAsync();
        }
        using (var foreign = Presenter(fixture))
        {
            var state = await foreign.OpenAsync(DirectLaunch(fixture, owner) with {
                ResourceIds = [fixture.ResourceId(NativeSliceFixture.UserB)] });
            Assert.Equal(NativePhase.Error, state.Phase);
            Assert.Equal(NativeVideoWatchingText.ResourceNotFound, state.ErrorKey);
            Assert.Null(state.VideoId);
            Assert.Null(state.Transcript);
            await foreign.ExitAsync();
        }
    }

    [Fact]
    public async Task NoTranscript_HasNoTranscriptCardAndToggleIsIgnored()
    {
        using var fixture = new NativeSliceFixture();
        await SeedVideoAsync(fixture, NativeSliceFixture.UserA, $"https://youtu.be/{VideoId}", transcript: null);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var video = Presenter(fixture);

        var state = await video.OpenAsync(DirectLaunch(fixture, NativeSliceFixture.UserA));

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.False(state.HasTranscript);
        Assert.False((await video.ToggleTranscriptAsync()).ShowTranscript);
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedAndBackStillReturns()
    {
        using var fixture = new NativeSliceFixture();
        await SeedVideoAsync(fixture, NativeSliceFixture.UserA, $"https://youtu.be/{VideoId}");
        await SeedVideoAsync(fixture, NativeSliceFixture.UserB, $"https://youtu.be/{VideoId}");
        await SignInAsync(fixture, NativeSliceFixture.EmailA);

        using (var refused = Presenter(fixture))
        {
            var state = await refused.OpenAsync(DirectLaunch(fixture, NativeSliceFixture.UserB));
            Assert.Equal(NativePhase.Blocked, state.Phase);
            Assert.Null(state.VideoId);
        }

        using var video = Presenter(fixture);
        var open = await video.OpenAsync(DirectLaunch(fixture, NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Ready, open.Phase);
        fixture.LoseOwner();

        Assert.Equal(NativePhase.Blocked, video.State.Phase);
        Assert.Null(video.State.VideoId);
        Assert.Equal(NativePhase.Blocked, (await video.ToggleTranscriptAsync()).Phase);
        Assert.Equal(NativeDestination.TodaysPlan, (await video.ExitAsync()).Destination);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.DailyPlanCompletions.AnyAsync(row =>
            row.UserProfileId == NativeSliceFixture.UserB));
        Assert.False(await db.UserActivities.AnyAsync());
    }

    [Fact]
    public void VideoService_ResolvesFromAnActivityScopeWithScopedRepositories()
    {
        // Android registers the repositories as scoped; the shared service is
        // transient so the activity scope supplies them.
        using var fixture = new NativeSliceFixture(scopedResourceRepository: true);
        using var scope = fixture.Scope();
        var videos = scope.ServiceProvider.GetRequiredService<SentenceStudio.Services.VideoWatchingService>();

        Assert.Equal(VideoId, videos.ExtractYouTubeVideoId($"https://youtu.be/{VideoId}"));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("http://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ExternalUri_OpensOnlyAbsoluteWebUrls(string? url, bool opens) =>
        Assert.Equal(opens, new NativeVideoWatchingState(NativePhase.Ready, MediaUrl: url).ExternalUri is not null);

    [Theory]
    [InlineData("Show ▼", "Show")]
    [InlineData("숨기기 ▲", "숨기기")]
    [InlineData("↗ Open in YouTube", "Open in YouTube")]
    public void Spoken_DropsTheDecorativeArrows(string label, string expected) =>
        Assert.Equal(expected, NativeVideoWatchingText.Spoken(label));

    private static async Task SeedVideoAsync(NativeSliceFixture fixture, string owner, string? mediaUrl,
        string? transcript = Transcript)
    {
        await fixture.SeedAsync(owner, wordCount: 0);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = await db.LearningResources.SingleAsync(item => item.Id == fixture.ResourceId(owner));
        resource.Title = "Coast video";
        resource.MediaType = "Video";
        resource.MediaUrl = mediaUrl;
        resource.Transcript = transcript;
        await db.SaveChangesAsync();
    }

    private static NativeVideoWatchingPresenter Presenter(NativeSliceFixture fixture) =>
        fixture.Provider.GetRequiredService<NativeVideoWatchingPresenter>();

    /// <summary>
    /// A launch without a plan item (the ad-hoc timer row), used only to drive the
    /// presenter directly; heads never build one because Choose My Own refuses this route.
    /// </summary>
    private static NativeActivityLaunch DirectLaunch(NativeSliceFixture fixture, string owner) =>
        new(Route, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner),
            PlanActivityType.VideoWatching);

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);
}
