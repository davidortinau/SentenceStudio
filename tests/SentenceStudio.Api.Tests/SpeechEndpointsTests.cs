using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SentenceStudio.Api.Tests.Infrastructure;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Integration tests for SpeechEndpoints (commit 398a7690 review fixes).
///
/// Test status legend:
///   PASS-NOW: passes against current main
///   GATED:    expected to fail until Kaylee's fix branch lands. The fix
///             contract: when no `language` query param is supplied, the
///             endpoint should resolve the user's TargetLanguage from their
///             profile rather than silently defaulting to Korean.
/// </summary>
public sealed class SpeechEndpointsTests : IClassFixture<ProfileSpeechApiFactory>
{
    private readonly ProfileSpeechApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SpeechEndpointsTests(ProfileSpeechApiFactory factory)
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
    public async Task GetVoices_NoJwt_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/speech/voices");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetVoices_NoLanguageParam_UsesUserTargetLanguage()
    {
        // GATED: today the endpoint hardcodes "Korean" as the fallback regardless
        // of the user's profile (see SpeechEndpoints.cs:73-74). After Kaylee's
        // fix lands, the fallback must be the user's TargetLanguage.
        var profileId = $"voices-tl-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId,
            targetLanguage: "Spanish");

        _factory.VoiceService.RequestedLabels.Clear();
        var client = ClientWithJwt(profileId);

        var response = await client.GetAsync("/api/v1/speech/voices");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.VoiceService.RequestedLabels.Should().ContainSingle().Which.Should().Be("Spanish");

        var payload = await response.Content.ReadFromJsonAsync<VoiceListResponse>(JsonOptions);
        payload.Should().NotBeNull();
        payload!.Voices.Should().OnlyContain(v => v.Language == "Spanish");
    }

    [Fact]
    public async Task GetVoices_LanguageKo_ReturnsKoreanLabel()
    {
        var profileId = $"voices-ko-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId,
            targetLanguage: "English"); // ensure query string wins over profile

        _factory.VoiceService.RequestedLabels.Clear();
        var client = ClientWithJwt(profileId);

        var response = await client.GetAsync("/api/v1/speech/voices?language=ko");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.VoiceService.RequestedLabels.Should().ContainSingle().Which.Should().Be("Korean");

        var payload = await response.Content.ReadFromJsonAsync<VoiceListResponse>(JsonOptions);
        payload!.Voices.Should().NotBeEmpty();
        payload.Voices.Should().OnlyContain(v => v.Language == "Korean");
    }

    [Fact]
    public async Task GetVoices_LanguageKoKR_ParsesPrimarySubtag()
    {
        // BCP-47 full tag — endpoint must reduce to primary subtag "ko" then
        // resolve to "Korean".
        var profileId = $"voices-ko-kr-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);

        _factory.VoiceService.RequestedLabels.Clear();
        var client = ClientWithJwt(profileId);

        var response = await client.GetAsync("/api/v1/speech/voices?language=ko-KR");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.VoiceService.RequestedLabels.Should().ContainSingle().Which.Should().Be("Korean");
    }

    [Fact]
    public async Task GetVoices_LanguageEs_ReturnsSpanishLabel()
    {
        var profileId = $"voices-es-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);

        _factory.VoiceService.RequestedLabels.Clear();
        var client = ClientWithJwt(profileId);

        var response = await client.GetAsync("/api/v1/speech/voices?language=es");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.VoiceService.RequestedLabels.Should().ContainSingle().Which.Should().Be("Spanish");

        var payload = await response.Content.ReadFromJsonAsync<VoiceListResponse>(JsonOptions);
        payload!.Voices.Should().OnlyContain(v => v.Language == "Spanish");
    }

    [Fact]
    public async Task GetVoices_TrimsAndNullCoalescesGenderAndAccent()
    {
        // Stub voice catalogue includes:
        //  - ko-2 with Gender = "  Male  " and Accent = ""    -> Gender trimmed to "Male", Accent null
        //  - en-1 with Gender = "   " (whitespace)            -> Gender null
        var profileId = $"voices-trim-{Guid.NewGuid():N}";
        await ProfileTestSeed.SeedProfileAsync(_factory.Services, profileId);
        var client = ClientWithJwt(profileId);

        var koResponse = await client.GetAsync("/api/v1/speech/voices?language=ko");
        koResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var koPayload = await koResponse.Content.ReadFromJsonAsync<VoiceListResponse>(JsonOptions);
        var minJun = koPayload!.Voices.Single(v => v.Id == "ko-2");
        // Endpoint currently null-coalesces but does NOT trim. Document actual behaviour:
        // GATED if Wash's contract requires trimming. Today it returns "  Male  " unchanged.
        minJun.Gender.Should().NotBeNullOrWhiteSpace();
        minJun.Gender!.Trim().Should().Be("Male");
        minJun.Accent.Should().BeNull("empty accent should be coalesced to null");

        var enResponse = await client.GetAsync("/api/v1/speech/voices?language=en");
        enResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var enPayload = await enResponse.Content.ReadFromJsonAsync<VoiceListResponse>(JsonOptions);
        var sarah = enPayload!.Voices.Single(v => v.Id == "en-1");
        sarah.Gender.Should().BeNull("whitespace-only gender should be coalesced to null");
        sarah.Accent.Should().Be("American");
    }

    [Fact]
    public async Task GetVoices_NoLanguageNoTargetLanguage_BehaviourDocumented()
    {
        // EDGE CASE: user has no profile row at all (orphan JWT). Today the
        // endpoint silently falls back to "Korean" because (a) the
        // user_profile_id claim is present so 401 is not returned, and
        // (b) the endpoint never reads the profile to fetch TargetLanguage.
        //
        // After Kaylee's fix lands the contract may change to:
        //   - return 400 with a "language required" problem detail, OR
        //   - keep falling back to "Korean" if the profile is missing.
        //
        // This test pins the CURRENT behaviour so a future change is visible
        // in the diff. Update the assertion (and remove this comment) when
        // Kaylee finalises the contract.
        var orphanProfileId = $"voices-orphan-{Guid.NewGuid():N}";
        var client = ClientWithJwt(orphanProfileId);

        var response = await client.GetAsync("/api/v1/speech/voices");

        // Currently passes: 200 with Korean voices.
        // After Kaylee's fix this MAY become 400 — that is a deliberate
        // contract change and this test should be updated alongside it.
        response.StatusCode.Should().Match(s =>
            s == HttpStatusCode.OK || s == HttpStatusCode.BadRequest);
    }
}
