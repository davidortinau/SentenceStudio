using ElevenLabs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Speech;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// Which voice a speech request gets. Slugs resolve as before; a raw ElevenLabs ID is used only when
/// this API offers it; anything else falls back to the default voice and says so. The account voice
/// list belongs to the voice source (VoiceDiscoveryService), which alone decides how long it lives.
/// </summary>
public sealed class SpeechVoiceResolverTests
{
    private const string JiYoung = "AW5wrnG1jVizOYY7R1Oo";
    private const string Rachel = "21m00Tcm4TlvDq8ikWAM";
    private const string MultilingualAdam = "pNInz6obpgDQGcFmaJgB";
    private const string AccountVoice = "AccountPickedVoice0001";

    private readonly CapturingLogger<SpeechVoiceResolver> _logger = new();
    private readonly FakeAccountVoices _accounts = new();

    private SpeechVoiceResolver Resolver(
        IAccountVoiceSource? accounts = null,
        IDictionary<string, string?>? settings = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build(),
            _logger, accounts);

    [Theory]
    [InlineData("jiyoung", JiYoung)]
    [InlineData("yuna", "xi3rF0t7dg7uN2M0WUhr")]
    [InlineData("echo", Rachel)]
    [InlineData("shimmer", "kgG8YXSrynzpPIncHKrx")]
    [InlineData("fable", "5Q0t7uMcjvnagumLfvZi")]
    public async Task Slugs_resolve_exactly_as_before(string slug, string voiceId)
    {
        (await Resolver().ResolveAsync(slug, CancellationToken.None))
            .Should().Be(new ResolvedSpeechVoice(voiceId, Substituted: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task No_voice_gets_the_default_voice_and_is_not_a_substitution(string? requested)
    {
        (await Resolver().ResolveAsync(requested, CancellationToken.None))
            .Should().Be(new ResolvedSpeechVoice(Rachel, Substituted: false));
        (await Resolver(settings: new Dictionary<string, string?> { ["AI:ElevenLabs:DefaultVoice"] = JiYoung })
            .ResolveAsync(requested, CancellationToken.None)).VoiceId.Should().Be(JiYoung);
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
        (await Resolver(_accounts).ResolveAsync(voiceId, CancellationToken.None))
            .Should().Be(new ResolvedSpeechVoice(voiceId, Substituted: false));
        _accounts.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_configured_voice_id_is_used_as_sent()
    {
        var resolver = Resolver(settings: new Dictionary<string, string?>
        {
            ["AI:ElevenLabs:Voices:Korean:Custom"] = "ConfiguredKoreanVoice01"
        });

        (await resolver.ResolveAsync("ConfiguredKoreanVoice01", CancellationToken.None)).VoiceId
            .Should().Be("ConfiguredKoreanVoice01");
    }

    [Fact]
    public async Task An_account_voice_listed_by_the_voices_endpoint_is_used_as_sent()
    {
        _accounts.Voices = [AccountVoice];

        (await Resolver(_accounts).ResolveAsync(AccountVoice, CancellationToken.None))
            .Should().Be(new ResolvedSpeechVoice(AccountVoice, Substituted: false));
    }

    [Fact]
    public async Task An_unknown_voice_falls_back_as_a_substitution_logged_without_its_value()
    {
        const string Unknown = "voice-nobody-offers";
        _accounts.Voices = [AccountVoice];

        var resolved = await Resolver(_accounts).ResolveAsync(Unknown, CancellationToken.None);

        resolved.Should().Be(new ResolvedSpeechVoice(Rachel, Substituted: true));
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("default voice").And.NotContain(Unknown);
    }

    [Fact]
    public async Task Without_voice_discovery_only_built_in_voices_are_accepted()
    {
        var resolver = Resolver(accounts: null);

        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None))
            .Should().Be(new ResolvedSpeechVoice(Rachel, Substituted: true));
        (await resolver.ResolveAsync(JiYoung, CancellationToken.None)).VoiceId.Should().Be(JiYoung);
    }

    [Fact]
    public async Task An_unavailable_voice_list_substitutes_and_says_the_voice_could_not_be_checked()
    {
        _accounts.Fail = true;

        (await Resolver(_accounts).ResolveAsync(AccountVoice, CancellationToken.None))
            .Should().Be(new ResolvedSpeechVoice(Rachel, Substituted: true));
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be checked"));
    }

    [Fact]
    public async Task A_throwing_source_is_treated_as_an_unavailable_list()
    {
        _accounts.Throw = new HttpRequestException("voice list unavailable");

        (await Resolver(_accounts).ResolveAsync(AccountVoice, CancellationToken.None)).Substituted.Should().BeTrue();
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Exception is HttpRequestException);
    }

    [Fact]
    public async Task The_resolver_keeps_no_copy_and_asks_the_source_each_time()
    {
        // One layer owns the list's lifetime: whatever the source answers now is what counts.
        _accounts.Voices = [AccountVoice];
        var resolver = Resolver(_accounts);

        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None)).Substituted.Should().BeFalse();
        _accounts.Voices = [];
        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None)).Substituted.Should().BeTrue();

        _accounts.Calls.Should().Be(2);
    }

    [Fact]
    public async Task With_the_real_voice_source_many_requests_make_one_elevenlabs_call()
    {
        var elevenLabs = new VoiceDiscoveryServiceTests.FakeElevenLabsVoices();
        var discovery = new VoiceDiscoveryService(
            new ElevenLabsClient(new ElevenLabsAuthentication("test-key"), httpClient: new HttpClient(elevenLabs)),
            new CapturingLogger<VoiceDiscoveryService>());
        var resolver = Resolver(discovery);

        var all = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => resolver.ResolveAsync(i % 2 == 0 ? "voice-a" : "voice-nobody-offers", CancellationToken.None)));

        all.Where((_, i) => i % 2 == 0).Should().OnlyContain(v => v.VoiceId == "voice-a" && !v.Substituted);
        all.Where((_, i) => i % 2 == 1).Should().OnlyContain(v => v.Substituted);
        elevenLabs.VoiceListCalls.Should().Be(1);
    }

    internal sealed class FakeAccountVoices : IAccountVoiceSource
    {
        private int _calls;

        public string[] Voices { get; set; } = [];

        public bool Fail { get; set; }

        public Exception? Throw { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public Task<AccountVoiceSnapshot> GetAccountVoicesAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(Fail
                ? AccountVoiceSnapshot.Unavailable
                : new AccountVoiceSnapshot(true, Voices.Select(id => new VoiceInfo { VoiceId = id, Name = id }).ToList()));
        }
    }
}
