using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Shared.Models;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.ClosedCaptions;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// YouTube import (source MediaImport.razor): the single video flow through the shared YouTube
/// services on the owner's device database, and that an absent or another owner gets nothing and
/// writes nothing.
/// </summary>
public sealed class NativeYouTubeImportTests
{
    private const string A = NativeSliceFixture.UserA;
    private const string B = NativeSliceFixture.UserB;
    private const string Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    // ---- Task 3: the single video flow ----

    [Fact]
    public async Task SingleVideo_FetchPolishSave_WritesTheOwnersVideoResource_AndTheOtherOwnerSeesNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        fixture.Activate(A);
        var youtube = new ScratchYouTube();
        var ai = new NativeSharedAiTests.ScratchOwnerAi { Polished = "첫 문장입니다.\n\n둘째 문장입니다." };
        using var import = Coordinator(fixture, Adapter(fixture, youtube, ai));
        Assert.True(await import.OpenAsync(A));
        Assert.Equal(YouTubeImportTab.Channels, import.State.Tab);
        import.SwitchTab(YouTubeImportTab.SingleVideo);

        Assert.False(import.State.CanFetch);
        import.SetVideoUrl(Url);
        Assert.True(import.State.CanFetch);
        await import.FetchTranscriptsAsync();
        var fetched = import.State;
        Assert.Null(fetched.Error);
        Assert.Equal(2, fetched.Tracks.Length);
        // The profile's Korean picks the Korean track (source preferred language "ko").
        Assert.Equal(1, fetched.SelectedTrack);
        // SmartCleanup ran on the Korean captions (no trailing blank line is left).
        Assert.StartsWith("첫 문장입니다", fetched.TranscriptText);
        Assert.EndsWith("둘째 문장입니다", fetched.TranscriptText);
        Assert.Equal(["ko"], youtube.Downloads);
        Assert.False(fetched.IsLoadingTranscripts || fetched.IsLoadingTranscript);

        await import.PolishAsync();
        Assert.Equal("첫 문장입니다.\n\n둘째 문장입니다.", import.State.TranscriptText);
        Assert.Contains(import.State.Messages, message => message.Key == "Import_TranscriptPolished");
        Assert.Equal([A], ai.Owners);

        import.SetTranscriptText("첫 문장입니다.\n\n둘째 문장입니다. (edited)");
        await import.SaveAsResourceAsync();
        var saved = import.State;
        Assert.NotNull(saved.SavedResourceId);
        Assert.Contains(saved.Messages, message => message.Key == "Import_ResourceSaved");
        Assert.False(import.HasUnsavedWork);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var resource = await db.LearningResources.AsNoTracking()
                .SingleAsync(row => row.Id == saved.SavedResourceId);
            Assert.Equal(A, resource.UserProfileId);
            Assert.Equal("A short Korean video", resource.Title);
            Assert.Equal("Video", resource.MediaType);
            Assert.Equal(Url, resource.MediaUrl);
            // Source NormalizeLanguageName: the track name without its "(...)" note.
            Assert.Equal("Korean", resource.Language);
            Assert.Equal("첫 문장입니다.\n\n둘째 문장입니다. (edited)", resource.Transcript);
            Assert.False(await db.LearningResources.AnyAsync(row => row.UserProfileId == B));
        }
        var pending = fixture.Provider.GetRequiredService<NativePendingWrites>();
        Assert.True(pending.IsPending(A));
        Assert.False(pending.IsPending(B));

        // Import Another: back to an empty link.
        import.Reset();
        Assert.Equal(string.Empty, import.State.VideoUrl);
        Assert.False(import.State.HasTracks);
        Assert.Null(import.State.SavedResourceId);
    }

    [Fact]
    public async Task AnotherOwnersCalls_AreRefused_AndWriteNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        fixture.Activate(B);
        var youtube = new ScratchYouTube();
        var ai = new NativeSharedAiTests.ScratchOwnerAi { Polished = "x" };
        var adapter = Adapter(fixture, youtube, ai);

        // The device belongs to B: every call for A is refused before it runs.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.SaveResourceAsync(A, new("t", Url, "text", "Korean"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.GetTracksAsync(A, Url, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.PolishAsync(A, "text", "ko", default));
        Assert.Equal(0, youtube.Calls);
        Assert.Empty(ai.Owners);
        using (var scope = fixture.Scope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .LearningResources.AsNoTracking().ToListAsync());

        // A screen opened for A on B's device opens nothing.
        using var screen = Coordinator(fixture, adapter);
        Assert.False(await screen.OpenAsync(A));
    }

    [Fact]
    public async Task AnOwnerChangeBeforeTheSave_BlocksTheScreen_AndWritesNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        fixture.Activate(A);
        using var import = Coordinator(fixture,
            Adapter(fixture, new ScratchYouTube(), new NativeSharedAiTests.ScratchOwnerAi { Polished = "x" }));
        Assert.True(await import.OpenAsync(A));
        import.SwitchTab(YouTubeImportTab.SingleVideo);
        import.SetVideoUrl(Url);
        await import.FetchTranscriptsAsync();
        Assert.True(import.State.HasTranscript);

        fixture.Activate(B);
        await import.SaveAsResourceAsync();

        Assert.True(import.State.Blocked);
        Assert.Null(import.State.SavedResourceId);
        using var scope = fixture.Scope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .LearningResources.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task NoTracks_TrackFailure_AndPolishFailure_SayWhatHappened()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var youtube = new ScratchYouTube { Tracks = [] };
        var ai = new NativeSharedAiTests.ScratchOwnerAi { Failure = new HttpRequestException("gateway down") };
        using var import = Coordinator(fixture, Adapter(fixture, youtube, ai));
        Assert.True(await import.OpenAsync(A));
        import.SwitchTab(YouTubeImportTab.SingleVideo);
        import.SetVideoUrl(Url);

        await import.FetchTranscriptsAsync();
        Assert.Equal("Import_NoTranscripts", import.State.Error?.Key);
        Assert.False(import.State.HasTracks);

        youtube.Tracks = null;
        youtube.TrackFailure = new VideoImportException(
            "This video is not accessible. It may be private, age-restricted, region-locked, or removed.",
            "VideoUnavailable");
        await import.FetchTranscriptsAsync();
        Assert.Equal("YouTubeImport_FetchTranscriptsError", import.State.Error?.Key);
        Assert.StartsWith("This video is not accessible.", import.State.Error?.Argument);
        // Switching tabs clears the error (source SwitchTab).
        import.SwitchTab(YouTubeImportTab.History);
        Assert.Null(import.State.Error);
        import.SwitchTab(YouTubeImportTab.SingleVideo);

        youtube.TrackFailure = null;
        youtube.Tracks = ScratchYouTube.DefaultTracks();
        await import.FetchTranscriptsAsync();
        var before = import.State.TranscriptText;
        await import.PolishAsync();
        Assert.Equal(before, import.State.TranscriptText);
        Assert.Contains(import.State.Messages, message => message.Key == "Import_PolishingFailed");
        Assert.False(import.State.IsPolishing);

        // The learner's next action (another fetch, a tab) clears the banner.
        await import.FetchTranscriptsAsync();
        Assert.Empty(import.State.Messages);
        await import.PolishAsync();
        Assert.NotEmpty(import.State.Messages);
        import.SwitchTab(YouTubeImportTab.History);
        Assert.Empty(import.State.Messages);
    }

    [Fact]
    public async Task ChoosingAnotherTrack_DownloadsItsCaptions()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var youtube = new ScratchYouTube();
        using var import = Coordinator(fixture,
            Adapter(fixture, youtube, new NativeSharedAiTests.ScratchOwnerAi { Polished = "x" }));
        Assert.True(await import.OpenAsync(A));
        import.SwitchTab(YouTubeImportTab.SingleVideo);
        import.SetVideoUrl(Url);
        await import.FetchTranscriptsAsync();

        await import.SelectTrackAsync(0);

        Assert.Equal(0, import.State.SelectedTrack);
        Assert.Contains("Hello there", import.State.TranscriptText);
        Assert.Equal(["ko", "en"], youtube.Downloads);
    }

    // ---- Helpers ----

    internal static YouTubeImportCoordinator Coordinator(NativeSliceFixture fixture,
        INativeYouTubeImport adapter) =>
        new(adapter, fixture.UserScope, NullLogger<YouTubeImportCoordinator>.Instance);

    internal static NativeYouTubeImport Adapter(NativeSliceFixture fixture, IYouTubeImportService youtube,
        IOwnerBoundNativeAiGateway ai, INativeYouTubeApi? api = null)
    {
        // The shared AiService and TranscriptFormattingService as the native registration builds
        // them, over this test's owner-bound gateway.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(fixture.Provider.GetRequiredService<IPreferencesService>());
        services.AddSingleton(fixture.UserScope);
        services.AddSingleton<IConnectivityService>(new NativeSharedAiTests.Online());
        services.AddSingleton(ai);
        services.AddNativeSharedAi();
        var shared = services.BuildServiceProvider();
        return new NativeYouTubeImport(fixture.Provider, fixture.UserScope,
            fixture.Provider.GetRequiredService<IPreferencesService>(),
            fixture.Provider.GetRequiredService<NativePendingWrites>(), youtube,
            shared.GetRequiredService<TranscriptFormattingService>(),
            NullLoggerFactory.Instance, api);
    }

    /// <summary>The shared YouTube service's calls, without the network.</summary>
    internal sealed class ScratchYouTube : IYouTubeImportService
    {
        public List<TranscriptTrack>? Tracks { get; set; } = DefaultTracks();
        public Exception? TrackFailure { get; set; }
        public int Calls { get; private set; }
        public List<string> Downloads { get; } = [];

        public static List<TranscriptTrack> DefaultTracks() =>
        [
            Track("en", "English", auto: false),
            Track("ko", "Korean (auto-generated)", auto: true),
        ];

        private static TranscriptTrack Track(string code, string name, bool auto) => new()
        {
            LanguageCode = code,
            LanguageName = name,
            IsAutoGenerated = auto,
            TrackInfo = new ClosedCaptionTrackInfo($"https://captions.example.invalid/{code}",
                new Language(code, name), auto)
        };

        public Task<Video> GetVideoMetadataAsync(string videoUrl)
        {
            Calls++;
            return Task.FromResult(new Video(VideoId.Parse(videoUrl), "A short Korean video",
                new Author(new YoutubeExplode.Channels.ChannelId("UC0000000000000000000000"), "Channel"),
                DateTimeOffset.UnixEpoch, string.Empty, TimeSpan.FromMinutes(2), [], [],
                new Engagement(0, 0, 0)));
        }

        public Task<List<TranscriptTrack>> GetAvailableTranscriptsAsync(string videoUrl)
        {
            Calls++;
            if (TrackFailure is not null)
                throw TrackFailure;
            return Task.FromResult(Tracks ?? []);
        }

        public Task<string> DownloadTranscriptTextAsync(TranscriptTrack track)
        {
            Calls++;
            Downloads.Add(track.LanguageCode);
            return Task.FromResult(track.LanguageCode == "ko"
                ? "첫 문장입니다\n둘째 문장입니다\n"
                : "Hello there\nGeneral Kenobi\n");
        }

        public Task<StreamHistory> ExtractAudioClipAsync(string videoUrl, double startTime, double duration) =>
            throw new NotSupportedException();
    }
}
