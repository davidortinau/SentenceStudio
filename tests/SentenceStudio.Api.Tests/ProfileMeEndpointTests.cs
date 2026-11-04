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
/// PUT /api/v1/profile/me (native profile Save, Captain decision 8d, 2026-11-04):
/// the owner comes only from the token, only the learner-editable fields change,
/// and an absent owner, a missing row or an invalid body writes nothing.
/// The two-owner proof on real PostgreSQL is in <c>ProfileMePostgresTests</c>.
/// </summary>
public sealed class ProfileMeEndpointTests : IClassFixture<ProfileSpeechApiFactory>
{
    private readonly ProfileSpeechApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ProfileMeEndpointTests(ProfileSpeechApiFactory factory) => _factory = factory;

    internal static object ValidBody(string name = "Saved Name") => new
    {
        DisplayName = name,
        NativeLanguage = "Japanese",
        TargetLanguage = "French",
        DisplayLanguage = "Korean",
        PreferredSessionMinutes = 45,
        TargetCefrLevel = "C1"
    };

    [Fact]
    public async Task OwnToken_SavesOnlyEditableFields_AndKeepsEmailAndKey()
    {
        var owner = $"me-own-{Guid.NewGuid():N}";
        await SeedAsync(owner, email: "keep@test.local", openAiKey: "sk-keep");
        using var client = ClientFor(owner);

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me",
            ValidBody("  Saved Name  "));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<ProfileDto>(JsonOptions);
        dto!.Id.Should().Be(owner);
        dto.DisplayName.Should().Be("Saved Name");
        dto.Email.Should().Be("keep@test.local");
        var stored = await GetAsync(owner);
        stored.Name.Should().Be("Saved Name");
        stored.NativeLanguage.Should().Be("Japanese");
        stored.TargetLanguage.Should().Be("French");
        stored.DisplayLanguage.Should().Be("Korean");
        stored.PreferredSessionMinutes.Should().Be(45);
        stored.TargetCEFRLevel.Should().Be("C1");
        stored.Email.Should().Be("keep@test.local");
        stored.OpenAI_APIKey.Should().Be("sk-keep");
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenWithoutOwnerClaim_Returns401_AndWritesNothing()
    {
        var bystander = $"me-noclaim-{Guid.NewGuid():N}";
        await SeedAsync(bystander);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtGenerator.GenerateToken());

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await GetAsync(bystander)).Name.Should().Be($"Seed {bystander}");
    }

    [Fact]
    public async Task OwnerWithoutRow_Returns404_AndCreatesNothing()
    {
        var orphan = $"me-orphan-{Guid.NewGuid():N}";
        using var client = ClientFor(orphan);

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.UserProfiles.AnyAsync(profile => profile.Id == orphan)).Should().BeFalse();
    }

    public static TheoryData<string, object> InvalidBodies() => new()
    {
        { "DisplayName", new { DisplayName = "  ", NativeLanguage = "English", TargetLanguage = "Korean",
            DisplayLanguage = "English", PreferredSessionMinutes = 15, TargetCefrLevel = "A2" } },
        { "DisplayName", new { DisplayName = new string('x', 201), NativeLanguage = "English",
            TargetLanguage = "Korean", DisplayLanguage = "English", PreferredSessionMinutes = 15,
            TargetCefrLevel = "A2" } },
        { "NativeLanguage", new { DisplayName = "N", NativeLanguage = "Klingon", TargetLanguage = "Korean",
            DisplayLanguage = "English", PreferredSessionMinutes = 15, TargetCefrLevel = "A2" } },
        { "TargetLanguage", new { DisplayName = "N", NativeLanguage = "English", TargetLanguage = "korean",
            DisplayLanguage = "English", PreferredSessionMinutes = 15, TargetCefrLevel = "A2" } },
        { "DisplayLanguage", new { DisplayName = "N", NativeLanguage = "English", TargetLanguage = "Korean",
            DisplayLanguage = "French", PreferredSessionMinutes = 15, TargetCefrLevel = "A2" } },
        { "PreferredSessionMinutes", new { DisplayName = "N", NativeLanguage = "English",
            TargetLanguage = "Korean", DisplayLanguage = "English", PreferredSessionMinutes = 25,
            TargetCefrLevel = "A2" } },
        { "TargetCefrLevel", new { DisplayName = "N", NativeLanguage = "English", TargetLanguage = "Korean",
            DisplayLanguage = "English", PreferredSessionMinutes = 15, TargetCefrLevel = "D1" } },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task InvalidBody_Returns400_AndWritesNothing(string field, object body)
    {
        var owner = $"me-invalid-{Guid.NewGuid():N}";
        await SeedAsync(owner);
        using var client = ClientFor(owner);

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationShape>(JsonOptions);
        problem!.Errors.Should().ContainKey(field);
        var stored = await GetAsync(owner);
        stored.Name.Should().Be($"Seed {owner}");
        stored.PreferredSessionMinutes.Should().Be(20);
        stored.TargetLanguage.Should().Be("Korean");
    }

    [Fact]
    public async Task UnchangedOutOfListValues_AreKept_WhileTheNameChanges()
    {
        // Older rows can hold values the pickers no longer list (a language outside the
        // shared choices, different casing, a 25-minute session, a non-CEFR level).
        var owner = $"me-legacy-{Guid.NewGuid():N}";
        await SeedAsync(owner, nativeLanguage: "english", targetLanguage: "Italian",
            sessionMinutes: 25, cefr: "Advanced");
        using var client = ClientFor(owner);

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me", new
        {
            DisplayName = "Renamed", NativeLanguage = "english", TargetLanguage = "Italian",
            DisplayLanguage = "English", PreferredSessionMinutes = 25, TargetCefrLevel = "Advanced"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await GetAsync(owner);
        stored.Name.Should().Be("Renamed");
        stored.NativeLanguage.Should().Be("english");
        stored.TargetLanguage.Should().Be("Italian");
        stored.PreferredSessionMinutes.Should().Be(25);
        stored.TargetCEFRLevel.Should().Be("Advanced");
    }

    public static TheoryData<string, object> ChangedOutOfListBodies() => new()
    {
        { "TargetLanguage", new { DisplayName = "Renamed", NativeLanguage = "english",
            TargetLanguage = "Portuguese", DisplayLanguage = "English",
            PreferredSessionMinutes = 25, TargetCefrLevel = "Advanced" } },
        { "NativeLanguage", new { DisplayName = "Renamed", NativeLanguage = "ENGLISH",
            TargetLanguage = "Italian", DisplayLanguage = "English",
            PreferredSessionMinutes = 25, TargetCefrLevel = "Advanced" } },
        { "PreferredSessionMinutes", new { DisplayName = "Renamed", NativeLanguage = "english",
            TargetLanguage = "Italian", DisplayLanguage = "English",
            PreferredSessionMinutes = 35, TargetCefrLevel = "Advanced" } },
        { "TargetCefrLevel", new { DisplayName = "Renamed", NativeLanguage = "english",
            TargetLanguage = "Italian", DisplayLanguage = "English",
            PreferredSessionMinutes = 25, TargetCefrLevel = "Expert" } },
    };

    [Theory]
    [MemberData(nameof(ChangedOutOfListBodies))]
    public async Task ChangedOutOfListValue_IsRefused_AndWritesNothing(string field, object body)
    {
        var owner = $"me-legacy-bad-{Guid.NewGuid():N}";
        await SeedAsync(owner, nativeLanguage: "english", targetLanguage: "Italian",
            sessionMinutes: 25, cefr: "Advanced");
        using var client = ClientFor(owner);

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationShape>(JsonOptions);
        problem!.Errors.Should().ContainKey(field);
        var stored = await GetAsync(owner);
        stored.Name.Should().Be($"Seed {owner}");
        stored.TargetLanguage.Should().Be("Italian");
        stored.PreferredSessionMinutes.Should().Be(25);
    }

    [Fact]
    public async Task AnotherOwnersStoredValue_DoesNotMakeItAcceptable()
    {
        // The unchanged-value rule compares with the caller's own row only.
        var legacy = $"me-legacy-a-{Guid.NewGuid():N}";
        var other = $"me-legacy-b-{Guid.NewGuid():N}";
        await SeedAsync(legacy, targetLanguage: "Italian");
        await SeedAsync(other);
        using var client = ClientFor(other);

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me", new
        {
            DisplayName = "B", NativeLanguage = "English", TargetLanguage = "Italian",
            DisplayLanguage = "English", PreferredSessionMinutes = 20, TargetCefrLevel = "A1"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(other)).TargetLanguage.Should().Be("Korean");
        (await GetAsync(legacy)).Name.Should().Be($"Seed {legacy}");
    }

    [Fact]
    public async Task MeLiteral_DoesNotCollideWithProfileIdRoutes()
    {
        var owner = $"me-route-{Guid.NewGuid():N}";
        await SeedAsync(owner);
        using var client = ClientFor(owner);

        // The /me handler accepts a body without Email or keys; the /{profileId}
        // handler would compare "me" with the claim and answer 403.
        using (var put = await client.PutAsJsonAsync("/api/v1/profile/me", ValidBody()))
            put.StatusCode.Should().Be(HttpStatusCode.OK);
        // GET has no /me route: "me" is a profileId there and is not this owner.
        using (var get = await client.GetAsync("/api/v1/profile/me"))
            get.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // The existing owner-checked routes still work.
        using (var own = await client.GetAsync($"/api/v1/profile/{owner}"))
            own.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private HttpClient ClientFor(string owner)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestJwtGenerator.GenerateToken(userProfileId: owner));
        return client;
    }

    private async Task SeedAsync(string id, string? email = null, string? openAiKey = null,
        string nativeLanguage = "English", string targetLanguage = "Korean",
        int sessionMinutes = 20, string cefr = "A1")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Add(new UserProfile
        {
            Id = id, Name = $"Seed {id}", Email = email ?? $"{id}@test.local",
            NativeLanguage = nativeLanguage, TargetLanguage = targetLanguage,
            DisplayLanguage = "English", PreferredSessionMinutes = sessionMinutes,
            TargetCEFRLevel = cefr, OpenAI_APIKey = openAiKey, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<UserProfile> GetAsync(string id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.UserProfiles.AsNoTracking().SingleAsync(profile => profile.Id == id);
    }

    private sealed record ValidationShape(Dictionary<string, string[]> Errors);
}
