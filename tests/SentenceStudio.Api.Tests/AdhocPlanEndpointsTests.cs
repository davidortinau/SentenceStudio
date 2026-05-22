using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Integration tests for <c>POST /api/v1/plans/adhoc/start</c>. Reuses the
/// Activity Log factory (same SQLite + JWT setup); the endpoint depends only
/// on <c>IUserScopeProvider</c> and <c>ApplicationDbContext</c>.
/// </summary>
public sealed class AdhocPlanEndpointsTests : IClassFixture<ActivityLogApiFactory>
{
    private readonly ActivityLogApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AdhocPlanEndpointsTests(ActivityLogApiFactory factory)
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
    public async Task Start_NoJwt_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            ActivityType = "Translation",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Start_MissingClientSessionId_Returns400()
    {
        var client = ClientWithJwt($"adhoc-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = null,
            ActivityType = "Translation",
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_InvalidClientSessionId_Returns400()
    {
        var client = ClientWithJwt($"adhoc-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = "not-a-uuid",
            ActivityType = "Translation",
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_InvalidActivityType_Returns400()
    {
        var client = ClientWithJwt($"adhoc-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            ActivityType = "NotARealActivity",
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_NonPositiveEstimatedMinutes_Returns400()
    {
        var client = ClientWithJwt($"adhoc-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            ActivityType = "Translation",
            EstimatedMinutes = 0,
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_ValidRequest_Returns201AndCreatesCompletion()
    {
        var userProfileId = $"adhoc-create-{Guid.NewGuid():N}";
        var clientSessionId = Guid.NewGuid().ToString();
        var client = ClientWithJwt(userProfileId);

        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = clientSessionId,
            ActivityType = "Translation",
            EstimatedMinutes = 15,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AdhocStartResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.PlanItemId.Should().StartWith("adhoc-");
        body.PlanItemId.Should().Contain(clientSessionId);
        body.ActivityType.Should().Be("Translation");
        body.EstimatedMinutes.Should().Be(15);
        body.Date.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");

        // Row actually exists in the DB.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.DailyPlanCompletions
            .SingleAsync(c => c.UserProfileId == userProfileId && c.PlanItemId == body.PlanItemId);
        row.ActivityType.Should().Be("Translation");
        row.EstimatedMinutes.Should().Be(15);
        row.IsCompleted.Should().BeFalse();
        row.MinutesSpent.Should().Be(0);
    }

    [Fact]
    public async Task Start_OmittedEstimatedMinutes_DefaultsTo10()
    {
        var userProfileId = $"adhoc-default-{Guid.NewGuid():N}";
        var client = ClientWithJwt(userProfileId);
        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            ActivityType = "Reading",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AdhocStartResponse>(JsonOptions);
        body!.EstimatedMinutes.Should().Be(10);
    }

    [Fact]
    public async Task Start_ReplayWithSameClientSessionId_Returns200AndSamePlanItemId()
    {
        var userProfileId = $"adhoc-replay-{Guid.NewGuid():N}";
        var clientSessionId = Guid.NewGuid().ToString();
        var client = ClientWithJwt(userProfileId);

        var first = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = clientSessionId,
            ActivityType = "Writing",
            EstimatedMinutes = 20,
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstBody = await first.Content.ReadFromJsonAsync<AdhocStartResponse>(JsonOptions);

        // Replay with same id (and even different EstimatedMinutes) — must
        // not create a duplicate row, must return the same planItemId, and
        // must return 200 OK (not 201).
        var second = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = clientSessionId,
            ActivityType = "Writing",
            EstimatedMinutes = 99,
        });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<AdhocStartResponse>(JsonOptions);
        secondBody!.PlanItemId.Should().Be(firstBody!.PlanItemId);
        // The first call's EstimatedMinutes wins (stored on the row).
        secondBody.EstimatedMinutes.Should().Be(20);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.DailyPlanCompletions
            .Where(c => c.UserProfileId == userProfileId && c.PlanItemId == firstBody.PlanItemId)
            .ToListAsync();
        rows.Should().HaveCount(1, "idempotent replay must not insert a duplicate row");
    }

    [Fact]
    public async Task Start_PlanItemIdUsableByProgressEndpoint()
    {
        var userProfileId = $"adhoc-progress-{Guid.NewGuid():N}";
        var client = ClientWithJwt(userProfileId);
        var response = await client.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            ActivityType = "Translation",
            EstimatedMinutes = 10,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AdhocStartResponse>(JsonOptions);

        // POST progress against the existing /api/v1/plans/{date}/items/{id}/progress
        // endpoint must find the synthetic ad-hoc row. PlanService.UpdateProgressAsync
        // returns NoContent on success, NotFound when no row matches.
        var progress = await client.PostAsJsonAsync(
            $"/api/v1/plans/{body!.Date}/items/{body.PlanItemId}/progress",
            new { minutesSpent = 5 });

        progress.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.DailyPlanCompletions
            .SingleAsync(c => c.UserProfileId == userProfileId && c.PlanItemId == body.PlanItemId);
        row.MinutesSpent.Should().Be(5);
    }

    [Fact]
    public async Task Start_PerUserIsolation_DifferentUsersSameClientSessionIdGetDistinctRows()
    {
        var sharedSessionId = Guid.NewGuid().ToString();
        var userA = $"adhoc-userA-{Guid.NewGuid():N}";
        var userB = $"adhoc-userB-{Guid.NewGuid():N}";

        var clientA = ClientWithJwt(userA);
        var clientB = ClientWithJwt(userB);

        var aResp = await clientA.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = sharedSessionId,
            ActivityType = "Reading",
        });
        var bResp = await clientB.PostAsJsonAsync("/api/v1/plans/adhoc/start", new AdhocStartRequest
        {
            ClientSessionId = sharedSessionId,
            ActivityType = "Reading",
        });

        aResp.StatusCode.Should().Be(HttpStatusCode.Created);
        bResp.StatusCode.Should().Be(HttpStatusCode.Created,
            "user B has never used this clientSessionId; idempotency is scoped per user");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var aRow = await db.DailyPlanCompletions.SingleAsync(c => c.UserProfileId == userA);
        var bRow = await db.DailyPlanCompletions.SingleAsync(c => c.UserProfileId == userB);
        aRow.PlanItemId.Should().Be(bRow.PlanItemId,
            "deterministic id from same clientSessionId — collision is fine because UserProfileId differs");
    }
}
