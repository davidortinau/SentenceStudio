using System.Net;
using System.Text;
using System.Text.Json;
using ElevenLabs;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// The API's one VoiceDiscoveryService serves every learner (the voices endpoint and the speech
/// voice check at once) and alone owns the account voice list's lifetime: safe for concurrent use,
/// one ElevenLabs call per load, kept from when a load completes, never kept when it failed, and
/// never overwritten by a load that started before a clear or a forced refresh.
/// </summary>
public sealed class VoiceDiscoveryServiceTests
{
    private readonly FakeElevenLabsVoices _elevenLabs = new();
    private readonly CapturingLogger<VoiceDiscoveryService> _logger = new();
    private readonly ManualTime _time = new();

    private ElevenLabsClient Client() =>
        new(new ElevenLabsAuthentication("test-key"), httpClient: new HttpClient(_elevenLabs));

    private VoiceDiscoveryService Discovery() => new(Client(), _logger, time: _time);

    private static string[] Ids(AccountVoiceSnapshot snapshot) => snapshot.Voices.Select(v => v.VoiceId).ToArray();

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
        var gate = _elevenLabs.HoldNextCall();

        var calls = Enumerable.Range(0, 40)
            .Select(i => Task.Run(() => discovery.GetVoicesForLanguageAsync(
                discovery.SupportedLanguages[i % discovery.SupportedLanguages.Count])))
            .ToArray();
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        gate.Release();
        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(list => list.Select(v => v.VoiceId).SequenceEqual(new[] { "voice-a", "voice-b" }));
        _elevenLabs.VoiceListCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_loaded_list_is_reported_as_loaded_from_one_call()
    {
        var snapshot = await Discovery().GetAccountVoicesAsync(CancellationToken.None);

        snapshot.Loaded.Should().BeTrue();
        Ids(snapshot).Should().Equal("voice-a", "voice-b");
        _elevenLabs.Paths.Should().Equal(new[] { "/v1/voices" },
            "one call: the voices' settings are not fetched one by one");
    }

    [Fact]
    public async Task A_failed_load_is_reported_not_kept_and_retried_after_the_backoff()
    {
        var discovery = Discovery();
        _elevenLabs.Status = HttpStatusCode.ServiceUnavailable;

        var failed = await discovery.GetAccountVoicesAsync(CancellationToken.None);
        failed.Loaded.Should().BeFalse("an outage is not an account without voices");
        failed.Voices.Should().BeEmpty();
        // The voices endpoint still answers with the fallback voices, as before.
        (await discovery.GetVoicesForLanguageAsync("Korean")).Should().Contain(v => v.VoiceId == "AW5wrnG1jVizOYY7R1Oo");
        _elevenLabs.VoiceListCalls.Should().Be(1, "within the back-off ElevenLabs is not asked again");

        _elevenLabs.Status = HttpStatusCode.OK;
        _time.Advance(VoiceDiscoveryService.FailureBackoff);

        var recovered = await discovery.GetAccountVoicesAsync(CancellationToken.None);
        recovered.Loaded.Should().BeTrue();
        _elevenLabs.VoiceListCalls.Should().Be(2);
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_loaded_list_is_kept_for_its_lifetime_and_force_refresh_reloads_it()
    {
        var discovery = Discovery();

        await discovery.GetVoicesForLanguageAsync("Korean");
        _time.Advance(VoiceDiscoveryService.AccountVoicesLifetime - TimeSpan.FromHours(1));
        await discovery.GetVoicesForLanguageAsync("English");
        _elevenLabs.VoiceListCalls.Should().Be(1);

        await discovery.GetVoicesForLanguageAsync("Korean", forceRefresh: true);
        _elevenLabs.VoiceListCalls.Should().Be(2);

        _time.Advance(VoiceDiscoveryService.AccountVoicesLifetime);
        await discovery.GetVoicesForLanguageAsync("Korean");
        _elevenLabs.VoiceListCalls.Should().Be(3);
    }

    [Fact]
    public async Task An_expired_list_reuses_a_load_that_is_still_running()
    {
        var discovery = Discovery();
        await discovery.GetAccountVoicesAsync(CancellationToken.None);
        _time.Advance(VoiceDiscoveryService.AccountVoicesLifetime);

        var gate = _elevenLabs.HoldNextCall();
        var first = discovery.GetAccountVoicesAsync(CancellationToken.None);
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromMinutes(1));
        var second = discovery.GetAccountVoicesAsync(CancellationToken.None);
        gate.Release();

        (await first).Loaded.Should().BeTrue();
        (await second).Loaded.Should().BeTrue();
        _elevenLabs.VoiceListCalls.Should().Be(2, "the second caller waited on the running reload");
    }

    [Fact]
    public async Task The_lifetime_starts_when_the_load_completes()
    {
        var discovery = Discovery();
        var gate = _elevenLabs.HoldNextCall();

        var first = discovery.GetAccountVoicesAsync(CancellationToken.None);
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromMinutes(5)); // a slow load
        gate.Release();
        await first;

        _time.Advance(VoiceDiscoveryService.AccountVoicesLifetime - TimeSpan.FromSeconds(1));
        await discovery.GetAccountVoicesAsync(CancellationToken.None);

        _elevenLabs.VoiceListCalls.Should().Be(1, "the list's age counts from completion, not from the start");
    }

    [Fact]
    public async Task A_load_that_started_before_ClearCache_does_not_store_its_older_list()
    {
        var discovery = Discovery();
        _elevenLabs.VoiceIds = ["voice-old"];
        var gate = _elevenLabs.HoldNextCall();
        var before = discovery.GetAccountVoicesAsync(CancellationToken.None);
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));

        discovery.ClearCache();
        _elevenLabs.VoiceIds = ["voice-new"];
        gate.Release();
        Ids(await before).Should().Equal(["voice-old"], "the earlier caller still gets its own answer");

        var after = await discovery.GetAccountVoicesAsync(CancellationToken.None);

        Ids(after).Should().Equal(["voice-new"], "the cleared cache was not refilled by the older load");
        _elevenLabs.VoiceListCalls.Should().Be(2);
    }

    [Fact]
    public async Task Force_refresh_during_a_load_reads_afresh_and_the_older_load_does_not_overwrite_it()
    {
        var discovery = Discovery();
        _elevenLabs.VoiceIds = ["voice-old"];
        var gate = _elevenLabs.HoldNextCall();
        var older = discovery.GetVoicesForLanguageAsync("Korean");
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(10));

        _elevenLabs.VoiceIds = ["voice-new"];
        // Bounded: a refresh that joined the held older read would never finish.
        var refreshed = await discovery.GetVoicesForLanguageAsync("Korean", forceRefresh: true)
            .WaitAsync(TimeSpan.FromSeconds(10));
        refreshed.Select(v => v.VoiceId).Should().Equal(["voice-new"], "a forced refresh never joins an older read");

        gate.Release();
        (await older).Select(v => v.VoiceId).Should().Equal(["voice-old"]);

        Ids(await discovery.GetAccountVoicesAsync(CancellationToken.None)).Should().Equal(["voice-new"]);
        _elevenLabs.VoiceListCalls.Should().Be(2);
    }

    /// <summary>ElevenLabs' voice list route: <see cref="VoiceIds"/> as account voices, or <see cref="Status"/>.</summary>
    internal sealed class FakeElevenLabsVoices : HttpMessageHandler
    {
        private int _voiceListCalls;
        private Hold? _nextHold;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        /// <summary>The account's voice IDs the next list call answers with.</summary>
        public string[] VoiceIds { get; set; } = ["voice-b", "voice-a"];

        public List<string> Paths { get; } = new();

        public int VoiceListCalls => Volatile.Read(ref _voiceListCalls);

        /// <summary>Holds the next list call until released; later calls are not held.</summary>
        public Hold HoldNextCall() => _nextHold = new Hold();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths)
                Paths.Add(path);
            if (path.EndsWith("/settings", StringComparison.Ordinal))
            {
                // A voice's settings (the SDK fetches them one voice at a time unless told not to).
                return Json(request, """{"stability":0.5,"similarity_boost":0.75}""");
            }
            if (!path.EndsWith("/voices", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };

            Interlocked.Increment(ref _voiceListCalls);
            var ids = VoiceIds;
            var hold = Interlocked.Exchange(ref _nextHold, null);
            if (hold is not null)
                await hold.WaitAsync();
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { RequestMessage = request, Content = new StringContent("{}") };

            var voices = ids.Select(id => new Dictionary<string, object>
            {
                ["voice_id"] = id,
                ["name"] = id switch { "voice-a" => "Ara", "voice-b" => "Bora", _ => id },
                ["category"] = "premade",
                ["labels"] = new Dictionary<string, string> { ["gender"] = "female" }
            });
            return Json(request, JsonSerializer.Serialize(new { voices }));
        }

        private static HttpResponseMessage Json(HttpRequestMessage request, string json) => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        internal sealed class Hold
        {
            private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Started => _started.Task;

            public void Release() => _released.TrySetResult();

            public Task WaitAsync()
            {
                _started.TrySetResult();
                return _released.Task;
            }
        }
    }
}
