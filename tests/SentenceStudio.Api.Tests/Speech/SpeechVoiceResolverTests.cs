using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Speech;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// Which voice a speech request gets. Slugs resolve as before; a raw ElevenLabs ID is used only when
/// this API offers it; anything else falls back to the default voice. The account voice list is
/// read through the voices endpoint's own discovery cache, at most once per snapshot lifetime.
/// </summary>
public sealed class SpeechVoiceResolverTests
{
    private const string JiYoung = "AW5wrnG1jVizOYY7R1Oo";
    private const string Rachel = "21m00Tcm4TlvDq8ikWAM";
    private const string MultilingualAdam = "pNInz6obpgDQGcFmaJgB";
    private const string AccountVoice = "AccountPickedVoice0001";

    private readonly CapturingLogger<SpeechVoiceResolver> _logger = new();
    private readonly FakeVoiceDiscovery _discovery = new();
    private readonly ManualTime _time = new();

    private SpeechVoiceResolver Resolver(
        IVoiceDiscoveryService? discovery = null,
        IDictionary<string, string?>? settings = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build(),
            _logger, discovery, _time);

    [Theory]
    [InlineData("jiyoung", JiYoung)]
    [InlineData("yuna", "xi3rF0t7dg7uN2M0WUhr")]
    [InlineData("echo", Rachel)]
    [InlineData("shimmer", "kgG8YXSrynzpPIncHKrx")]
    [InlineData("fable", "5Q0t7uMcjvnagumLfvZi")]
    public async Task Slugs_resolve_exactly_as_before(string slug, string voiceId)
    {
        (await Resolver().ResolveAsync(slug, CancellationToken.None)).Should().Be(voiceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task No_voice_gets_the_default_voice(string? requested)
    {
        (await Resolver().ResolveAsync(requested, CancellationToken.None)).Should().Be(Rachel);
        (await Resolver(settings: new Dictionary<string, string?> { ["AI:ElevenLabs:DefaultVoice"] = JiYoung })
            .ResolveAsync(requested, CancellationToken.None)).Should().Be(JiYoung);
    }

    [Theory]
    [InlineData(JiYoung)]
    [InlineData("s07IwTCOrCDCaETjUVjx")]
    [InlineData(MultilingualAdam)]
    [InlineData(Rachel)]
    public async Task A_built_in_voice_id_is_used_as_sent_without_asking_for_the_voice_list(string voiceId)
    {
        // These are the IDs SpeechVoicePreferences stores (JiYoung for Korean, multilingual Adam for
        // German, French and Spanish). Before 2026-11-04 every one of them fell back to Rachel.
        (await Resolver(_discovery).ResolveAsync(voiceId, CancellationToken.None)).Should().Be(voiceId);
        _discovery.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_configured_voice_id_is_used_as_sent()
    {
        var resolver = Resolver(settings: new Dictionary<string, string?>
        {
            ["AI:ElevenLabs:Voices:Korean:Custom"] = "ConfiguredKoreanVoice01"
        });

        (await resolver.ResolveAsync("ConfiguredKoreanVoice01", CancellationToken.None))
            .Should().Be("ConfiguredKoreanVoice01");
    }

    [Fact]
    public async Task An_account_voice_listed_by_the_voices_endpoint_is_used_as_sent()
    {
        _discovery.Voices = [AccountVoice];

        (await Resolver(_discovery).ResolveAsync(AccountVoice, CancellationToken.None)).Should().Be(AccountVoice);
    }

    [Fact]
    public async Task An_unknown_voice_falls_back_and_is_logged_at_information_without_its_value()
    {
        const string Unknown = "voice-nobody-offers";
        _discovery.Voices = [AccountVoice];

        var resolved = await Resolver(_discovery).ResolveAsync(Unknown, CancellationToken.None);

        resolved.Should().Be(Rachel);
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("default voice").And.NotContain(Unknown);
    }

    [Fact]
    public async Task Without_voice_discovery_only_built_in_voices_are_accepted()
    {
        var resolver = Resolver(discovery: null);

        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None)).Should().Be(Rachel);
        (await resolver.ResolveAsync(JiYoung, CancellationToken.None)).Should().Be(JiYoung);
    }

    [Fact]
    public async Task The_voice_list_is_read_once_per_snapshot_lifetime_not_per_request()
    {
        _discovery.Voices = [AccountVoice];
        var resolver = Resolver(_discovery);
        var languages = _discovery.SupportedLanguages.Count;

        for (var i = 0; i < 5; i++)
        {
            await resolver.ResolveAsync(AccountVoice, CancellationToken.None);
            await resolver.ResolveAsync("unknown-" + i, CancellationToken.None);
        }

        _discovery.Calls.Should().Be(languages, "one snapshot reads each language's cached list once");

        _time.Advance(SpeechVoiceResolver.AccountVoicesLifetime);
        await resolver.ResolveAsync(AccountVoice, CancellationToken.None);

        _discovery.Calls.Should().Be(languages * 2, "an expired snapshot is read again");
    }

    [Fact]
    public async Task Concurrent_requests_share_one_voice_list_load()
    {
        _discovery.Voices = [AccountVoice];
        _discovery.Delay = TimeSpan.FromMilliseconds(50);
        var resolver = Resolver(_discovery);

        var all = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => resolver.ResolveAsync(AccountVoice, CancellationToken.None)));

        all.Should().OnlyContain(voice => voice == AccountVoice);
        _discovery.Calls.Should().Be(_discovery.SupportedLanguages.Count);
    }

    [Fact]
    public async Task A_failing_voice_list_falls_back_and_is_not_retried_within_the_lifetime()
    {
        _discovery.FailWith = new HttpRequestException("voice list unavailable");
        var resolver = Resolver(_discovery);

        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None)).Should().Be(Rachel);
        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None)).Should().Be(Rachel);

        _discovery.Calls.Should().Be(1, "the failed snapshot is kept until it expires");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Exception is HttpRequestException);

        _discovery.FailWith = null;
        _discovery.Voices = [AccountVoice];
        _time.Advance(SpeechVoiceResolver.AccountVoicesLifetime);

        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None)).Should().Be(AccountVoice);
    }

    internal sealed class FakeVoiceDiscovery : IVoiceDiscoveryService
    {
        private int _calls;

        public string[] Voices { get; set; } = [];

        public Exception? FailWith { get; set; }

        public TimeSpan Delay { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<string> SupportedLanguages { get; } = ["English", "French", "German", "Korean", "Spanish"];

        public async Task<List<VoiceInfo>> GetVoicesForLanguageAsync(string language, bool forceRefresh = false)
        {
            Interlocked.Increment(ref _calls);
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay);
            if (FailWith is not null)
                throw FailWith;
            return Voices.Select(id => new VoiceInfo { VoiceId = id, Name = id, Language = language }).ToList();
        }

        public string? GetLanguageCode(string language) => null;

        public void ClearCache() { }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
