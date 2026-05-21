using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Activity;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Integration tests for <c>GET /api/v1/activity-log</c>. Pins the wire shape
/// the Flutter client builds against (see activity-log-api-spec.md): camelCase
/// keys, <c>yyyy-MM-dd</c> day dates, 7-day weeks newest-first, PascalCase
/// enum values for <c>category</c> and <c>activityType</c>.
/// </summary>
public sealed class ActivityLogEndpointsTests : IClassFixture<ActivityLogApiFactory>
{
    private readonly ActivityLogApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ActivityLogEndpointsTests(ActivityLogApiFactory factory)
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

    private async Task SeedCompletionAsync(
        string userProfileId,
        DateTime date,
        string planItemId,
        string activityType,
        int minutesSpent,
        DateTime createdAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.DailyPlanCompletions.Add(new DailyPlanCompletion
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = userProfileId,
            Date = date.Date,
            PlanItemId = planItemId,
            ActivityType = activityType,
            IsCompleted = true,
            CompletedAt = createdAt,
            MinutesSpent = minutesSpent,
            EstimatedMinutes = minutesSpent,
            TitleKey = "PlanItem.Title",
            DescriptionKey = "PlanItem.Description",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetActivityLog_NoJwt_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/activity-log?fromUtc=2026-01-01T00:00:00Z&toUtc=2026-01-07T23:59:59Z");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetActivityLog_MissingDates_Returns400()
    {
        var client = ClientWithJwt($"actlog-bad-{Guid.NewGuid():N}");
        var response = await client.GetAsync("/api/v1/activity-log");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetActivityLog_InvertedRange_Returns400()
    {
        var client = ClientWithJwt($"actlog-bad-{Guid.NewGuid():N}");
        var response = await client.GetAsync(
            "/api/v1/activity-log?fromUtc=2026-01-08T00:00:00Z&toUtc=2026-01-01T00:00:00Z");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetActivityLog_RangeTooLarge_Returns400()
    {
        var client = ClientWithJwt($"actlog-bad-{Guid.NewGuid():N}");
        var response = await client.GetAsync(
            "/api/v1/activity-log?fromUtc=2025-01-01T00:00:00Z&toUtc=2026-01-01T00:00:00Z");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetActivityLog_InvalidFilter_Returns400()
    {
        var client = ClientWithJwt($"actlog-bad-{Guid.NewGuid():N}");
        var response = await client.GetAsync(
            "/api/v1/activity-log?fromUtc=2026-01-01T00:00:00Z&toUtc=2026-01-07T23:59:59Z&filter=Bogus");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetActivityLog_NoActivity_ReturnsEmptyArray()
    {
        var client = ClientWithJwt($"actlog-empty-{Guid.NewGuid():N}");
        var response = await client.GetAsync(
            "/api/v1/activity-log?fromUtc=2026-01-01T00:00:00Z&toUtc=2026-01-07T23:59:59Z");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var weeks = await response.Content.ReadFromJsonAsync<List<ActivityLogWeekDto>>(JsonOptions);
        weeks.Should().NotBeNull();
        weeks!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActivityLog_WithCompletions_RollsUpWeekWithCamelCaseShape()
    {
        var userProfileId = $"actlog-rollup-{Guid.NewGuid():N}";

        // Two completions on a Tuesday — clustered (within 60s) into one Plan.
        // One Input (Reading) + one Output (Writing) so we exercise the day's
        // input/output minute totals.
        var tuesday = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc); // 2026-03-31 is a Tuesday
        var firstCompletion = tuesday.AddHours(13).AddMinutes(42);
        var secondCompletion = firstCompletion.AddSeconds(30);

        await SeedCompletionAsync(userProfileId, tuesday, "item-1", "Reading", 12, firstCompletion);
        await SeedCompletionAsync(userProfileId, tuesday, "item-2", "Writing", 8, secondCompletion);

        var client = ClientWithJwt(userProfileId);
        var response = await client.GetAsync(
            "/api/v1/activity-log?fromUtc=2026-03-30T00:00:00Z&toUtc=2026-04-05T23:59:59Z");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();
        // Wire contract: camelCase keys, PascalCase enum values, yyyy-MM-dd dates.
        raw.Should().Contain("\"weekStart\"");
        raw.Should().Contain("\"hasActivity\"");
        raw.Should().Contain("\"inputMinutes\"");
        raw.Should().Contain("\"outputMinutes\"");
        raw.Should().Contain("\"completedAtUtc\"");
        raw.Should().Contain("\"category\":\"Input\"");
        raw.Should().Contain("\"category\":\"Output\"");
        raw.Should().Contain("\"activityType\":\"Reading\"");
        raw.Should().Contain("\"activityType\":\"Writing\"");
        raw.Should().Contain("\"date\":\"2026-03-31\"");

        var weeks = await response.Content.ReadFromJsonAsync<List<ActivityLogWeekDto>>(JsonOptions);
        weeks.Should().NotBeNull().And.HaveCount(1);

        var week = weeks![0];
        week.Days.Should().HaveCount(7, "weeks always have 7 day entries Mon–Sun");
        week.WeekStart.DayOfWeek.Should().Be(DayOfWeek.Monday);

        var tuesdayDay = week.Days.Single(d => d.Date == "2026-03-31");
        tuesdayDay.HasActivity.Should().BeTrue();
        tuesdayDay.InputMinutes.Should().Be(12);
        tuesdayDay.OutputMinutes.Should().Be(8);
        tuesdayDay.TotalMinutes.Should().Be(20);
        tuesdayDay.AllPlansCompleted.Should().BeTrue();
        tuesdayDay.Plans.Should().HaveCount(1, "the two completions are within 60s of each other");

        var plan = tuesdayDay.Plans[0];
        plan.IsAdhoc.Should().BeFalse();
        plan.DisplayName.Should().Be("Plan 1");
        plan.TotalMinutes.Should().Be(20);
        plan.Completed.Should().BeTrue();
        plan.Entries.Should().HaveCount(2);
        plan.Entries[0].ActivityType.Should().Be("Reading");
        plan.Entries[0].Category.Should().Be("Input");
        plan.Entries[0].CompletedAtUtc.Should().NotBeNull();
        plan.Entries[1].ActivityType.Should().Be("Writing");
        plan.Entries[1].Category.Should().Be("Output");

        // Empty days still present with hasActivity=false and zero totals.
        var monday = week.Days.Single(d => d.Date == "2026-03-30");
        monday.HasActivity.Should().BeFalse();
        monday.Plans.Should().BeEmpty();
        monday.TotalMinutes.Should().Be(0);
    }

    [Fact]
    public async Task GetActivityLog_AdhocCompletion_LabeledAsAdhoc()
    {
        var userProfileId = $"actlog-adhoc-{Guid.NewGuid():N}";
        var wednesday = new DateTime(2026, 4, 1, 14, 0, 0, DateTimeKind.Utc); // Wednesday
        await SeedCompletionAsync(userProfileId, wednesday, "adhoc-001", "Reading", 5, wednesday);

        var client = ClientWithJwt(userProfileId);
        var response = await client.GetAsync(
            "/api/v1/activity-log?fromUtc=2026-03-30T00:00:00Z&toUtc=2026-04-05T23:59:59Z");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var weeks = await response.Content.ReadFromJsonAsync<List<ActivityLogWeekDto>>(JsonOptions);
        var day = weeks!.SelectMany(w => w.Days).Single(d => d.Date == "2026-04-01");
        day.Plans.Should().HaveCount(1);
        day.Plans[0].IsAdhoc.Should().BeTrue();
        day.Plans[0].DisplayName.Should().Be("Ad-hoc");
        day.Plans[0].PlanItemId.Should().Be("adhoc-001");
    }

    [Fact]
    public async Task GetActivityLog_InputFilter_DropsOutputEntries()
    {
        var userProfileId = $"actlog-filter-{Guid.NewGuid():N}";
        var thursday = new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc);
        await SeedCompletionAsync(userProfileId, thursday, "i-1", "Reading", 10, thursday);
        await SeedCompletionAsync(userProfileId, thursday, "o-1", "Writing", 7, thursday.AddSeconds(5));

        var client = ClientWithJwt(userProfileId);
        var response = await client.GetAsync(
            "/api/v1/activity-log?fromUtc=2026-03-30T00:00:00Z&toUtc=2026-04-05T23:59:59Z&filter=Input");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var weeks = await response.Content.ReadFromJsonAsync<List<ActivityLogWeekDto>>(JsonOptions);
        var day = weeks!.SelectMany(w => w.Days).Single(d => d.Date == "2026-04-02");
        day.Plans.Should().HaveCount(1);
        day.Plans[0].Entries.Should().OnlyContain(e => e.Category == "Input");
        day.Plans[0].Entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetActivityLog_PerUserIsolation()
    {
        var meId = $"actlog-me-{Guid.NewGuid():N}";
        var otherId = $"actlog-other-{Guid.NewGuid():N}";
        var friday = new DateTime(2026, 4, 3, 9, 0, 0, DateTimeKind.Utc);
        await SeedCompletionAsync(meId, friday, "me-1", "Reading", 9, friday);
        await SeedCompletionAsync(otherId, friday, "you-1", "Writing", 15, friday);

        var client = ClientWithJwt(meId);
        var response = await client.GetAsync(
            "/api/v1/activity-log?fromUtc=2026-03-30T00:00:00Z&toUtc=2026-04-05T23:59:59Z");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var weeks = await response.Content.ReadFromJsonAsync<List<ActivityLogWeekDto>>(JsonOptions);
        weeks!.SelectMany(w => w.Days)
            .Where(d => d.HasActivity)
            .SelectMany(d => d.Plans)
            .SelectMany(p => p.Entries)
            .Should().OnlyContain(e => e.ActivityType == "Reading",
                "the other user's Writing entry must not appear");
    }
}
