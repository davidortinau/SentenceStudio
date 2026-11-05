using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Speech;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Data;
using SentenceStudio.Services.Speech;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// The optional voice settings and raw voice IDs on <c>POST /api/v1/speech/synthesize</c> (added
/// 2026-11-04 so the web and MAUI pages keep their own settings and chosen voices). A request
/// without them is handled exactly as before, down to the stored clip's name.
/// </summary>
public sealed class SpeechSynthesizeSettingsEndpointTests : IClassFixture<SpeechCacheApiFactory>, IDisposable
{
    private const string Yuna = "xi3rF0t7dg7uN2M0WUhr";
    private const string JiYoung = "AW5wrnG1jVizOYY7R1Oo";
    private const string Rachel = "21m00Tcm4TlvDq8ikWAM";

    /// <summary>
    /// The blob name pinned in <see cref="SpeechClipCacheKeyTests"/> for yuna, eleven_v3 and
    /// "안녕하세요" with the settings /synthesize has always used.
    /// </summary>
    private const string PinnedYunaClip =
        "speech-cache/v1/synthesize/991278328e4fa2ee4ac4969432df8ef7c4ee113245d6f632805b27cc269282c7.mp3";

    private readonly SpeechCacheApiFactory _factory;

    public SpeechSynthesizeSettingsEndpointTests(SpeechCacheApiFactory factory)
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

    private HttpClient ClientFor(string userProfileId, WebApplicationFactoryClientSource? source = null)
    {
        var client = source?.CreateClient() ?? _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwtGenerator.GenerateToken(userProfileId: userProfileId));
        return client;
    }

    private HttpClient Learner() => ClientFor($"speech-settings-{Guid.NewGuid():N}");

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string json) =>
        client.PostAsync("/api/v1/speech/synthesize",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<string> AudioUrlAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SynthesizeResponse>())!.AudioUrl!;
    }

    private static string AudioUrlFor(string clip) =>
        "data:audio/mpeg;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(clip));

    [Theory]
    [InlineData("""{"Text":"안녕하세요","VoiceId":"yuna"}""")]
    [InlineData("""{"text":"안녕하세요","voiceId":"yuna","stability":null,"similarityBoost":null,"speed":null}""")]
    public async Task Omitted_settings_produce_the_same_request_response_and_stored_clip_as_before(string json)
    {
        var audioUrl = await AudioUrlAsync(await PostRawAsync(Learner(), json));

        audioUrl.Should().Be(AudioUrlFor("clip-1:" + Yuna), "the response is unchanged");
        _factory.Store.Blobs.Should().ContainSingle().Which.Key.Should().Be(PinnedYunaClip,
            "clips stored before the settings existed must still be found");
        var spec = _factory.Provider.Requests.Should().ContainSingle().Subject;
        spec.Should().Be(SpeechClipSpec.ForSynthesize(Yuna, "eleven_v3", "안녕하세요"));
        spec.Stability.Should().Be(0.75f);
        spec.SimilarityBoost.Should().Be(0.75f);
        spec.Speed.Should().Be(1.0f);
        spec.Style.Should().Be(0f);
        spec.SpeakerBoost.Should().BeTrue();
    }

    [Fact]
    public async Task Explicit_default_settings_share_the_clip_stored_without_them()
    {
        var client = Learner();
        var before = await AudioUrlAsync(await PostRawAsync(client, """{"Text":"안녕하세요","VoiceId":"yuna"}"""));

        var explicitDefaults = await AudioUrlAsync(await client.PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = "안녕하세요", VoiceId = "yuna", Stability = 0.75f, SimilarityBoost = 0.75f, Speed = 1.0f }));

        explicitDefaults.Should().Be(before);
        _factory.Provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Page_settings_reach_the_provider_and_name_their_own_clip()
    {
        // VocabQuiz, HowDoYouSay, MinimalPairs and the word editor have always used 0.5 / 0.75 / 0.85.
        var text = $"사과 {Guid.NewGuid():N}";
        var response = await Learner().PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = text, VoiceId = JiYoung, Stability = 0.5f, SimilarityBoost = 0.75f, Speed = 0.85f });

        await AudioUrlAsync(response);
        var spec = _factory.Provider.Requests.Should().ContainSingle().Subject;
        spec.Should().Be(SpeechClipSpec.ForSynthesize(JiYoung, "eleven_v3", text, 0.5f, 0.75f, 0.85f));
        _factory.Store.Blobs.Should().ContainSingle().Which.Key.Should()
            .Be(SpeechClipCacheKey.BlobName(spec))
            .And.NotBe(SpeechClipCacheKey.BlobName(SpeechClipSpec.ForSynthesize(JiYoung, "eleven_v3", text)));
    }

    [Theory]
    [InlineData(1.5f, 0.75f, 1.0f, 1f, 0.75f, 1.0f)]
    [InlineData(-0.2f, -1f, 1.0f, 0f, 0f, 1.0f)]
    [InlineData(0.5f, 2f, 2.0f, 0.5f, 1f, 1.2f)]
    [InlineData(0.5f, 0.75f, 0.1f, 0.5f, 0.75f, 0.7f)]
    public async Task Settings_are_clamped_like_the_timestamped_endpoint(
        float stability, float similarity, float speed,
        float expectedStability, float expectedSimilarity, float expectedSpeed)
    {
        var response = await Learner().PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = $"clamp {Guid.NewGuid():N}", VoiceId = "yuna", Stability = stability, SimilarityBoost = similarity, Speed = speed });

        await AudioUrlAsync(response);
        var spec = _factory.Provider.Requests.Should().ContainSingle().Subject;
        spec.Stability.Should().Be(expectedStability);
        spec.SimilarityBoost.Should().Be(expectedSimilarity);
        spec.Speed.Should().Be(expectedSpeed);
    }

    [Theory]
    [InlineData("""{"Text":"x","Speed":"NaN"}""")]
    [InlineData("""{"Text":"x","Stability":"Infinity"}""")]
    [InlineData("""{"Text":"x","SimilarityBoost":"-Infinity"}""")]
    [InlineData("""{"Text":"x","Speed":1e39}""")]
    public async Task Non_finite_settings_are_rejected_with_400_and_never_paid_for(string json)
    {
        var response = await PostRawAsync(Learner(), json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Provider.Calls.Should().Be(0);
        _factory.Store.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Settings_do_not_change_the_sign_in_requirement()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = "x", Stability = 0.5f, Speed = 0.85f });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(JiYoung)]
    [InlineData("pNInz6obpgDQGcFmaJgB")]
    public async Task A_built_in_voice_id_is_synthesized_in_that_voice(string voiceId)
    {
        // The IDs SpeechVoicePreferences stores. Before 2026-11-04 the API answered them in Rachel.
        await AudioUrlAsync(await Learner().PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = $"voice {Guid.NewGuid():N}", VoiceId = voiceId }));

        _factory.Provider.Requests.Should().ContainSingle().Which.VoiceId.Should().Be(voiceId);
    }

    [Fact]
    public async Task An_unknown_voice_id_still_gets_the_default_voice()
    {
        await AudioUrlAsync(await Learner().PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = $"voice {Guid.NewGuid():N}", VoiceId = "not-a-voice-this-api-offers" }));

        _factory.Provider.Requests.Should().ContainSingle().Which.VoiceId.Should().Be(Rachel);
    }

    [Fact]
    public async Task An_account_voice_listed_by_voice_discovery_is_synthesized_in_that_voice()
    {
        var discovery = new SpeechVoiceResolverTests.FakeVoiceDiscovery { Voices = ["AccountPickedVoice0001"] };
        using var withDiscovery = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IVoiceDiscoveryService>();
            services.AddSingleton<IVoiceDiscoveryService>(discovery);
        }));
        var client = ClientFor($"speech-account-{Guid.NewGuid():N}", new(withDiscovery.CreateClient));

        await AudioUrlAsync(await client.PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = $"voice {Guid.NewGuid():N}", VoiceId = "AccountPickedVoice0001" }));
        await AudioUrlAsync(await client.PostAsJsonAsync("/api/v1/speech/synthesize",
            new SynthesizeRequest { Text = $"voice {Guid.NewGuid():N}", VoiceId = "AccountPickedVoice0001" }));

        _factory.Provider.Requests.Should().HaveCount(2).And.OnlyContain(spec => spec.VoiceId == "AccountPickedVoice0001");
        discovery.Calls.Should().Be(discovery.SupportedLanguages.Count, "the voice list is read once, not per request");
    }

    [Fact]
    public async Task Timestamped_accepts_a_built_in_voice_id_and_keeps_its_owner_check()
    {
        var owner = $"speech-timed-voice-{Guid.NewGuid():N}";
        var resourceId = await SeedResourceAsync(owner, "가나다라");

        var foreign = await Learner().PostAsJsonAsync("/api/v1/speech/synthesize-timestamped",
            new SynthesizeTimestampedRequest { ResourceId = resourceId, VoiceId = JiYoung });
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Provider.Calls.Should().Be(0);

        var own = await ClientFor(owner).PostAsJsonAsync("/api/v1/speech/synthesize-timestamped",
            new SynthesizeTimestampedRequest { ResourceId = resourceId, VoiceId = JiYoung });
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Provider.Requests.Should().ContainSingle().Which.VoiceId.Should().Be(JiYoung);
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
            Title = "Speech voice test",
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

    /// <summary>Lets <see cref="ClientFor"/> use a derived factory's client.</summary>
    private sealed record WebApplicationFactoryClientSource(Func<HttpClient> CreateClient);
}
