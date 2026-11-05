using System.Net;
using System.Text;
using ElevenLabs;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// The API's one VoiceDiscoveryService serves every learner (the voices endpoint and the speech
/// voice check at once): it must be safe for concurrent use, read the account's voice list with
/// one ElevenLabs call per load, and say when that list could not be read.
/// </summary>
public sealed class VoiceDiscoveryServiceTests
{
    private readonly FakeElevenLabsVoices _elevenLabs = new();
    private readonly CapturingLogger<VoiceDiscoveryService> _logger = new();

    private ElevenLabsClient Client() =>
        new(new ElevenLabsAuthentication("test-key"), httpClient: new HttpClient(_elevenLabs));

    [Fact]
    public async Task A_cold_load_for_every_language_makes_one_elevenlabs_call()
    {
        var discovery = new VoiceDiscoveryService(Client(), _logger);

        foreach (var language in discovery.SupportedLanguages)
        {
            var voices = await discovery.GetVoicesForLanguageAsync(language);
            voices.Select(v => v.VoiceId).Should().Equal("voice-a", "voice-b");
            voices.Should().OnlyContain(v => v.Language == discovery.GetLanguageCode(language));
        }

        _elevenLabs.VoiceListCalls.Should().Be(1, "the account's voices are the same for every language");
    }

    [Fact]
    public async Task Concurrent_callers_share_one_load_and_all_get_the_list()
    {
        var discovery = new VoiceDiscoveryService(Client(), _logger);
        _elevenLabs.Gate = new TaskCompletionSource();

        var calls = Enumerable.Range(0, 40)
            .Select(i => Task.Run(() => discovery.GetVoicesForLanguageAsync(
                discovery.SupportedLanguages[i % discovery.SupportedLanguages.Count])))
            .ToArray();
        await _elevenLabs.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        _elevenLabs.Gate.SetResult();
        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(list => list.Select(v => v.VoiceId).SequenceEqual(new[] { "voice-a", "voice-b" }));
        _elevenLabs.VoiceListCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_loaded_list_is_reported_as_loaded()
    {
        var discovery = new VoiceDiscoveryService(Client(), _logger);

        var snapshot = await discovery.GetAccountVoicesAsync(CancellationToken.None);

        snapshot.Loaded.Should().BeTrue();
        snapshot.Voices.Select(v => v.VoiceId).Should().Equal("voice-a", "voice-b");
        _elevenLabs.Paths.Should().Equal(new[] { "/v1/voices" },
            "one call: the voices' settings are not fetched one by one");
    }

    [Fact]
    public async Task A_failed_load_is_reported_not_kept_and_retried_after_the_backoff()
    {
        var time = new SpeechVoiceResolverTests.ManualTime();
        var discovery = new VoiceDiscoveryService(Client(), _logger, time: time);
        _elevenLabs.Status = HttpStatusCode.ServiceUnavailable;

        var failed = await discovery.GetAccountVoicesAsync(CancellationToken.None);
        failed.Loaded.Should().BeFalse("an outage is not an account without voices");
        failed.Voices.Should().BeEmpty();
        // The voices endpoint still answers with the fallback voices, as before.
        (await discovery.GetVoicesForLanguageAsync("Korean")).Should().Contain(v => v.VoiceId == "AW5wrnG1jVizOYY7R1Oo");
        _elevenLabs.VoiceListCalls.Should().Be(1, "within the back-off ElevenLabs is not asked again");

        _elevenLabs.Status = HttpStatusCode.OK;
        time.Advance(VoiceDiscoveryService.FailureBackoff);

        var recovered = await discovery.GetAccountVoicesAsync(CancellationToken.None);
        recovered.Loaded.Should().BeTrue();
        _elevenLabs.VoiceListCalls.Should().Be(2);
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_loaded_list_is_kept_for_a_day_and_force_refresh_reloads_it()
    {
        var time = new SpeechVoiceResolverTests.ManualTime();
        var discovery = new VoiceDiscoveryService(Client(), _logger, time: time);

        await discovery.GetVoicesForLanguageAsync("Korean");
        time.Advance(TimeSpan.FromHours(23));
        await discovery.GetVoicesForLanguageAsync("English");
        _elevenLabs.VoiceListCalls.Should().Be(1);

        await discovery.GetVoicesForLanguageAsync("Korean", forceRefresh: true);
        _elevenLabs.VoiceListCalls.Should().Be(2);

        time.Advance(VoiceDiscoveryService.AccountVoicesLifetime);
        await discovery.GetVoicesForLanguageAsync("Korean");
        _elevenLabs.VoiceListCalls.Should().Be(3);
    }

    /// <summary>ElevenLabs' voice list route: two account voices, or <see cref="Status"/>.</summary>
    private sealed class FakeElevenLabsVoices : HttpMessageHandler
    {
        private int _voiceListCalls;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Paths { get; } = new();

        public int VoiceListCalls => Volatile.Read(ref _voiceListCalls);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths)
                Paths.Add(path);
            if (path.EndsWith("/settings", StringComparison.Ordinal))
            {
                // A voice's settings (the SDK fetches them one voice at a time unless told not to).
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("""{"stability":0.5,"similarity_boost":0.75}""", Encoding.UTF8, "application/json")
                };
            }
            if (!path.EndsWith("/voices", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };

            Interlocked.Increment(ref _voiceListCalls);
            Started.TrySetResult();
            if (Gate is { } gate)
                await gate.Task;
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { RequestMessage = request, Content = new StringContent("{}") };

            const string Json = """
                {"voices":[
                  {"voice_id":"voice-b","name":"Bora","category":"premade","labels":{"gender":"female"}},
                  {"voice_id":"voice-a","name":"Ara","category":"cloned","labels":{"gender":"male","accent":"seoul"}}
                ]}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(Json, Encoding.UTF8, "application/json")
            };
        }
    }
}
