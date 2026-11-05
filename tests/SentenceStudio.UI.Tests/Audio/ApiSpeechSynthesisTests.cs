using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElevenLabs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Speech;
using SentenceStudio.Shared.Models;
using SentenceStudio.UI.Tests.Activities;

namespace SentenceStudio.UI.Tests.Audio;

/// <summary>
/// Web and MAUI speech goes through the API (Captain 2026-11-04, docs/native-port-backports.md B60):
/// the pages' <see cref="ElevenLabsSpeechService"/> and <see cref="ApiVoiceDiscoveryService"/> call
/// <c>/api/v1/speech/*</c> through the real <c>AddApiClients</c> registration, carry the learner's
/// token, keep each page's voice and settings, fail closed without a learner, surface API failures,
/// and need no ElevenLabs key.
/// </summary>
#pragma warning disable CS0618 // TextToSpeechAsync carries an old [Obsolete] note; it is the pages' API.
public sealed class ApiSpeechSynthesisTests : IDisposable
{
    private const string Owner = "speech-owner-a";
    private const string Token = "learner-a-access-token";
    private const string JiYoung = "AW5wrnG1jVizOYY7R1Oo";
    private static readonly byte[] Clip = [0x49, 0x44, 0x33, 0x04, 0x07];

    private readonly DirectoryInfo _appData = Directory.CreateTempSubdirectory("ss-api-speech-");
    private readonly ActivityPageHarness.MemoryPreferences _preferences = new();
    private readonly FakeApi _api = new();
    private readonly ServiceProvider _services;

    public ApiSpeechSynthesisTests()
    {
        _preferences.Set(UserProfileRepository.ActiveProfileIdKey, Owner);
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddSingleton<IPreferencesService>(_preferences);
        services.AddSingleton<DeviceUserScopeProvider>();
        services.AddSingleton<IUserScopeProvider>(sp => sp.GetRequiredService<DeviceUserScopeProvider>());
        services.AddSingleton<IFileSystemService>(new AppData(_appData.FullName));
        services.AddSingleton<IAuthService>(new TokenAuth(Token));
        // Registered by the hosts themselves (WebApp Program.cs, MAUI AddAuthServices).
        services.AddTransient<AuthenticatedHttpMessageHandler>();
        // As on the hosts: every client gets the standard resilience pipeline first (with its
        // retry delay shortened so the failure cases stay fast).
        services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler(options =>
            {
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.UseJitter = false;
            });
            http.ConfigurePrimaryHttpMessageHandler(_api.CreateHandler);
        });
        services.AddApiClients(new Uri("https://api.test"));
        services.AddSingleton<ElevenLabsSpeechService>();
        services.AddSingleton<IVoiceDiscoveryService, ApiVoiceDiscoveryService>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public void Dispose()
    {
        _services.Dispose();
        try { _appData.Delete(recursive: true); } catch (IOException) { }
    }

    private ElevenLabsSpeechService Speech => _services.GetRequiredService<ElevenLabsSpeechService>();

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task A_page_clip_is_synthesized_by_the_api_with_the_pages_voice_settings_and_the_learners_token()
    {
        // VocabQuiz's call: TextToSpeechAsync(text, voice, 0.5f, 0.75f, 0.85f).
        await using var audio = await Speech.TextToSpeechAsync("사과", JiYoung, 0.5f, 0.75f, 0.85f);

        (await ReadAllAsync(audio)).Should().Equal(Clip);
        var request = _api.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be("POST");
        request.Path.Should().Be("/api/v1/speech/synthesize");
        request.Authorization.Should().Be("Bearer " + Token, "the API sees the signed-in learner");
        var body = JsonSerializer.Deserialize<SynthesizeRequest>(request.Body, JsonSerializerOptions.Web)!;
        body.Text.Should().Be("사과");
        body.VoiceId.Should().Be(JiYoung, "the learner's chosen voice is sent as it is stored");
        body.Stability.Should().Be(0.5f);
        body.SimilarityBoost.Should().Be(0.75f);
        body.Speed.Should().Be(0.85f);
    }

    [Fact]
    public async Task The_default_settings_are_the_ones_the_api_uses_without_them()
    {
        // Conversation and NumberDrill rely on the defaults; they equal the API's own, so these
        // requests share the clips stored for native and Flutter requests that send no settings.
        await using var _ = await Speech.TextToSpeechAsync("하나", "jiyoung");

        var body = JsonSerializer.Deserialize<SynthesizeRequest>(_api.Requests.Single().Body, JsonSerializerOptions.Web)!;
        body.VoiceId.Should().Be("jiyoung");
        body.Stability.Should().Be(0.75f);
        body.SimilarityBoost.Should().Be(0.75f);
        body.Speed.Should().Be(1.0f);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Without_an_active_learner_nothing_is_sent_and_the_call_fails_closed(string? owner)
    {
        _preferences.Set(UserProfileRepository.ActiveProfileIdKey, owner ?? string.Empty);
        if (owner is null)
            _preferences.Remove(UserProfileRepository.ActiveProfileIdKey);

        var speak = () => Speech.TextToSpeechAsync("사과", JiYoung);
        var read = () => Speech.GenerateTimestampedAudioAsync(Resource(Owner));
        var voices = () => _services.GetRequiredService<IVoiceDiscoveryService>().GetVoicesForLanguageAsync("Korean");

        await speak.Should().ThrowAsync<UnauthorizedAccessException>();
        await read.Should().ThrowAsync<UnauthorizedAccessException>();
        await voices.Should().ThrowAsync<UnauthorizedAccessException>();
        _api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task An_api_refusal_is_thrown_with_its_status_not_returned_as_silence(HttpStatusCode status)
    {
        _api.Status = status;

        var speak = () => Speech.TextToSpeechAsync("사과", JiYoung);

        (await speak.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("data:audio/mpeg;base64,")]
    [InlineData("data:audio/mpeg;base64,***")]
    [InlineData("https://example.test/clip.mp3")]
    public async Task A_response_without_usable_audio_is_a_failure(string audioUrl)
    {
        _api.AudioUrl = audioUrl;

        var speak = () => Speech.TextToSpeechAsync("사과", JiYoung);

        await speak.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancellation_reaches_the_api_call()
    {
        _api.Delay = TimeSpan.FromSeconds(30);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var speak = () => Speech.TextToSpeechAsync("사과", JiYoung, cancellationToken: cancel.Token);

        await speak.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Reading_audio_comes_from_the_timestamped_endpoint_with_its_timings_in_seconds()
    {
        var resource = Resource(Owner);
        _api.Characters = [new() { Char = "가", StartMs = 0, EndMs = 250 }, new() { Char = "나", StartMs = 250, EndMs = 600 }];
        _api.DurationSeconds = 0.6;

        var result = await Speech.GenerateTimestampedAudioAsync(resource);

        result.AudioData.ToArray().Should().Equal(Clip);
        result.Duration.Should().Be(TimeSpan.FromSeconds(0.6));
        result.Characters.Select(c => (c.Character.ToString(), c.StartTime, c.EndTime)).Should().Equal(
            ("가", 0.0, 0.25), ("나", 0.25, 0.6));
        var request = _api.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/api/v1/speech/synthesize-timestamped");
        request.Authorization.Should().Be("Bearer " + Token);
        var body = JsonSerializer.Deserialize<SynthesizeTimestampedRequest>(request.Body)!;
        body.ResourceId.Should().Be(resource.Id, "the API reads the learner's own copy of the resource");
        body.VoiceId.Should().Be("jiyoung");
        body.Stability.Should().Be(0.5f);
        body.SimilarityBoost.Should().Be(0.75f);
        body.Speed.Should().Be(1.0f);
    }

    [Fact]
    public async Task Reading_audio_is_kept_under_the_same_appdata_names_and_reused()
    {
        var resource = Resource(Owner);
        _api.Characters = [new() { Char = "가", StartMs = 0, EndMs = 250 }];

        var first = await Speech.GenerateTimestampedAudioAsync(resource);
        var second = await Speech.GenerateTimestampedAudioAsync(resource);

        _api.Requests.Should().ContainSingle("the second read uses the device's copy");
        var expected = Path.Combine(_appData.FullName, $"timestamped_{resource.Id}_jiyoung_1.0");
        File.Exists(expected + ".mp3").Should().BeTrue();
        File.Exists(expected + ".json").Should().BeTrue();
        second.AudioData.ToArray().Should().Equal(first.AudioData.ToArray());
        second.Characters.Should().HaveCount(1);
    }

    [Fact]
    public async Task Reading_audio_for_another_learners_resource_is_refused_before_any_request()
    {
        var read = () => Speech.GenerateTimestampedAudioAsync(Resource("speech-owner-b"));

        await read.Should().ThrowAsync<UnauthorizedAccessException>();
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Reading_audio_for_a_resource_the_api_does_not_have_surfaces_404()
    {
        // For example, a resource made on this device that has not synced yet.
        _api.Status = HttpStatusCode.NotFound;

        var read = () => Speech.GenerateTimestampedAudioAsync(Resource(Owner));

        (await read.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.GetFiles(_appData.FullName, "timestamped_*").Should().BeEmpty("a failure is never cached");
    }

    [Fact]
    public async Task Reading_audio_is_one_paid_attempt_with_no_retry_and_the_servers_five_minutes()
    {
        _api.Status = HttpStatusCode.ServiceUnavailable;

        var read = () => Speech.GenerateTimestampedAudioAsync(Resource(Owner));

        await read.Should().ThrowAsync<HttpRequestException>();
        _api.Requests.Should().ContainSingle("the standard pipeline's retry would pay for the clip again");
        var client = _services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(typeof(ITimestampedSpeechApiClient).Name);
        client.Timeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Voice_lists_come_from_the_api_and_are_kept_for_a_day()
    {
        _api.Voices = [new("AW5wrnG1jVizOYY7R1Oo", "Ji-Young", "ko", "Female", null), new("acct-voice", "Mina", "ko", null, null)];
        var discovery = _services.GetRequiredService<IVoiceDiscoveryService>();

        var voices = await discovery.GetVoicesForLanguageAsync("Korean");
        await discovery.GetVoicesForLanguageAsync("Korean");

        voices.Select(v => (v.VoiceId, v.Name, v.Language, v.Gender, v.Accent)).Should().Equal(
            ("AW5wrnG1jVizOYY7R1Oo", "Ji-Young", "ko", "Female", ""), ("acct-voice", "Mina", "ko", "", ""));
        var request = _api.Requests.Should().ContainSingle("the second read is cached").Subject;
        request.Method.Should().Be("GET");
        request.PathAndQuery.Should().Be("/api/v1/speech/voices?language=Korean");
        request.Authorization.Should().Be("Bearer " + Token);

        await discovery.GetVoicesForLanguageAsync("Korean", forceRefresh: true);
        _api.Requests.Should().HaveCount(2);
        (await discovery.GetVoicesForLanguageAsync("Klingon")).Should().BeEmpty();
        _api.Requests.Should().HaveCount(2, "an unsupported language is not asked for");
    }

    [Fact]
    public async Task A_voice_list_failure_is_thrown_not_hidden_as_an_empty_list()
    {
        _api.Status = HttpStatusCode.ServiceUnavailable;

        var voices = () => _services.GetRequiredService<IVoiceDiscoveryService>().GetVoicesForLanguageAsync("Korean");

        await voices.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public void Neither_service_needs_an_elevenlabs_client_or_key()
    {
        // The provider above has no ElevenLabsClient and no configuration at all; ValidateOnBuild
        // would have failed if either service still needed one.
        _services.GetService<ElevenLabsClient>().Should().BeNull();
        typeof(ElevenLabsSpeechService).GetConstructors().SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType == typeof(ElevenLabsClient));
        typeof(ApiVoiceDiscoveryService).GetConstructors().SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType == typeof(ElevenLabsClient));

        var core = new ServiceCollection();
        core.AddSentenceStudioCoreServices();
        core.Should().ContainSingle(d => d.ServiceType == typeof(IVoiceDiscoveryService))
            .Which.ImplementationType.Should().Be(typeof(ApiVoiceDiscoveryService));
    }

    [Fact]
    public void The_web_and_maui_hosts_no_longer_build_an_elevenlabs_client()
    {
        var root = FindRepoRoot();
        foreach (var host in new[]
        {
            Path.Combine(root, "src", "SentenceStudio.WebApp", "Program.cs"),
            Path.Combine(root, "src", "SentenceStudio.MauiCompatibility", "Setup", "SentenceStudioAppBuilder.cs"),
        })
        {
            var code = File.ReadAllLines(host).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            code.Should().NotContain(line => line.Contains("ElevenLabsClient", StringComparison.Ordinal)
                || line.Contains("ElevenLabsKey", StringComparison.Ordinal), host);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static LearningResource Resource(string owner) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserProfileId = owner,
        Title = "Speech test",
        Transcript = "가나",
        Language = "Korean"
    };

    /// <summary>The API's speech routes, recording what arrives (path, query, token, body).</summary>
    private sealed class FakeApi
    {
        public record Request(string Method, string Path, string PathAndQuery, string? Authorization, string Body);

        private readonly List<Request> _requests = new();

        public IReadOnlyList<Request> Requests { get { lock (_requests) return _requests.ToList(); } }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? AudioUrl { get; set; } = "data:audio/mpeg;base64," + Convert.ToBase64String(Clip);
        public List<CharacterTimestamp> Characters { get; set; } = new();
        public double DurationSeconds { get; set; }
        public SpeechApiVoice[] Voices { get; set; } = [];
        public TimeSpan Delay { get; set; }

        public HttpMessageHandler CreateHandler() => new Handler(this);

        private sealed class Handler(FakeApi api) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (api._requests)
                {
                    api._requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath,
                        request.RequestUri.PathAndQuery, request.Headers.Authorization?.ToString(), body));
                }

                if (api.Delay > TimeSpan.Zero)
                    await Task.Delay(api.Delay, cancellationToken);
                if (api.Status != HttpStatusCode.OK)
                    return new HttpResponseMessage(api.Status) { RequestMessage = request };

                object payload = request.RequestUri.AbsolutePath switch
                {
                    "/api/v1/speech/synthesize" => new SynthesizeResponse { AudioUrl = api.AudioUrl },
                    "/api/v1/speech/synthesize-timestamped" => new SynthesizeTimestampedResponse
                    {
                        AudioUrl = api.AudioUrl ?? string.Empty,
                        Characters = api.Characters,
                        DurationSeconds = api.DurationSeconds
                    },
                    "/api/v1/speech/voices" => new { voices = api.Voices },
                    _ => throw new InvalidOperationException("Unexpected route " + request.RequestUri)
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload), RequestMessage = request };
            }
        }
    }

    private sealed class AppData(string root) : IFileSystemService
    {
        public string AppDataDirectory => root;
        public Task<Stream> OpenAppPackageFileAsync(string filename) => throw new FileNotFoundException(filename);
    }

    private sealed class TokenAuth(string token) : IAuthService
    {
        public bool IsSignedIn => true;
        public string? UserName => "learner-a";
        public Task<string?> GetAccessTokenAsync(string[] scopes) => Task.FromResult<string?>(token);
        public Task<bool> HasStoredSessionAsync() => Task.FromResult(true);
        public Task<AuthResult?> SignInAsync() => throw new NotSupportedException();
        public Task<AuthResult?> SignInAsync(string email, string password) => throw new NotSupportedException();
        public Task<AuthResult?> RegisterAsync(string email, string password, string displayName) => throw new NotSupportedException();
        public Task SignOutAsync() => throw new NotSupportedException();
        public Task<bool> DeleteAccountAsync() => throw new NotSupportedException();
        public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword) => throw new NotSupportedException();
    }
}
#pragma warning restore CS0618
