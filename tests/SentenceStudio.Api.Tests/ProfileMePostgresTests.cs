using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoreSync;
using CoreSync.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Api.Tests.Sync;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Two owners on a throwaway PostgreSQL database (APP_OPERATION_PG_TEST_CONNECTION):
/// PUT /api/v1/profile/me changes only the token owner's row. Owner B's token can
/// never change owner A, and a token without an owner writes nothing.
/// </summary>
public sealed class ProfileMePostgresTests : IClassFixture<ProfileMePostgresFixture>
{
    private readonly ProfileMePostgresFixture _fixture;

    public ProfileMePostgresTests(ProfileMePostgresFixture fixture) => _fixture = fixture;

    [ApplicationOperationPostgresFact]
    public async Task OwnerSavesOwnProfile_OtherOwnerUnchanged()
    {
        var (a, b) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.ClientFor(a);

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me",
            ProfileMeEndpointTests.ValidBody("Owner A saved"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var storedA = await _fixture.GetAsync(a);
        Assert.Equal("Owner A saved", storedA.Name);
        Assert.Equal("Japanese", storedA.NativeLanguage);
        Assert.Equal("French", storedA.TargetLanguage);
        Assert.Equal("Korean", storedA.DisplayLanguage);
        Assert.Equal(45, storedA.PreferredSessionMinutes);
        Assert.Equal("C1", storedA.TargetCEFRLevel);
        Assert.Equal($"{a}@example.test", storedA.Email);
        Assert.Equal("sk-a", storedA.OpenAI_APIKey);
        _fixture.AssertSeeded(await _fixture.GetAsync(b), b);
    }

    [ApplicationOperationPostgresFact]
    public async Task OwnerBToken_CannotChangeOwnerA()
    {
        var (a, b) = await _fixture.SeedOwnersAsync();
        using var clientB = _fixture.ClientFor(b);

        // /me always resolves to B: B's save changes B only.
        using (var me = await clientB.PutAsJsonAsync("/api/v1/profile/me",
                   ProfileMeEndpointTests.ValidBody("Owner B saved")))
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        // Naming A in the route is refused before any write.
        using (var direct = await clientB.PutAsJsonAsync($"/api/v1/profile/{a}", new
               {
                   DisplayName = "Hijack", Email = "x@example.test", NativeLanguage = "English",
                   TargetLanguage = "Korean", DisplayLanguage = "English", TargetCefrLevel = "A1",
                   PreferredSessionMinutes = 20, OpenAiApiKey = (string?)null,
                   ElevenLabsApiKey = (string?)null
               }))
            Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);

        _fixture.AssertSeeded(await _fixture.GetAsync(a), a);
        Assert.Equal("Owner B saved", (await _fixture.GetAsync(b)).Name);
    }

    [ApplicationOperationPostgresFact]
    public async Task TokenWithoutOwner_Returns401_AndNoRowChanges()
    {
        var (a, b) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtGenerator.GenerateToken());

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me",
            ProfileMeEndpointTests.ValidBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        _fixture.AssertSeeded(await _fixture.GetAsync(a), a);
        _fixture.AssertSeeded(await _fixture.GetAsync(b), b);
    }

    [ApplicationOperationPostgresFact]
    public async Task InvalidBody_Returns400_AndNoRowChanges()
    {
        var (a, _) = await _fixture.SeedOwnersAsync();
        using var client = _fixture.ClientFor(a);

        using var response = await client.PutAsJsonAsync("/api/v1/profile/me", new
        {
            DisplayName = "Valid", NativeLanguage = "English", TargetLanguage = "Korean",
            DisplayLanguage = "English", PreferredSessionMinutes = 999, TargetCefrLevel = "A1"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _fixture.AssertSeeded(await _fixture.GetAsync(a), a);
    }
}

public sealed class ProfileMePostgresFixture : IAsyncLifetime
{
    private ApplicationOperationPostgresHarness? _database;
    private NativeOwnerSyncPostgresApiFactory? _api;

    public async Task InitializeAsync()
    {
        if (ApplicationOperationPostgresServer.SkipReason is not null)
            return;
        _database = await ApplicationOperationPostgresHarness.CreateAsync("profile_me", migrate: false);
        try
        {
            await using (var db = _database.NewContext())
                await db.Database.EnsureCreatedAsync();
            // The API host provisions nothing at startup here, so match the shared
            // sync fixture and provision CoreSync before any request runs.
            var coreSync = new PostgreSQLSyncProvider(
                new PostgreSQLSyncConfigurationBuilder(_database.ConnectionString)
                    .ConfigureSyncTables().Build(), ProviderMode.Remote);
            await coreSync.ApplyProvisionAsync();
            _api = new NativeOwnerSyncPostgresApiFactory(_database.ConnectionString);
        }
        catch
        {
            await _database.DisposeAsync();
            _database = null;
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        _api?.Dispose();
        if (_database is not null)
            await _database.DisposeAsync();
    }

    internal HttpClient CreateClient() =>
        (_api ?? throw new InvalidOperationException("No PostgreSQL test host.")).CreateClient();

    internal HttpClient ClientFor(string owner)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestJwtGenerator.GenerateToken(userProfileId: owner));
        return client;
    }

    internal async Task<(string A, string B)> SeedOwnersAsync()
    {
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        await using var db = NewContext();
        db.UserProfiles.AddRange(Seed(a, "sk-a"), Seed(b, "sk-b"));
        await db.SaveChangesAsync();
        return (a, b);
    }

    internal async Task<UserProfile> GetAsync(string id)
    {
        await using var db = NewContext();
        return await db.UserProfiles.AsNoTracking().SingleAsync(profile => profile.Id == id);
    }

    internal void AssertSeeded(UserProfile stored, string id)
    {
        Assert.Equal($"Seed {id}", stored.Name);
        Assert.Equal("English", stored.NativeLanguage);
        Assert.Equal("Korean", stored.TargetLanguage);
        Assert.Equal("English", stored.DisplayLanguage);
        Assert.Equal(20, stored.PreferredSessionMinutes);
        Assert.Equal("A1", stored.TargetCEFRLevel);
        Assert.Equal($"{id}@example.test", stored.Email);
    }

    private ApplicationDbContext NewContext() =>
        (_database ?? throw new InvalidOperationException("No PostgreSQL test database.")).NewContext();

    private static UserProfile Seed(string id, string key) => new()
    {
        Id = id, Name = $"Seed {id}", Email = $"{id}@example.test",
        NativeLanguage = "English", TargetLanguage = "Korean", DisplayLanguage = "English",
        PreferredSessionMinutes = 20, TargetCEFRLevel = "A1", OpenAI_APIKey = key,
        CreatedAt = DateTime.UtcNow
    };
}
