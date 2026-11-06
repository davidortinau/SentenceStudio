using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// YouTube import's history and channels (source MediaImport.razor tabs and ChannelDetail.razor):
/// the API calls as one owner, the history's stuck and retry rules and polling, the channel form
/// and video discovery, and that another owner's calls are refused before they reach the API.
/// </summary>
public sealed class NativeYouTubeChannelsTests
{
    private const string A = NativeSliceFixture.UserA;
    private const string B = NativeSliceFixture.UserB;
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    // ---- The adapter's API calls ----

    [Fact]
    public async Task ApiCalls_ParseTheHistoryAndChannels_AsTheOwner_AndRefuseAnotherOwnerBeforeTheRequest()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        fixture.Activate(A);
        var api = new ScratchApi();
        api.Imports[A] = """
            [{"id":"i1","userProfileId":"native-owner-a","videoId":"v1","videoTitle":"One","videoUrl":"u1",
              "monitoredChannelId":null,"status":10,"errorMessage":null,"learningResourceId":"r1",
              "language":"Korean","createdAt":"2026-11-06T11:00:00Z","completedAt":"2026-11-06T11:03:00Z"},
             {"id":"i2","userProfileId":"native-owner-a","videoId":"v2","videoTitle":"Two","status":1}]
            """;
        api.Channels[A] = """[{"id":"c1","userProfileId":"native-owner-a","channelUrl":"https://www.youtube.com/@one","channelName":"One","isActive":true,"checkIntervalHours":24,"language":"Korean"}]""";
        api.Imports[B] = """[{"id":"b1","userProfileId":"native-owner-b","status":10}]""";
        var adapter = NativeYouTubeImportTests.Adapter(fixture, new NativeYouTubeImportTests.ScratchYouTube(),
            new NativeSharedAiTests.ScratchOwnerAi(), api);

        var imports = await adapter.ListImportsAsync(A, default);
        Assert.Equal(["i1", "i2"], imports.Select(row => row.Id));
        Assert.Equal(new DateTime(2026, 11, 6, 11, 0, 0, DateTimeKind.Utc), imports[0].CreatedAt?.ToUniversalTime());
        Assert.Equal(VideoImportStatus.Completed, imports[0].Status);
        // An API before B79 sends no createdAt: unknown, not 0001-01-01.
        Assert.Null(imports[1].CreatedAt);
        var channels = await adapter.ListChannelsAsync(A, default);
        Assert.Equal("One", Assert.Single(channels).ChannelName);

        await adapter.UpdateChannelAsync(A, channels[0], default);
        await adapter.RetryImportAsync(A, "i2", default);
        await adapter.StartImportAsync(A, "https://www.youtube.com/watch?v=x", "Korean", "c1", default);
        await adapter.StartImportAsync(A, "https://www.youtube.com/watch?v=y", "Korean", null, default);
        Assert.Equal(["GET api/imports", "GET api/channels", "PUT api/channels/c1",
            "POST api/imports/i2/retry", "POST api/imports", "POST api/imports"],
            api.Requests.Select(r => r.Line));
        Assert.All(api.Requests, request => Assert.Equal(A, request.Owner));
        using (var body = JsonDocument.Parse(api.Requests[2].Body!))
            Assert.Equal("https://www.youtube.com/@one", body.RootElement.GetProperty("channelUrl").GetString());
        using (var body = JsonDocument.Parse(api.Requests[4].Body!))
        {
            Assert.Equal("https://www.youtube.com/watch?v=x", body.RootElement.GetProperty("videoUrl").GetString());
            Assert.Equal("Korean", body.RootElement.GetProperty("language").GetString());
            // The channel's import names its channel (the API's StartImportRequest.MonitoredChannelId).
            Assert.Equal("c1", body.RootElement.GetProperty("monitoredChannelId").GetString());
        }
        using (var body = JsonDocument.Parse(api.Requests[5].Body!))
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("monitoredChannelId").ValueKind);

        // The device now belongs to B: A's calls are refused before any request.
        fixture.Activate(B);
        var before = api.Requests.Count;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.ListImportsAsync(A, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.UpdateChannelAsync(A, channels[0], default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.StartImportAsync(A, "https://www.youtube.com/watch?v=x", null, "c1", default));
        Assert.Equal(before, api.Requests.Count);
        // B reads only B's history.
        Assert.Equal(["b1"], (await adapter.ListImportsAsync(B, default)).Select(row => row.Id));
    }

    [Fact]
    public async Task AddChannel_ReadsTheServersResolution()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var api = new ScratchApi
        {
            AddResponse = """{"id":"c9","channelUrl":"https://www.youtube.com/@x","channelHandle":"@x","language":"Korean","isActive":true,"metadataResolved":false,"resolutionError":"Not found"}"""
        };
        var adapter = NativeYouTubeImportTests.Adapter(fixture, new NativeYouTubeImportTests.ScratchYouTube(),
            new NativeSharedAiTests.ScratchOwnerAi(), api);

        var unresolved = await adapter.AddChannelAsync(A, new MonitoredChannel { ChannelUrl = "https://www.youtube.com/@x" }, default);
        Assert.False(unresolved.MetadataResolved);
        Assert.Equal("Not found", unresolved.ResolutionError);
        Assert.Equal("c9", unresolved.Channel.Id);

        api.AddResponse = """{"id":"c10","channelName":"Resolved","youTubeChannelId":"UC1","isActive":true}""";
        var resolved = await adapter.AddChannelAsync(A, new MonitoredChannel(), default);
        Assert.True(resolved.MetadataResolved);
        Assert.Equal("Resolved", resolved.Channel.ChannelName);
    }

    // ---- History (source MediaImport.razor:209-588) ----

    [Fact]
    public async Task History_StuckFailedAndCompletedRows_RetryAndPolling()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var import = new ScratchImport();
        import.Imports.AddRange(
        [
            Record("done", VideoImportStatus.Completed, Now.AddHours(-1), resource: "r1"),
            Record("failed", VideoImportStatus.Failed, Now.AddHours(-2), error: "No Korean transcript."),
            Record("stuck", VideoImportStatus.FetchingTranscript, Now.AddMinutes(-11)),
            Record("running", VideoImportStatus.CleaningTranscript, Now.AddMinutes(-2)),
            Record("unknown", VideoImportStatus.Pending, null),
        ]);
        import.Channels.Add(new MonitoredChannel { Id = "c1", ChannelName = "Channel One", UserProfileId = A });
        using var page = new YouTubeImportCoordinator(import, fixture.UserScope,
            NullLogger<YouTubeImportCoordinator>.Instance, () => Now, TimeSpan.FromHours(1));
        Assert.True(await page.OpenAsync(A));
        page.SwitchTab(YouTubeImportTab.History);

        var rows = page.State.DisplayedImports.ToDictionary(row => row.Id);
        Assert.Equal(5, page.State.ImportCount);
        Assert.True(rows["done"].IsClickable);
        Assert.False(rows["done"].CanRetry);
        Assert.True(rows["failed"].CanRetry);
        Assert.True(rows["stuck"].IsStuck && rows["stuck"].CanRetry);
        Assert.False(rows["running"].CanRetry);
        Assert.False(rows["unknown"].IsStuck); // no creation time: never taken as stuck
        Assert.True(rows["done"].IsManual);

        // A completed import opens its resource; a failed one shows its error.
        Assert.Equal("r1", page.OpenImport("done"));
        Assert.Null(page.OpenImport("failed"));
        Assert.Equal("No Korean transcript.", Assert.Single(page.State.Messages).Argument);

        await page.RetryImportAsync("stuck");
        Assert.Equal(["stuck"], import.Retried);
        Assert.Equal(VideoImportStatus.Pending, page.State.DisplayedImports.Single(row => row.Id == "stuck").Status);
        Assert.Contains(page.State.Messages, message => message.Key == "YouTubeImport_Retrying");
        await page.RetryImportAsync("running"); // not stuck, not failed: no retry
        Assert.Equal(["stuck"], import.Retried);

        // Polling updates the active rows from the history.
        import.Imports[3] = import.Imports[3] with { Status = VideoImportStatus.Completed, LearningResourceId = "r4" };
        await page.PollOnceAsync(default);
        Assert.Equal(VideoImportStatus.Completed,
            page.State.DisplayedImports.Single(row => row.Id == "running").Status);

        await page.RefreshImportsAsync();
        Assert.Contains(page.State.Messages, message => message.Key == "Import_ImportsRefreshed");
        Assert.All(import.Owners, owner => Assert.Equal(A, owner));
    }

    [Fact]
    public async Task History_ShowMore_AndChannels_ToggleMonitoring()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var import = new ScratchImport();
        for (var index = 0; index < 120; index++)
            import.Imports.Add(Record($"i{index}", VideoImportStatus.Completed, Now.AddMinutes(-index)));
        import.Channels.Add(new MonitoredChannel { Id = "c1", ChannelName = "One", UserProfileId = A, IsActive = true });
        using var page = new YouTubeImportCoordinator(import, fixture.UserScope,
            NullLogger<YouTubeImportCoordinator>.Instance, () => Now, TimeSpan.FromHours(1));
        Assert.True(await page.OpenAsync(A));

        Assert.Equal(50, page.State.DisplayedImports.Length);
        Assert.True(page.State.HasMoreImports);
        page.ShowMoreImports();
        page.ShowMoreImports();
        Assert.Equal(120, page.State.DisplayedImports.Length);
        Assert.False(page.State.HasMoreImports);

        await page.ToggleChannelActiveAsync("c1");
        Assert.False(Assert.Single(page.State.Channels).IsActive);
        Assert.Contains(page.State.Messages, message => message.Key == "Import_ChannelPaused");
        Assert.False(Assert.Single(import.Updated).IsActive);

        import.UpdateFailure = new HttpRequestException("down");
        await page.ToggleChannelActiveAsync("c1");
        Assert.False(Assert.Single(page.State.Channels).IsActive); // put back
        Assert.Contains(page.State.Messages, message => message.Key == "YouTubeImport_UpdateChannelFailed");
    }

    [Fact]
    public async Task LeavingTheChannelPage_ReloadsTheChannelsAndHistory_AndKeepsItsToast()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var import = new ScratchImport();
        using var page = new YouTubeImportCoordinator(import, fixture.UserScope,
            NullLogger<YouTubeImportCoordinator>.Instance, () => Now, TimeSpan.FromHours(1));
        Assert.True(await page.OpenAsync(A));
        Assert.Empty(page.State.Channels);

        // The channel page saved a channel and started an import, then closed (Save or Cancel).
        import.Channels.Add(new MonitoredChannel { Id = "c1", ChannelName = "One", UserProfileId = A, IsActive = true });
        import.Imports.Add(Record("i1", VideoImportStatus.Pending, Now));
        page.ShowMessage(NativeMessage.Success("ChannelDetail_ChannelAdded"));
        await page.ReloadAfterChannelPageAsync();
        Assert.Equal("c1", Assert.Single(page.State.Channels).Id);
        Assert.Equal("i1", Assert.Single(page.State.DisplayedImports).Id);
        Assert.Contains(page.State.Messages, message => message.Key == "ChannelDetail_ChannelAdded");
        Assert.All(import.Owners, owner => Assert.Equal(A, owner));

        // Another owner signed in meanwhile: nothing is read for the old page.
        await fixture.SeedProfileAsync(B);
        var reads = import.Owners.Count;
        fixture.Activate(B);
        await page.ReloadAfterChannelPageAsync();
        Assert.Equal(reads, import.Owners.Count);
    }

    // ---- Channel Detail (source ChannelDetail.razor) ----

    [Fact]
    public async Task NewChannel_StartsWithTheProfileLanguage_AndSavesResolvedOrOpensItsEditPage()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var import = new ScratchImport { TargetLanguage = "Korean" };
        using var channel = Channel(fixture, import);
        var outcomes = new List<(YouTubeChannelOutcome, string?)>();
        channel.Finished += (outcome, id) => outcomes.Add((outcome, id));
        Assert.True(await channel.OpenAsync(A, null));
        Assert.True(channel.State.IsNew);
        Assert.Equal("Korean", channel.State.Language);
        Assert.Equal(24, channel.State.CheckIntervalHours);
        Assert.False(channel.State.CanSave);

        channel.SetChannelUrl("https://www.youtube.com/@My_easykorean/videos");
        channel.SetCheckIntervalHours(12);
        channel.SetCheckIntervalHours(7); // not an option
        Assert.True(channel.State.CanSave);
        import.AddResolved = false;
        await channel.SaveAsync();
        var sent = Assert.Single(import.Added);
        Assert.Equal("@My_easykorean", sent.ChannelHandle);
        Assert.Equal(12, sent.CheckIntervalHours);
        Assert.Equal(A, sent.UserProfileId);
        Assert.Equal([(YouTubeChannelOutcome.EditSaved, sent.Id)], outcomes);
        Assert.Contains(channel.State.Messages, message => message.Key == "YouTubeChannel_SavedUnresolved");

        using var second = Channel(fixture, import);
        second.Finished += (outcome, id) => outcomes.Add((outcome, id));
        Assert.True(await second.OpenAsync(A, null));
        second.SetChannelUrl("https://www.youtube.com/@other");
        import.AddResolved = true;
        await second.SaveAsync();
        Assert.Equal(YouTubeChannelOutcome.Saved, outcomes[^1].Item1);
        Assert.Contains(second.State.Messages, message => message.Key == "ChannelDetail_ChannelAdded");
    }

    [Fact]
    public async Task ExistingChannel_LoadsOnlyTheOwnersChannel_ResolvesAndDiscoversVideos()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var import = new ScratchImport();
        import.Channels.Add(new MonitoredChannel
        {
            Id = "c1", UserProfileId = A, ChannelUrl = "https://www.youtube.com/@one", Language = "Korean",
            CheckIntervalHours = 6, IsActive = false
        });
        import.Videos.AddRange(
        [
            new NativeYouTubeVideo("v1", "New one", "https://www.youtube.com/watch?v=v1"),
            new NativeYouTubeVideo("v2", "Imported", "https://www.youtube.com/watch?v=v2"),
            new NativeYouTubeVideo("v3", "Failed", "https://www.youtube.com/watch?v=v3"),
            new NativeYouTubeVideo("v4", "Running", "https://www.youtube.com/watch?v=v4"),
        ]);
        import.Imports.AddRange(
        [
            Record("i2", VideoImportStatus.Completed, Now, videoId: "v2"),
            Record("i3", VideoImportStatus.Failed, Now, videoId: "v3"),
            Record("i4", VideoImportStatus.GeneratingVocabulary, Now, videoId: "v4"),
        ]);
        using var channel = Channel(fixture, import);
        Assert.True(await channel.OpenAsync(A, "c1"));
        Assert.False(channel.State.IsNew);
        Assert.Equal(6, channel.State.CheckIntervalHours);
        Assert.False(channel.State.IsActive);
        Assert.True(channel.State.ShowsDiscovery);
        Assert.False(channel.State.HasYouTubeChannelId);

        // No YouTube id: Retry resolve runs on the device, then saves.
        import.ResolveTo = "UC_one";
        await channel.RetryResolveAsync();
        Assert.True(channel.State.HasYouTubeChannelId);
        Assert.Equal("UC_one", Assert.Single(import.Updated).YouTubeChannelId);
        Assert.Contains(channel.State.Messages, message => message.Key == "YouTubeChannel_Resolved");

        await channel.CheckForNewVideosAsync();
        var videos = channel.State.Videos.ToDictionary(video => video.VideoId);
        Assert.Null(videos["v1"].ImportStatus);
        Assert.True(videos["v2"].IsCompleted && !videos["v2"].CanSelect);
        Assert.True(videos["v3"].IsFailed && videos["v3"].CanSelect);
        Assert.True(videos["v4"].IsInProgress);
        // Source: the new videos are selected (not the failed one).
        Assert.Equal(["v1"], channel.State.SelectedVideoIds);
        channel.ToggleVideo("v2"); // imported: cannot be selected
        Assert.Equal(["v1"], channel.State.SelectedVideoIds);
        channel.ToggleSelectAll();
        Assert.Equal(["v1", "v3"], channel.State.SelectedVideoIds.Order());
        channel.ToggleSelectAll();
        Assert.Empty(channel.State.SelectedVideoIds);
        channel.ToggleVideo("v1");

        await channel.ImportSelectedAsync();
        // Each import is linked to this channel (source MonitoredChannelId = channel.Id).
        Assert.Equal([("https://www.youtube.com/watch?v=v1", "Korean", "c1")], import.Started);
        Assert.Contains(channel.State.Messages, message => message.Key == "ChannelDetail_ImportStarted");

        // Retry names only the failed import; the server keeps that import's own channel link.
        await channel.RetryFailedVideoAsync("v3");
        Assert.Equal(["i3"], import.Retried);
        Assert.Single(import.Started);
        Assert.Contains(channel.State.Messages, message => message.Key == "YouTubeChannel_RetryingVideo");
        Assert.All(import.Owners, owner => Assert.Equal(A, owner));

        // Another owner's channel id is not found (the list is the owner's).
        using var foreign = Channel(fixture, import);
        Assert.True(await foreign.OpenAsync(A, "someone-elses"));
        Assert.Equal("YouTubeChannel_NotFound", foreign.State.Error?.Key);
        Assert.False(foreign.State.ShowsDiscovery);
    }

    [Fact]
    public async Task AnOwnerChange_BlocksTheChannelPage_AndWritesNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        fixture.Activate(A);
        var import = new ScratchImport();
        using var channel = Channel(fixture, import);
        Assert.True(await channel.OpenAsync(A, null));
        channel.SetChannelUrl("https://www.youtube.com/@x");
        channel.SetLanguage("Korean");

        fixture.Activate(B);
        await channel.SaveAsync();

        Assert.True(channel.State.Blocked);
        Assert.Empty(import.Added);
        Assert.Empty(import.Updated);
        // A page for A cannot open on B's device.
        using var other = Channel(fixture, import);
        Assert.False(await other.OpenAsync(A, null));
    }

    [Fact]
    public async Task SaveWithoutALanguage_SaysSo()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var import = new ScratchImport { TargetLanguage = null };
        using var channel = Channel(fixture, import);
        Assert.True(await channel.OpenAsync(A, null));
        channel.SetChannelUrl("https://www.youtube.com/@x");
        Assert.False(channel.State.CanSave);
        await channel.SaveAsync();
        Assert.Equal("YouTubeChannel_Required", channel.State.Error?.Key);
        Assert.Empty(import.Added);
        Assert.Equal("@x", YouTubeChannelCoordinator.ExtractChannelHandle("https://www.youtube.com/@x?si=1"));
        Assert.Equal(string.Empty, YouTubeChannelCoordinator.ExtractChannelHandle("https://www.youtube.com/channel/UC1"));
    }

    // ---- Helpers ----

    private static YouTubeChannelCoordinator Channel(NativeSliceFixture fixture, INativeYouTubeImport import) =>
        new(import, fixture.UserScope, NullLogger<YouTubeChannelCoordinator>.Instance, TimeSpan.Zero);

    private static NativeYouTubeImportRecord Record(string id, VideoImportStatus status, DateTime? created,
        string? resource = null, string? error = null, string? videoId = null) =>
        new(id, videoId ?? id, $"Video {id}", $"https://www.youtube.com/watch?v={videoId ?? id}", null, status,
            error, resource, created, null);

    /// <summary>The page's calls without the network or the API; every call records its owner.</summary>
    private sealed class ScratchImport : INativeYouTubeImport
    {
        public string? TargetLanguage { get; set; } = "Korean";
        public List<NativeYouTubeImportRecord> Imports { get; } = [];
        public List<MonitoredChannel> Channels { get; } = [];
        public List<NativeYouTubeVideo> Videos { get; } = [];
        public List<string> Owners { get; } = [];
        public List<string> Retried { get; } = [];
        public List<MonitoredChannel> Updated { get; } = [];
        public List<MonitoredChannel> Added { get; } = [];
        public List<(string Url, string? Language, string? ChannelId)> Started { get; } = [];
        public bool AddResolved { get; set; } = true;
        public string? ResolveTo { get; set; }
        public Exception? UpdateFailure { get; set; }

        private Task<T> As<T>(string owner, T value)
        {
            Owners.Add(owner);
            return Task.FromResult(value);
        }

        public Task<string?> GetTargetLanguageAsync(string owner, CancellationToken token) => As(owner, TargetLanguage);
        public Task<string?> GetVideoTitleAsync(string owner, string videoUrl, CancellationToken token) => As<string?>(owner, null);
        public Task<IReadOnlyList<TranscriptTrack>> GetTracksAsync(string owner, string videoUrl, CancellationToken token) =>
            As<IReadOnlyList<TranscriptTrack>>(owner, []);
        public Task<string> DownloadTranscriptAsync(string owner, TranscriptTrack track, CancellationToken token) => As(owner, string.Empty);
        public Task<string> PolishAsync(string owner, string transcript, string? languageCode, CancellationToken token) => As(owner, transcript);
        public Task<NativeYouTubeSaved> SaveResourceAsync(string owner, NativeYouTubeResourceDraft draft, CancellationToken token) =>
            As(owner, new NativeYouTubeSaved("r"));
        public Task<IReadOnlyList<MonitoredChannel>> ListChannelsAsync(string owner, CancellationToken token) =>
            As<IReadOnlyList<MonitoredChannel>>(owner, Channels.Where(row => row.UserProfileId == owner).ToList());

        public Task UpdateChannelAsync(string owner, MonitoredChannel channel, CancellationToken token)
        {
            Owners.Add(owner);
            if (UpdateFailure is not null)
                throw UpdateFailure;
            Updated.Add(new MonitoredChannel
            {
                Id = channel.Id, IsActive = channel.IsActive, YouTubeChannelId = channel.YouTubeChannelId,
                ChannelUrl = channel.ChannelUrl
            });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NativeYouTubeImportRecord>> ListImportsAsync(string owner, CancellationToken token) =>
            As<IReadOnlyList<NativeYouTubeImportRecord>>(owner, Imports.ToList());

        public Task RetryImportAsync(string owner, string importId, CancellationToken token)
        {
            Owners.Add(owner);
            Retried.Add(importId);
            return Task.CompletedTask;
        }

        public Task<NativeYouTubeChannelAdded> AddChannelAsync(string owner, MonitoredChannel channel, CancellationToken token)
        {
            Owners.Add(owner);
            Added.Add(channel);
            if (AddResolved)
                channel.ChannelName = "Resolved";
            return Task.FromResult(new NativeYouTubeChannelAdded(channel, AddResolved, AddResolved ? null : "Not found"));
        }

        public Task<(bool Resolved, string? Error)> ResolveChannelAsync(string owner, MonitoredChannel channel, CancellationToken token)
        {
            Owners.Add(owner);
            if (ResolveTo is null)
                return Task.FromResult<(bool, string?)>((false, "Channel not found"));
            channel.YouTubeChannelId = ResolveTo;
            channel.ChannelName = "One";
            return Task.FromResult<(bool, string?)>((true, null));
        }

        public Task<IReadOnlyList<NativeYouTubeVideo>> GetRecentVideosAsync(string owner, MonitoredChannel channel,
            int maxResults, CancellationToken token) =>
            As<IReadOnlyList<NativeYouTubeVideo>>(owner, Videos.Take(maxResults).ToList());

        public Task StartImportAsync(string owner, string videoUrl, string? language, string? monitoredChannelId,
            CancellationToken token)
        {
            Owners.Add(owner);
            Started.Add((videoUrl, language, monitoredChannelId));
            return Task.CompletedTask;
        }
    }

    /// <summary>The API as one owner: answers from per-owner JSON and records each request.</summary>
    internal sealed class ScratchApi : INativeYouTubeApi
    {
        public Dictionary<string, string> Imports { get; } = [];
        public Dictionary<string, string> Channels { get; } = [];
        public string AddResponse { get; set; } = "{}";
        public List<(string Owner, string Line, string? Body)> Requests { get; } = [];

        public async Task<T> CallAsync<T>(string owner, Func<HttpClient, CancellationToken, Task<T>> call,
            CancellationToken cancellationToken)
        {
            using var client = new HttpClient(new Handler(this, owner))
            {
                BaseAddress = new Uri("https://api.example.invalid/")
            };
            return await call(client, cancellationToken);
        }

        private sealed class Handler(ScratchApi api, string owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath.TrimStart('/');
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                api.Requests.Add((owner, $"{request.Method} {path}", body));
                var json = (request.Method.Method, path) switch
                {
                    ("GET", "api/imports") => api.Imports.GetValueOrDefault(owner, "[]"),
                    ("GET", "api/channels") => api.Channels.GetValueOrDefault(owner, "[]"),
                    ("POST", "api/channels") => api.AddResponse,
                    _ => "{}"
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        }
    }
}
