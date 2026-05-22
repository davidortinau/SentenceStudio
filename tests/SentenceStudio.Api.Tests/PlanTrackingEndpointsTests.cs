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

public sealed class PlanTrackingEndpointsTests : IClassFixture<ProfileSpeechApiFactory>
{
    private readonly ProfileSpeechApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PlanTrackingEndpointsTests(ProfileSpeechApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient ClientWithJwt(string userProfileId)
    {
        var token = TestJwtGenerator.GenerateToken(userProfileId: userProfileId);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ActivityLog_NoCompletions_ReturnsEmptyArray()
    {
        var profileId = $"log-empty-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var client = ClientWithJwt(profileId);

        var response = await client.GetAsync("/api/v1/activity-log?fromUtc=2026-05-18T00:00:00Z&toUtc=2026-05-24T23:59:59Z");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        json.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        json.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ActivityLog_ReturnsMondayAnchoredWeeksNewestFirstWithSevenDays()
    {
        var profileId = $"log-weeks-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        await SeedCompletionAsync(profileId, new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc), "plan-week-one", "Reading", 10, createdAt: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));
        await SeedCompletionAsync(profileId, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc), "plan-week-three", "Translation", 15, createdAt: new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc));
        var client = ClientWithJwt(profileId);

        var weeks = await GetActivityLogAsync(client, "2026-05-18T00:00:00Z", "2026-06-07T23:59:59Z");

        weeks.GetArrayLength().Should().Be(3);
        weeks[0].GetProperty("weekStart").GetDateTime().Should().Be(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        weeks[0].GetProperty("weekEnd").GetDateTime().Should().Be(new DateTime(2026, 6, 7, 23, 59, 59, DateTimeKind.Utc));
        weeks[0].GetProperty("days").GetArrayLength().Should().Be(7);
        weeks[0].GetProperty("days")[1].GetProperty("date").GetString().Should().Be("2026-06-02");
        weeks[1].GetProperty("weekStart").GetDateTime().Should().Be(new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc));
        weeks[1].GetProperty("days").GetArrayLength().Should().Be(7);
        weeks[1].GetProperty("activityCount").GetInt32().Should().Be(0);
        weeks[2].GetProperty("weekStart").GetDateTime().Should().Be(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ActivityLog_FilterAppliesBeforeTotals()
    {
        var profileId = $"log-filter-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var date = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc);
        var createdAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        await SeedCompletionAsync(profileId, date, "plan-input", "Reading", 25, createdAt: createdAt);
        await SeedCompletionAsync(profileId, date, "plan-output", "Translation", 17, createdAt: createdAt.AddSeconds(1));
        var client = ClientWithJwt(profileId);

        var weeks = await GetActivityLogAsync(client, "2026-05-18T00:00:00Z", "2026-05-24T23:59:59Z", "Input");

        var week = weeks[0];
        week.GetProperty("totalMinutes").GetInt32().Should().Be(25);
        week.GetProperty("inputMinutes").GetInt32().Should().Be(25);
        week.GetProperty("outputMinutes").GetInt32().Should().Be(0);
        week.GetProperty("activityCount").GetInt32().Should().Be(1);
        var day = FindDay(week, "2026-05-22");
        day.GetProperty("totalMinutes").GetInt32().Should().Be(25);
        var entry = day.GetProperty("plans")[0].GetProperty("entries")[0];
        entry.GetProperty("category").GetString().Should().Be("Input");
        entry.GetProperty("activityType").GetString().Should().Be("Reading");
    }

    [Fact]
    public async Task ActivityLog_AdHocRowsAreDisplayedAsAdHocGroups()
    {
        var profileId = $"log-adhoc-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var date = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc);
        var createdAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        await SeedCompletionAsync(profileId, date, "planned-nearby", "Reading", 12, createdAt: createdAt);
        await SeedCompletionAsync(profileId, date, "adhoc-11111111-1111-1111-1111-111111111111", "Translation", 10, createdAt: createdAt.AddSeconds(5));
        var client = ClientWithJwt(profileId);

        var weeks = await GetActivityLogAsync(client, "2026-05-18T00:00:00Z", "2026-05-24T23:59:59Z");

        var plans = FindDay(weeks[0], "2026-05-22").GetProperty("plans");
        plans.GetArrayLength().Should().Be(2);
        plans[0].GetProperty("displayName").GetString().Should().Be("Plan 1");
        plans[0].GetProperty("isAdhoc").GetBoolean().Should().BeFalse();
        plans[1].GetProperty("displayName").GetString().Should().Be("Ad-hoc");
        plans[1].GetProperty("isAdhoc").GetBoolean().Should().BeTrue();
        plans[1].GetProperty("planItemId").GetString().Should().StartWith("adhoc-");
    }

    [Fact]
    public async Task ActivityLog_ReturnsResourceAndSkillLabelsWhenAvailable()
    {
        var profileId = $"log-labels-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var resourceId = $"resource-{Guid.NewGuid():N}";
        var skillId = $"skill-{Guid.NewGuid():N}";
        await SeedResourceAndSkillAsync(profileId, resourceId, "Korean News Clip", skillId, "Present Tense");
        await SeedCompletionAsync(
            profileId,
            new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            "plan-labels",
            "Reading",
            12,
            resourceId: resourceId,
            skillId: skillId,
            createdAt: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));
        var client = ClientWithJwt(profileId);

        var weeks = await GetActivityLogAsync(client, "2026-05-18T00:00:00Z", "2026-05-24T23:59:59Z");

        var entry = FindDay(weeks[0], "2026-05-22").GetProperty("plans")[0].GetProperty("entries")[0];
        entry.GetProperty("resourceTitle").GetString().Should().Be("Korean News Clip");
        entry.GetProperty("skillName").GetString().Should().Be("Present Tense");
    }

    [Fact]
    public async Task ActivityLog_DoesNotReturnOtherUsersActivity()
    {
        var ownerProfileId = $"log-owner-{Guid.NewGuid():N}";
        var otherProfileId = $"log-other-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, ownerProfileId);
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, otherProfileId);
        await SeedCompletionAsync(
            ownerProfileId,
            new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            "owner-plan",
            "Reading",
            12,
            createdAt: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));
        var otherClient = ClientWithJwt(otherProfileId);

        var weeks = await GetActivityLogAsync(otherClient, "2026-05-18T00:00:00Z", "2026-05-24T23:59:59Z");

        weeks.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task StartAdHoc_CreatesCompletionWithDeterministicPlanItemId()
    {
        var profileId = $"adhoc-create-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var clientSessionId = Guid.NewGuid().ToString("D");
        var client = ClientWithJwt(profileId);

        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new
        {
            clientSessionId,
            activityType = "Translation",
            estimatedMinutes = 15
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<StartAdHocResponseShape>(JsonOptions);
        dto.Should().NotBeNull();
        dto!.PlanItemId.Should().Be($"adhoc-{clientSessionId}");
        dto.ActivityType.Should().Be("Translation");
        dto.EstimatedMinutes.Should().Be(15);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(c => c.UserProfileId == profileId && c.PlanItemId == $"adhoc-{clientSessionId}");
        row.ActivityType.Should().Be("Translation");
        row.EstimatedMinutes.Should().Be(15);
        row.IsCompleted.Should().BeFalse();
        row.MinutesSpent.Should().Be(0);
        row.TitleKey.Should().Be("Activity_Translation");
    }

    [Fact]
    public async Task StartAdHoc_SameClientSessionSameUserDateIsIdempotent()
    {
        var profileId = $"adhoc-idempotent-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var clientSessionId = Guid.NewGuid().ToString("D");
        var client = ClientWithJwt(profileId);
        var body = new
        {
            clientSessionId,
            activityType = "Translation",
            estimatedMinutes = 10
        };

        var first = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", body);
        var second = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", body);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstDto = await first.Content.ReadFromJsonAsync<StartAdHocResponseShape>(JsonOptions);
        var secondDto = await second.Content.ReadFromJsonAsync<StartAdHocResponseShape>(JsonOptions);
        secondDto.Should().BeEquivalentTo(firstDto);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rowCount = await db.DailyPlanCompletions.AsNoTracking()
            .CountAsync(c => c.UserProfileId == profileId && c.PlanItemId == $"adhoc-{clientSessionId}");
        rowCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAdHoc_SameClientSessionDifferentUsersCreatesSeparateRows()
    {
        var ownerProfileId = $"adhoc-owner-{Guid.NewGuid():N}";
        var otherProfileId = $"adhoc-other-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, ownerProfileId);
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, otherProfileId);
        var clientSessionId = Guid.NewGuid().ToString("D");
        var body = new
        {
            clientSessionId,
            activityType = "Translation",
            estimatedMinutes = 10
        };

        var ownerResponse = await ClientWithJwt(ownerProfileId).PostAsJsonAsync("/api/v1/plans/adhoc/start", body);
        var otherResponse = await ClientWithJwt(otherProfileId).PostAsJsonAsync("/api/v1/plans/adhoc/start", body);

        ownerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        otherResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rowCount = await db.DailyPlanCompletions.AsNoTracking()
            .CountAsync(c => c.PlanItemId == $"adhoc-{clientSessionId}"
                && (c.UserProfileId == ownerProfileId || c.UserProfileId == otherProfileId));
        rowCount.Should().Be(2);
    }

    [Fact]
    public async Task StartAdHoc_ReturnedPlanItemIdCanBeUpdatedThroughProgressEndpoint()
    {
        var profileId = $"adhoc-progress-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var client = ClientWithJwt(profileId);
        var clientSessionId = Guid.NewGuid().ToString("D");

        var start = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new
        {
            clientSessionId,
            activityType = "Writing"
        });
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await start.Content.ReadFromJsonAsync<StartAdHocResponseShape>(JsonOptions);
        dto.Should().NotBeNull();

        var progress = await client.PostAsJsonAsync($"/api/v1/plans/{dto!.Date:yyyy-MM-dd}/items/{dto.PlanItemId}/progress", new
        {
            minutesSpent = 7
        });

        progress.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(c => c.UserProfileId == profileId && c.PlanItemId == dto.PlanItemId);
        row.MinutesSpent.Should().Be(7);
    }

    [Theory]
    [InlineData("/api/v1/activity-log?fromUtc=not-a-date&toUtc=2026-05-24T23:59:59Z")]
    [InlineData("/api/v1/activity-log?fromUtc=2026-05-18T00:00:00Z&toUtc=2026-05-24T23:59:59Z&filter=Both")]
    public async Task ActivityLog_InvalidDateOrFilter_Returns400ProblemDetails(string url)
    {
        var profileId = $"log-invalid-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var client = ClientWithJwt(profileId);

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task StartAdHoc_InvalidActivityType_Returns400ProblemDetails()
    {
        var profileId = $"adhoc-invalid-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var client = ClientWithJwt(profileId);

        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new
        {
            clientSessionId = Guid.NewGuid().ToString("D"),
            activityType = "NotReal"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    private async Task<JsonElement> GetActivityLogAsync(HttpClient client, string fromUtc, string toUtc, string? filter = null)
    {
        var url = $"/api/v1/activity-log?fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}";
        if (filter is not null)
        {
            url += $"&filter={Uri.EscapeDataString(filter)}";
        }

        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return json.RootElement.Clone();
    }

    private static JsonElement FindDay(JsonElement week, string date)
    {
        return week.GetProperty("days")
            .EnumerateArray()
            .Single(d => d.GetProperty("date").GetString() == date);
    }

    private async Task SeedCompletionAsync(
        string userProfileId,
        DateTime date,
        string planItemId,
        string activityType,
        int minutesSpent,
        int estimatedMinutes = 10,
        bool isCompleted = false,
        string? resourceId = null,
        string? skillId = null,
        DateTime? createdAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = createdAt ?? DateTime.UtcNow;
        db.DailyPlanCompletions.Add(new DailyPlanCompletion
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = userProfileId,
            Date = date,
            PlanItemId = planItemId,
            ActivityType = activityType,
            ResourceId = resourceId,
            SkillId = skillId,
            IsCompleted = isCompleted,
            CompletedAt = isCompleted ? now : null,
            MinutesSpent = minutesSpent,
            EstimatedMinutes = estimatedMinutes,
            Priority = 1,
            TitleKey = activityType,
            DescriptionKey = activityType == "Reading" ? "Read and understand today's resource." : string.Empty,
            Rationale = string.Empty,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedResourceAndSkillAsync(
        string userProfileId,
        string resourceId,
        string resourceTitle,
        string skillId,
        string skillTitle)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        db.LearningResources.Add(new LearningResource
        {
            Id = resourceId,
            Title = resourceTitle,
            MediaType = "Article",
            Language = "Korean",
            UserProfileId = userProfileId,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.SkillProfiles.Add(new SkillProfile
        {
            Id = skillId,
            Title = skillTitle,
            Language = "Korean",
            UserProfileId = userProfileId,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private sealed record StartAdHocResponseShape(
        string PlanItemId,
        string ActivityType,
        DateOnly Date,
        int EstimatedMinutes);
}
