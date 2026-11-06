using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// The YouTube import endpoints the native heads read: the import history carries its
/// creation and completion times (additive: the existing fields keep their names and types),
/// and a channel update takes only the editable fields from the body, for the owner's channel
/// only.
/// </summary>
public sealed class YouTubeImportEndpointsTests : IClassFixture<ProfileSpeechApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ProfileSpeechApiFactory _factory;

    public YouTubeImportEndpointsTests(ProfileSpeechApiFactory factory) => _factory = factory;

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

    private HttpClient ClientFor(string owner)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtGenerator.GenerateToken(userProfileId: owner));
        return client;
    }

    private async Task<string> SeedImportAsync(string owner, VideoImportStatus status,
        DateTime createdAt, DateTime? completedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var import = new VideoImport
        {
            UserProfileId = owner,
            VideoUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            VideoId = "dQw4w9WgXcQ",
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
