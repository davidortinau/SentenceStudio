using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Speech;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// The speech endpoints through the real API host: same responses as before the cache, auth and
/// owner checks unchanged, repeat requests served from the store.
/// </summary>
public sealed class SpeechSynthesisCacheEndpointTests : IClassFixture<SpeechCacheApiFactory>, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SpeechCacheApiFactory _factory;

    public SpeechSynthesisCacheEndpointTests(SpeechCacheApiFactory factory)
    {
        _factory = factory;
        _factory.Provider.Reset();
        _factory.Store.Reset();
    }

    public void Dispose()
    {
        _factory.Provider.Reset();
        _factory.Store.Reset();
    }

    private HttpClient ClientFor(string userProfileId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtGenerator.GenerateToken(userProfileId: userProfileId));
        return client;
    }

    private static string UniqueText() => $"오늘 날씨가 좋네요 {Guid.NewGuid():N}";

    private static async Task<string> SynthesizeAsync(HttpClient client, string text, string? voice = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = text, VoiceId = voice });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SynthesizeResponse>(JsonOptions);
        return body!.AudioUrl!;
    }

    private static string AudioUrlFor(string clip) =>
        "data:audio/mpeg;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(clip));

    [Fact]
    public async Task Synthesize_requires_a_signed_in_user()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = UniqueText() });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Provider.Calls.Should().Be(0);
        _factory.Store.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Synthesize_repeat_request_is_served_from_the_store_without_a_paid_call()
    {
        var client = ClientFor($"speech-cache-{Guid.NewGuid():N}");
        var text = UniqueText();

        var first = await SynthesizeAsync(client, text, "yuna");
        var second = await SynthesizeAsync(client, text, "yuna");

        _factory.Provider.Calls.Should().Be(1);
        first.Should().Be(AudioUrlFor("clip-1:xi3rF0t7dg7uN2M0WUhr"), "the response shape is unchanged");
        second.Should().Be(first, "the stored clip, not a new one, is returned");

        var blobName = _factory.Store.Blobs.Should().ContainSingle().Subject.Key;
        blobName.Should().StartWith("speech-cache/v1/synthesize/").And.NotContain("날씨");
    }

    [Fact]
    public async Task Synthesize_is_shared_across_learners_for_the_same_text_and_voice()
    {
        var text = UniqueText();

        var learnerA = await SynthesizeAsync(ClientFor($"speech-a-{Guid.NewGuid():N}"), text, "yuna");
        var learnerB = await SynthesizeAsync(ClientFor($"speech-b-{Guid.NewGuid():N}"), text, "yuna");

        learnerB.Should().Be(learnerA);
        _factory.Provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Synthesize_with_a_different_voice_is_a_different_clip()
    {
        var client = ClientFor($"speech-voice-{Guid.NewGuid():N}");
        var text = UniqueText();

        var yuna = await SynthesizeAsync(client, text, "yuna");
        var jennie = await SynthesizeAsync(client, text, "jennie");

        jennie.Should().NotBe(yuna);
        _factory.Provider.Calls.Should().Be(2);
        _factory.Store.Blobs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Synthesize_provider_failure_returns_502_and_is_not_cached()
    {
        var client = ClientFor($"speech-fail-{Guid.NewGuid():N}");
        var text = UniqueText();
        _factory.Provider.FailWith = new HttpRequestException("provider unavailable");

        var failed = await client.PostAsJsonAsync("/api/v1/speech/synthesize", new SynthesizeRequest { Text = text });

        failed.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        _factory.Store.Blobs.Should().BeEmpty();

        _factory.Provider.FailWith = null;
        var recovered = await SynthesizeAsync(client, text);

        recovered.Should().StartWith("data:audio/mpeg;base64,");
        _factory.Provider.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Synthesize_still_works_when_the_store_fails()
    {
        var client = ClientFor($"speech-store-down-{Guid.NewGuid():N}");
        var text = UniqueText();
        _factory.Store.ReadFailure = new Azure.RequestFailedException(403, "denied", "AuthorizationPermissionMismatch", null);
        _factory.Store.WriteFailure = new Azure.RequestFailedException(403, "denied", "AuthorizationPermissionMismatch", null);

        var first = await SynthesizeAsync(client, text);
        var second = await SynthesizeAsync(client, text);

        first.Should().StartWith("data:audio/mpeg;base64,");
        second.Should().StartWith("data:audio/mpeg;base64,");
        _factory.Provider.Calls.Should().Be(2, "with the store down every request is generated, as before the cache");
    }

    [Fact]
    public async Task Timestamped_repeat_request_returns_the_stored_audio_and_timestamps()
    {
        var owner = $"speech-timed-{Guid.NewGuid():N}";
        var resourceId = await SeedResourceAsync(owner, "가나다라 마바사");
        var client = ClientFor(owner);

        var first = await SynthesizeTimestampedAsync(client, resourceId);
        var second = await SynthesizeTimestampedAsync(client, resourceId);

        _factory.Provider.Calls.Should().Be(1);
        first.AudioUrl.Should().StartWith("data:audio/mpeg;base64,");
        first.Characters.Should().HaveCount("가나다라 마바사".Length);
        second.AudioUrl.Should().Be(first.AudioUrl);
        second.DurationSeconds.Should().Be(first.DurationSeconds);
        second.Characters.Should().BeEquivalentTo(first.Characters, o => o.WithStrictOrdering());
        _factory.Store.Blobs.Single().Key.Should().StartWith("speech-cache/v1/synthesize-timestamped/");
    }

    [Fact]
    public async Task Timestamped_owner_check_runs_before_the_cache()
    {
        var owner = $"speech-owner-{Guid.NewGuid():N}";
        var other = $"speech-other-{Guid.NewGuid():N}";
        var transcript = $"같은 대본 {Guid.NewGuid():N}";
        var ownersResource = await SeedResourceAsync(owner, transcript);
        await SynthesizeTimestampedAsync(ClientFor(owner), ownersResource);

        var foreign = await ClientFor(other).PostAsJsonAsync("/api/v1/speech/synthesize-timestamped",
            new SynthesizeTimestampedRequest { ResourceId = ownersResource });

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a stored clip must not let another learner read a resource they do not own");

        // The same transcript in the other learner's own resource shares the stored clip.
        var othersResource = await SeedResourceAsync(other, transcript);
        var shared = await SynthesizeTimestampedAsync(ClientFor(other), othersResource);
        shared.AudioUrl.Should().NotBeNullOrEmpty();
        _factory.Provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Timestamped_provider_failure_returns_502_and_is_not_cached()
    {
        var owner = $"speech-timed-fail-{Guid.NewGuid():N}";
        var resourceId = await SeedResourceAsync(owner, "실패 테스트");
        _factory.Provider.FailWith = new HttpRequestException("provider unavailable");

        var response = await ClientFor(owner).PostAsJsonAsync("/api/v1/speech/synthesize-timestamped",
            new SynthesizeTimestampedRequest { ResourceId = resourceId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        _factory.Store.Blobs.Should().BeEmpty();
    }

    private static async Task<SynthesizeTimestampedResponse> SynthesizeTimestampedAsync(
        HttpClient client,
        string resourceId)
    {
        var response = await client.PostAsJsonAsync("/api/v1/speech/synthesize-timestamped",
            new SynthesizeTimestampedRequest { ResourceId = resourceId });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SynthesizeTimestampedResponse>())!;
    }

    private async Task<string> SeedResourceAsync(string owner, string transcript)
    {
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, owner);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Speech cache test",
            MediaType = "Article",
            Language = "Korean",
            Transcript = transcript,
            UserProfileId = owner,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.LearningResources.Add(resource);
        await db.SaveChangesAsync();
        return resource.Id;
    }
}
