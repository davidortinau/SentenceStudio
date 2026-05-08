using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Integration tests for ProfileEndpoints (commit 398a7690 review fixes).
///
/// Test status legend:
///   PASS-NOW: passes against current main (proves baseline behaviour holds)
///   GATED:    expected to FAIL against current main, will PASS after Kaylee's
///             fix branch (squad/wash-398a7690-fixes-profile-speech) lands.
///             These tests encode the contract Wash's review demanded.
/// </summary>
public sealed class ProfileEndpointsTests : IClassFixture<ProfileSpeechApiFactory>
{
    private readonly ProfileSpeechApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ProfileEndpointsTests(ProfileSpeechApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient ClientWithJwt(string userProfileId, string? email = null)
    {
        var token = TestJwtGenerator.GenerateToken(
            userProfileId: userProfileId,
            email: email);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---------------------------------------------------------------------
    // GET /api/v1/profile/{id}
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetProfile_NoJwt_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/profile/some-id");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetProfile_OwnJwt_Returns200WithOwnProfile()
    {
        var profileId = $"profile-get-own-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId,
            displayName: "Mal Reynolds", email: "mal@serenity.local",
            targetLanguage: "Korean", preferredSessionMinutes: 25);

        var client = ClientWithJwt(profileId);

        var response = await client.GetAsync($"/api/v1/profile/{profileId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        dto.Should().NotBeNull();
        dto!.Id.Should().Be(profileId);
        dto.DisplayName.Should().Be("Mal Reynolds");
        dto.Email.Should().Be("mal@serenity.local");
        dto.TargetLanguage.Should().Be("Korean");
        dto.PreferredSessionMinutes.Should().Be(25);
    }

    [Fact]
    public async Task GetProfile_MismatchedJwt_Returns403()
    {
        var ownerId = $"profile-owner-{Guid.NewGuid():N}";
        var attackerId = $"profile-attacker-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, ownerId);
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, attackerId);

        var client = ClientWithJwt(attackerId);

        var response = await client.GetAsync($"/api/v1/profile/{ownerId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "IDOR: attacker JWT must not access another user's profile");
    }

    [Fact]
    public async Task GetProfile_JwtWithoutUserProfileIdClaim_Returns401()
    {
        // No user_profile_id claim — endpoint cannot establish ownership.
        var token = TestJwtGenerator.GenerateToken();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/profile/anything");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetProfile_OwnJwtButProfileMissingFromDb_Returns404()
    {
        // Edge case: JWT was issued for a profile that no longer exists in the DB
        // (e.g., admin-deleted profile, or DB wipe). Ownership check passes
        // (claim matches path), but the DB has no row to return. The endpoint
        // must surface 404, not crash.
        var orphanId = $"profile-orphan-{Guid.NewGuid():N}";
        var client = ClientWithJwt(orphanId);

        var response = await client.GetAsync($"/api/v1/profile/{orphanId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------------
    // PUT /api/v1/profile/{id}
    // ---------------------------------------------------------------------

    [Fact]
    public async Task PutProfile_NoJwt_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/v1/profile/anything", new
        {
            DisplayName = "X",
            Email = "x@x.local",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            DisplayLanguage = "English",
            TargetCefrLevel = "A1",
            PreferredSessionMinutes = 20,
            OpenAiApiKey = (string?)null,
            ElevenLabsApiKey = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PutProfile_MismatchedJwt_Returns403()
    {
        var ownerId = $"put-owner-{Guid.NewGuid():N}";
        var attackerId = $"put-attacker-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, ownerId);
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, attackerId);

        var client = ClientWithJwt(attackerId);

        var response = await client.PutAsJsonAsync($"/api/v1/profile/{ownerId}", new
        {
            DisplayName = "Hijack",
            Email = "x@x.local",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            DisplayLanguage = "English",
            TargetCefrLevel = "A1",
            PreferredSessionMinutes = 20,
            OpenAiApiKey = (string?)null,
            ElevenLabsApiKey = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // GATED: requires Kaylee's PR. Current ProfileEndpoints.UpdateProfile has
    // no input validation, so empty DisplayName goes through and returns 200.
    // After fix, must return 400 ValidationProblemDetails.
    [Fact]
    public async Task PutProfile_EmptyDisplayName_Returns400Validation()
    {
        var profileId = $"put-empty-name-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var client = ClientWithJwt(profileId);

        var response = await client.PutAsJsonAsync($"/api/v1/profile/{profileId}", new
        {
            DisplayName = "",
            Email = "ok@ok.local",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            DisplayLanguage = "English",
            TargetCefrLevel = "A1",
            PreferredSessionMinutes = 20,
            OpenAiApiKey = (string?)null,
            ElevenLabsApiKey = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemShape>(JsonOptions);
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("DisplayName");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(481)]
    [InlineData(99999)]
    public async Task PutProfile_PreferredSessionMinutesOutOfRange_Returns400Validation(int minutes)
    {
        var profileId = $"put-minutes-{minutes}-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var client = ClientWithJwt(profileId);

        var response = await client.PutAsJsonAsync($"/api/v1/profile/{profileId}", new
        {
            DisplayName = "Valid Name",
            Email = "ok@ok.local",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            DisplayLanguage = "English",
            TargetCefrLevel = "A1",
            PreferredSessionMinutes = minutes,
            OpenAiApiKey = (string?)null,
            ElevenLabsApiKey = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemShape>(JsonOptions);
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("PreferredSessionMinutes");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing@tld")]
    [InlineData("@no-local-part.com")]
    [InlineData("spaces in@email.com")]
    public async Task PutProfile_MalformedEmail_Returns400Validation(string badEmail)
    {
        var profileId = $"put-email-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var client = ClientWithJwt(profileId);

        var response = await client.PutAsJsonAsync($"/api/v1/profile/{profileId}", new
        {
            DisplayName = "Valid Name",
            Email = badEmail,
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            DisplayLanguage = "English",
            TargetCefrLevel = "A1",
            PreferredSessionMinutes = 20,
            OpenAiApiKey = (string?)null,
            ElevenLabsApiKey = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemShape>(JsonOptions);
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("Email");
    }

    [Fact]
    public async Task PutProfile_ValidUpdate_Returns200_PersistsAndRoundTrips()
    {
        var profileId = $"put-roundtrip-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId,
            displayName: "Original",
            targetLanguage: "Korean");

        var client = ClientWithJwt(profileId);

        var update = new
        {
            DisplayName = "Updated Name",
            Email = "updated@test.local",
            NativeLanguage = "English",
            TargetLanguage = "Spanish",
            DisplayLanguage = "English",
            TargetCefrLevel = "B1",
            PreferredSessionMinutes = 45,
            OpenAiApiKey = "sk-test-rotated",
            ElevenLabsApiKey = "el-test-key"
        };

        var response = await client.PutAsJsonAsync($"/api/v1/profile/{profileId}", update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        dto.Should().NotBeNull();
        dto!.DisplayName.Should().Be(update.DisplayName);
        dto.Email.Should().Be(update.Email);
        dto.TargetLanguage.Should().Be(update.TargetLanguage);
        dto.TargetCefrLevel.Should().Be(update.TargetCefrLevel);
        dto.PreferredSessionMinutes.Should().Be(update.PreferredSessionMinutes);
        dto.OpenAiApiKey.Should().Be(update.OpenAiApiKey);

        // DB-side verification
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.UserProfiles.AsNoTracking()
            .SingleAsync(p => p.Id == profileId);
        persisted.Name.Should().Be(update.DisplayName);
        persisted.Email.Should().Be(update.Email);
        persisted.TargetLanguage.Should().Be(update.TargetLanguage);
        persisted.PreferredSessionMinutes.Should().Be(update.PreferredSessionMinutes);
        persisted.OpenAI_APIKey.Should().Be(update.OpenAiApiKey);
    }

    [Fact]
    public async Task PutProfile_ElevenLabsApiKey_AcceptedButNotPersisted_GetReturnsNull()
    {
        // Forward-compatibility contract documented in ProfileEndpoints.cs:84-86:
        // ElevenLabsApiKey is accepted in PUT but the current UserProfile schema
        // does not persist it server-side, so GET returns null.
        var profileId = $"put-elevenlabs-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var client = ClientWithJwt(profileId);

        var put = await client.PutAsJsonAsync($"/api/v1/profile/{profileId}", new
        {
            DisplayName = "ElevenLabs Tester",
            Email = "el@test.local",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            DisplayLanguage = "English",
            TargetCefrLevel = "A1",
            PreferredSessionMinutes = 20,
            OpenAiApiKey = (string?)null,
            ElevenLabsApiKey = "el-secret-should-not-persist"
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await client.GetAsync($"/api/v1/profile/{profileId}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await get.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        dto.Should().NotBeNull();
        dto!.ElevenLabsApiKey.Should().BeNull(
            "ElevenLabsApiKey is not yet persisted in the UserProfile schema");
    }

    // ---------------------------------------------------------------------
    // Performance / fetch-all regression guard
    // GATED: passes today only by accident (or fails outright). After Kaylee's
    // fix lands, ProfileEndpoints must use a scoped query against the
    // UserProfiles table — no ListAsync().FirstOrDefault.
    // See .squad/decisions.md 2026-05-08.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetProfile_ExecutesScopedQuery_NotFetchAll()
    {
        var ownerId = $"perf-owner-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, ownerId);
        // Pollute table with extra rows so a fetch-all is unambiguously a regression.
        for (var i = 0; i < 5; i++)
        {
            await ProfileTestSeed.SeedProfileAsync(_factory.Services,
                $"perf-noise-{i}-{Guid.NewGuid():N}");
        }

        _factory.QueryCounter.Reset();
        var client = ClientWithJwt(ownerId);

        var response = await client.GetAsync($"/api/v1/profile/{ownerId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.QueryCounter.UserProfilesFetchAllCount.Should().Be(0,
            "GET /profile/{id} must scope by Id at the DB layer, not fetch the entire UserProfiles table. " +
            $"Captured queries: {string.Join(" | ", _factory.QueryCounter.CapturedQueries)}");
        _factory.QueryCounter.UserProfilesSelectCount.Should().BeGreaterThan(0,
            "the endpoint should still query UserProfiles");
    }

    [Fact]
    public async Task PutProfile_ExecutesScopedQuery_NotFetchAll()
    {
        var ownerId = $"perf-put-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, ownerId);
        for (var i = 0; i < 5; i++)
        {
            await ProfileTestSeed.SeedProfileAsync(_factory.Services,
                $"perf-put-noise-{i}-{Guid.NewGuid():N}");
        }

        _factory.QueryCounter.Reset();
        var client = ClientWithJwt(ownerId);

        var response = await client.PutAsJsonAsync($"/api/v1/profile/{ownerId}", new
        {
            DisplayName = "Perf Test",
            Email = "perf@test.local",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            DisplayLanguage = "English",
            TargetCefrLevel = "A1",
            PreferredSessionMinutes = 20,
            OpenAiApiKey = (string?)null,
            ElevenLabsApiKey = (string?)null
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.QueryCounter.UserProfilesFetchAllCount.Should().Be(0,
            "PUT /profile/{id} must scope by Id at the DB layer. " +
            $"Captured queries: {string.Join(" | ", _factory.QueryCounter.CapturedQueries)}");
    }

    private sealed record ValidationProblemShape(
        string? Type,
        string? Title,
        int? Status,
        Dictionary<string, string[]> Errors);
}
