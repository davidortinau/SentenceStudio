using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// The YouTube import endpoints the native heads read: the import history carries its
/// creation and completion times (additive: the existing fields keep their names and types),
/// a channel update takes only the editable fields from the body, for the owner's channel
/// only, and an import started from one of the owner's channels is linked to it (another
/// owner's or a missing channel is refused and nothing is created).
/// </summary>
public sealed class YouTubeImportEndpointsTests : IClassFixture<YouTubeImportApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly YouTubeImportApiFactory _factory;

    public YouTubeImportEndpointsTests(YouTubeImportApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ImportHistory_CarriesCreatedAndCompletedAt_ForTheOwnersRowsOnly()
    {
        var owner = $"yt-history-a-{Guid.NewGuid():N}";
        var other = $"yt-history-b-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, owner);
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, other);
        var created = new DateTime(2026, 11, 6, 9, 30, 0, DateTimeKind.Utc);
        var completed = created.AddMinutes(3);
        var done = await SeedImportAsync(owner, VideoImportStatus.Completed, created, completed);
        var running = await SeedImportAsync(owner, VideoImportStatus.FetchingTranscript, created.AddHours(1), null);
        var foreign = await SeedImportAsync(other, VideoImportStatus.Completed, created, completed);
        var client = ClientFor(owner);

        var response = await client.GetAsync("/api/imports");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var rows = json.RootElement.EnumerateArray().ToList();
        rows.Select(row => row.GetProperty("id").GetString()).Should().BeEquivalentTo([done, running]);
        rows.Should().NotContain(row => row.GetProperty("id").GetString() == foreign);
        var first = rows.Single(row => row.GetProperty("id").GetString() == done);
        first.GetProperty("createdAt").GetDateTime().ToUniversalTime().Should().Be(created);
        first.GetProperty("completedAt").GetDateTime().ToUniversalTime().Should().Be(completed);
        // The fields clients already read keep their names and types (status is still a number).
        first.GetProperty("status").GetInt32().Should().Be((int)VideoImportStatus.Completed);
        first.GetProperty("videoTitle").GetString().Should().Be("Video " + done[..8]);
        first.GetProperty("userProfileId").GetString().Should().Be(owner);
        first.TryGetProperty("learningResourceId", out _).Should().BeTrue();
        first.TryGetProperty("monitoredChannelId", out _).Should().BeTrue();
        rows.Single(row => row.GetProperty("id").GetString() == running)
            .GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.Null);

        // One import: the same shape; another owner's import is not found.
        var single = await client.GetFromJsonAsync<JsonElement>($"/api/imports/{done}", Json);
        single.GetProperty("createdAt").GetDateTime().ToUniversalTime().Should().Be(created);
        (await client.GetAsync($"/api/imports/{foreign}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void HistoryTimes_ReadAsHostLocal_AreSentAsUtcWithZ()
    {
        // Npgsql's legacy timestamp mode (API Program.cs) reads timestamptz as host-local time
        // (Kind Local): the history must still send the instant in UTC, with a Z.
        var created = new DateTime(2026, 11, 6, 9, 30, 0, DateTimeKind.Utc);
        var import = new VideoImport
        {
            UserProfileId = "owner",
            Status = VideoImportStatus.Completed,
            CreatedAt = created.ToLocalTime(),
            CompletedAt = created.AddMinutes(3).ToLocalTime(),
        };
        var unlabelled = new VideoImport
        {
            UserProfileId = "owner",
            CreatedAt = DateTime.SpecifyKind(created, DateTimeKind.Unspecified),
        };

        var json = JsonSerializer.SerializeToElement(VideoImportResponse.From(import), Json);
        var plain = JsonSerializer.SerializeToElement(VideoImportResponse.From(unlabelled), Json);

        json.GetProperty("createdAt").GetString().Should().Be("2026-11-06T09:30:00Z");
        json.GetProperty("completedAt").GetString().Should().Be("2026-11-06T09:33:00Z");
        // A provider that drops the kind stored UTC: relabelled, not shifted.
        plain.GetProperty("createdAt").GetString().Should().Be("2026-11-06T09:30:00Z");
    }

    [Fact]
    public async Task ChannelUpdate_TakesOnlyTheEditableFields_AndKeepsTheStoredOwnerAndTimes()
    {
        var owner = $"yt-channel-a-{Guid.NewGuid():N}";
        var other = $"yt-channel-b-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, owner);
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, other);
        var created = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var checkedAt = new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);
        var channelId = await SeedChannelAsync(owner, created, checkedAt);

        // The body names another owner, another id and other times: none of them is taken.
        var body = new
        {
            id = "not-this-id",
            userProfileId = other,
            channelUrl = "https://www.youtube.com/@edited",
            channelName = "Edited",
            channelHandle = "@edited",
            youTubeChannelId = "UC_edited",
            language = "Japanese",
            checkIntervalHours = 12,
            isActive = false,
            lastCheckedAt = (DateTime?)null,
        };
        var response = await ClientFor(owner).PutAsJsonAsync($"/api/channels/{channelId}", body, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await ReadChannelAsync(channelId);
        stored.Should().NotBeNull();
        stored!.UserProfileId.Should().Be(owner);
        stored.CreatedAt.Should().Be(created);
        stored.LastCheckedAt.Should().Be(checkedAt);
        stored.ChannelUrl.Should().Be("https://www.youtube.com/@edited");
        stored.ChannelName.Should().Be("Edited");
        stored.ChannelHandle.Should().Be("@edited");
        stored.YouTubeChannelId.Should().Be("UC_edited");
        stored.Language.Should().Be("Japanese");
        stored.CheckIntervalHours.Should().Be(12);
        stored.IsActive.Should().BeFalse();
        (await ReadChannelAsync("not-this-id")).Should().BeNull();
    }

    [Fact]
    public async Task ChannelUpdate_ForAnotherOwnersChannel_FailsClosed()
    {
        var owner = $"yt-channel-own-{Guid.NewGuid():N}";
        var other = $"yt-channel-oth-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, owner);
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, other);
        var created = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);
        var channelId = await SeedChannelAsync(owner, created, null);
        var body = new
        {
            channelUrl = "https://www.youtube.com/@taken",
            language = "Spanish",
            checkIntervalHours = 6,
            isActive = false,
            userProfileId = other,
        };

        var response = await ClientFor(other).PutAsJsonAsync($"/api/channels/{channelId}", body, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var stored = await ReadChannelAsync(channelId);
        stored!.UserProfileId.Should().Be(owner);
        stored.ChannelUrl.Should().Be("https://www.youtube.com/@seeded");
        stored.Language.Should().Be("Korean");
        stored.IsActive.Should().BeTrue();
        (await ClientFor(other).GetFromJsonAsync<JsonElement>("/api/channels", Json))
            .EnumerateArray().Should().BeEmpty("the other owner sees none of the owner's channels");
        (await ClientFor(other).PutAsJsonAsync($"/api/channels/missing-{Guid.NewGuid():N}", body, Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartImport_WithTheOwnersChannel_IsLinkedToIt_AndRetryKeepsTheLink()
    {
        var owner = $"yt-link-a-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, owner);
        var channelId = await SeedChannelAsync(owner, DateTime.UtcNow, null);
        var url = $"https://www.youtube.com/watch?v=link{Guid.NewGuid():N}";
        var client = ClientFor(owner);

        var response = await client.PostAsJsonAsync("/api/imports",
            new { videoUrl = url, language = "Korean", monitoredChannelId = channelId }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;
        // The stub fails the pipeline's first stage, so the row settles as Failed with no network.
        var failed = await WaitForImportAsync(id, row => row.ErrorMessage == YouTubeImportStub.Failure(url, 1));
        failed.UserProfileId.Should().Be(owner);
        failed.MonitoredChannelId.Should().Be(channelId);
        failed.Language.Should().Be("Korean");
        (await client.GetFromJsonAsync<JsonElement>($"/api/imports/{id}", Json))
            .GetProperty("monitoredChannelId").GetString().Should().Be(channelId);

        // Retry runs the same row again; the row keeps its channel.
        (await client.PostAsync($"/api/imports/{id}/retry", content: null))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        var retried = await WaitForImportAsync(id, row => row.ErrorMessage == YouTubeImportStub.Failure(url, 2));
        retried.MonitoredChannelId.Should().Be(channelId);
        retried.UserProfileId.Should().Be(owner);
    }

    [Fact]
    public async Task StartImport_WithAnotherOwnersOrAMissingChannel_IsRefused_AndCreatesNoImport()
    {
        var owner = $"yt-link-own-{Guid.NewGuid():N}";
        var other = $"yt-link-oth-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, owner);
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, other);
        var othersChannel = await SeedChannelAsync(other, DateTime.UtcNow, null);
        var foreignUrl = $"https://www.youtube.com/watch?v=foreign{Guid.NewGuid():N}";
        var missingUrl = $"https://www.youtube.com/watch?v=missing{Guid.NewGuid():N}";
        var emptyUrl = $"https://www.youtube.com/watch?v=empty{Guid.NewGuid():N}";
        var client = ClientFor(owner);

        (await client.PostAsJsonAsync("/api/imports",
                new { videoUrl = foreignUrl, language = "Korean", monitoredChannelId = othersChannel }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/imports",
                new { videoUrl = missingUrl, language = "Korean", monitoredChannelId = $"missing-{Guid.NewGuid():N}" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/imports",
                new { videoUrl = emptyUrl, language = "Korean", monitoredChannelId = "" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A later import of the owner's settles first, so a refused one had time to appear.
        var laterUrl = $"https://www.youtube.com/watch?v=later{Guid.NewGuid():N}";
        var later = await client.PostAsJsonAsync("/api/imports", new { videoUrl = laterUrl, language = "Korean" }, Json);
        later.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var laterId = (await later.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;
        await WaitForImportAsync(laterId, row => row.ErrorMessage == YouTubeImportStub.Failure(laterUrl, 1));

        _factory.YouTube.Calls(foreignUrl).Should().Be(0, "a refused import never starts the pipeline");
        _factory.YouTube.Calls(missingUrl).Should().Be(0);
        _factory.YouTube.Calls(emptyUrl).Should().Be(0);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.VideoImports.AsNoTracking().Where(row => row.UserProfileId == owner)
                .Select(row => row.VideoUrl).ToListAsync())
            .Should().Equal([laterUrl], "only the import without a channel was created");
        (await db.VideoImports.AsNoTracking().CountAsync(row => row.MonitoredChannelId == othersChannel))
            .Should().Be(0);
        (await db.VideoImports.AsNoTracking().CountAsync(row =>
                row.VideoUrl == foreignUrl || row.VideoUrl == missingUrl || row.VideoUrl == emptyUrl))
            .Should().Be(0);
    }

    [Fact]
    public async Task StartImport_WithoutAChannel_IsAManualImport_AsBefore()
    {
        var owner = $"yt-manual-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, owner);
        var url = $"https://www.youtube.com/watch?v=manual{Guid.NewGuid():N}";
        var defaultUrl = $"https://www.youtube.com/watch?v=default{Guid.NewGuid():N}";
        var client = ClientFor(owner);

        // The body an existing client sends: the link and the language, nothing else.
        var response = await client.PostAsJsonAsync("/api/imports", new { videoUrl = url, language = "Japanese" }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.OriginalString.Should().StartWith("/api/imports/");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = body.GetProperty("id").GetString()!;
        // The status is read while the pipeline may already be running, so only its shape is fixed.
        body.GetProperty("status").ValueKind.Should().Be(JsonValueKind.Number);
        body.TryGetProperty("message", out _).Should().BeTrue();
        var row = await WaitForImportAsync(id, row => row.ErrorMessage == YouTubeImportStub.Failure(url, 1));
        row.MonitoredChannelId.Should().BeNull();
        row.UserProfileId.Should().Be(owner);
        row.Language.Should().Be("Japanese");

        // No language: Korean, as before.
        var unnamed = await client.PostAsJsonAsync("/api/imports", new { videoUrl = defaultUrl }, Json);
        unnamed.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var unnamedId = (await unnamed.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;
        var korean = await WaitForImportAsync(unnamedId, row => row.ErrorMessage == YouTubeImportStub.Failure(defaultUrl, 1));
        korean.Language.Should().Be("Korean");
        korean.MonitoredChannelId.Should().BeNull();
    }

    [Fact]
    public async Task StartImport_WithTheVideosIdAndTitle_StoresThemBeforeThePipeline_AndRefusesAnotherVideosId()
    {
        var owner = $"yt-video-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, owner);
        var channelId = await SeedChannelAsync(owner, DateTime.UtcNow, null);
        var videoId = NewVideoId();
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var client = ClientFor(owner);

        var response = await client.PostAsJsonAsync("/api/imports", new
        {
            videoUrl = url, language = "Korean", monitoredChannelId = channelId, videoId, videoTitle = "첫 번째 영상"
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;
        // The metadata step fails (the stub), and the row still names its video.
        var failed = await WaitForImportAsync(id, row => row.ErrorMessage == YouTubeImportStub.Failure(url, 1));
        failed.Status.Should().Be(VideoImportStatus.Failed);
        failed.VideoId.Should().Be(videoId);
        failed.VideoTitle.Should().Be("첫 번째 영상");
        failed.MonitoredChannelId.Should().Be(channelId);

        // Another video's id for this link is refused, and nothing is created.
        var otherUrl = $"https://www.youtube.com/watch?v={NewVideoId()}";
        (await client.PostAsJsonAsync("/api/imports",
                new { videoUrl = otherUrl, language = "Korean", videoId = NewVideoId() }, Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.YouTube.Calls(otherUrl).Should().Be(0);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.VideoImports.AsNoTracking().CountAsync(row => row.VideoUrl == otherUrl)).Should().Be(0);
    }

    [Fact]
    public async Task ImportsByVideo_AreTheOwnersNewestImportOfEachVideo_BeyondTheHistoryLimit()
    {
        var owner = $"yt-byvideo-a-{Guid.NewGuid():N}";
        var other = $"yt-byvideo-b-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, owner);
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, other);
        var start = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var old = NewVideoId();
        var twice = NewVideoId();
        var oldImport = await SeedImportAsync(owner, VideoImportStatus.Completed, start, start.AddMinutes(3), old);
        await SeedImportAsync(owner, VideoImportStatus.Failed, start.AddDays(1), start.AddDays(1), twice);
        var twiceNewer = await SeedImportAsync(owner, VideoImportStatus.Completed, start.AddDays(2), start.AddDays(2), twice);
        // 55 newer imports push the old video out of the newest 50.
        for (var index = 0; index < 55; index++)
            await SeedImportAsync(owner, VideoImportStatus.Completed, start.AddDays(10).AddMinutes(index), null, NewVideoId());
        // Another owner's newer import of the same video is not the owner's.
        await SeedImportAsync(other, VideoImportStatus.Failed, start.AddDays(30), null, old);
        var client = ClientFor(owner);

        var history = await client.GetFromJsonAsync<JsonElement>("/api/imports", Json);
        history.GetArrayLength().Should().Be(50);
        history.EnumerateArray().Should().NotContain(row => row.GetProperty("videoId").GetString() == old);

        var byVideo = await client.GetFromJsonAsync<JsonElement>(
            $"/api/imports?videoId={old}&videoId={twice}&videoId={NewVideoId()}", Json);
        var rows = byVideo.EnumerateArray().ToList();
        rows.Select(row => row.GetProperty("id").GetString()).Should().BeEquivalentTo([oldImport, twiceNewer]);
        rows.Should().OnlyContain(row => row.GetProperty("userProfileId").GetString() == owner);
        rows.Single(row => row.GetProperty("id").GetString() == oldImport)
            .GetProperty("status").GetInt32().Should().Be((int)VideoImportStatus.Completed);
        // The other owner sees only their own import of that video.
        var theirs = await ClientFor(other).GetFromJsonAsync<JsonElement>($"/api/imports?videoId={old}", Json);
        theirs.EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("userProfileId").GetString().Should().Be(other);
        (await client.GetAsync("/api/imports?" + string.Join("&",
                Enumerable.Range(0, 51).Select(_ => "videoId=" + NewVideoId()))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>An 11-character YouTube-shaped video id.</summary>
    private static string NewVideoId() => Guid.NewGuid().ToString("N")[..11];

    private async Task<VideoImport> WaitForImportAsync(string id, Func<VideoImport, bool> settled)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        VideoImport? row = null;
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                row = await db.VideoImports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);
            }
            if (row is not null && settled(row))
                return row;
            await Task.Delay(50);
        }
        throw new TimeoutException(
            $"Import {id} did not settle (status {row?.Status}, error '{row?.ErrorMessage}').");
    }

    private HttpClient ClientFor(string owner)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtGenerator.GenerateToken(userProfileId: owner));
        return client;
    }

    private async Task<string> SeedImportAsync(string owner, VideoImportStatus status,
        DateTime createdAt, DateTime? completedAt, string videoId = "dQw4w9WgXcQ")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var import = new VideoImport
        {
            UserProfileId = owner,
            VideoUrl = $"https://www.youtube.com/watch?v={videoId}",
            VideoId = videoId,
            Status = status,
            Language = "English",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
        };
        import.VideoTitle = "Video " + import.Id[..8];
        db.VideoImports.Add(import);
        await db.SaveChangesAsync();
        return import.Id;
    }

    private async Task<string> SeedChannelAsync(string owner, DateTime createdAt, DateTime? checkedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var channel = new MonitoredChannel
        {
            UserProfileId = owner,
            ChannelUrl = "https://www.youtube.com/@seeded",
            ChannelName = "Seeded",
            ChannelHandle = "@seeded",
            Language = "Korean",
            CheckIntervalHours = 24,
            IsActive = true,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastCheckedAt = checkedAt,
        };
        db.MonitoredChannels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private async Task<MonitoredChannel?> ReadChannelAsync(string id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.MonitoredChannels.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id);
    }
}

/// <summary>
/// The API with a YouTube service that never reaches the network: every video's metadata call
/// fails with a numbered message, so the import pipeline settles each run as Failed.
/// </summary>
public sealed class YouTubeImportApiFactory : ProfileSpeechApiFactory
{
    public YouTubeImportStub YouTube { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IYouTubeImportService>();
            services.AddSingleton<IYouTubeImportService>(YouTube);
        });
    }
}

public sealed class YouTubeImportStub : IYouTubeImportService
{
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

    public static string Failure(string url, int run) => $"Stub run {run} for {url}";

    public int Calls(string url) => _calls.GetValueOrDefault(url);

    public Task<YoutubeExplode.Videos.Video> GetVideoMetadataAsync(string videoUrl)
    {
        var run = _calls.AddOrUpdate(videoUrl, 1, (_, count) => count + 1);
        throw new VideoImportException(Failure(videoUrl, run), "StubNoNetwork");
    }

    public Task<List<TranscriptTrack>> GetAvailableTranscriptsAsync(string videoUrl) =>
        throw new NotSupportedException("Not reached: the metadata call fails first.");

    public Task<string> DownloadTranscriptTextAsync(TranscriptTrack track) =>
        throw new NotSupportedException("Not reached: the metadata call fails first.");

    public Task<StreamHistory> ExtractAudioClipAsync(string videoUrl, double startTime, double duration) =>
        throw new NotSupportedException("Not used by the import pipeline.");
}
