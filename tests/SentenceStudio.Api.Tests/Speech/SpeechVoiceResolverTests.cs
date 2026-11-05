using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Speech;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// Which voice a speech request gets. Slugs resolve as before; a raw ElevenLabs ID is used only when
/// this API offers it; anything else falls back to the default voice and says so. The account voice
/// list is loaded once at a time, kept from when the load completes, and never kept when it failed.
/// </summary>
public sealed class SpeechVoiceResolverTests
{
    private const string JiYoung = "AW5wrnG1jVizOYY7R1Oo";
    private const string Rachel = "21m00Tcm4TlvDq8ikWAM";
    private const string MultilingualAdam = "pNInz6obpgDQGcFmaJgB";
    private const string AccountVoice = "AccountPickedVoice0001";

    private readonly CapturingLogger<SpeechVoiceResolver> _logger = new();
    private readonly FakeAccountVoices _accounts = new();
    private readonly ManualTime _time = new();

    private SpeechVoiceResolver Resolver(
        IAccountVoiceSource? accounts = null,
        IDictionary<string, string?>? settings = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build(),
            _logger, accounts, _time);

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
    public async Task A_loaded_voice_list_is_kept_for_its_lifetime_not_read_per_request()
    {
        _accounts.Voices = [AccountVoice];
        var resolver = Resolver(_accounts);

        for (var i = 0; i < 5; i++)
        {
            await resolver.ResolveAsync(AccountVoice, CancellationToken.None);
            await resolver.ResolveAsync("unknown-" + i, CancellationToken.None);
        }

        _accounts.Calls.Should().Be(1);

        _time.Advance(SpeechVoiceResolver.AccountVoicesLifetime);
        await resolver.ResolveAsync(AccountVoice, CancellationToken.None);

        _accounts.Calls.Should().Be(2, "an expired snapshot is loaded again");
    }

    [Fact]
    public async Task Concurrent_requests_share_one_voice_list_load()
    {
        _accounts.Voices = [AccountVoice];
        _accounts.Gate = new TaskCompletionSource();
        var resolver = Resolver(_accounts);

        var requests = Enumerable.Range(0, 8)
            .Select(_ => resolver.ResolveAsync(AccountVoice, CancellationToken.None))
            .ToArray();
        await _accounts.Started.Task;
        _accounts.Gate.SetResult();
        var all = await Task.WhenAll(requests);

        all.Should().OnlyContain(voice => voice.VoiceId == AccountVoice && !voice.Substituted);
        _accounts.Calls.Should().Be(1);
    }

    [Fact]
    public async Task An_expired_snapshot_reuses_a_load_that_is_still_running()
    {
        _accounts.Voices = [AccountVoice];
        var resolver = Resolver(_accounts);
        await resolver.ResolveAsync(AccountVoice, CancellationToken.None);
        _time.Advance(SpeechVoiceResolver.AccountVoicesLifetime);

        // The reload is slow; more requests arrive while it runs and after more time passes.
        _accounts.Gate = new TaskCompletionSource();
        var first = resolver.ResolveAsync(AccountVoice, CancellationToken.None);
        await _accounts.Started.Task;
        _time.Advance(TimeSpan.FromMinutes(1));
        var second = resolver.ResolveAsync(AccountVoice, CancellationToken.None);
        _accounts.Gate.SetResult();

        (await first).VoiceId.Should().Be(AccountVoice);
        (await second).VoiceId.Should().Be(AccountVoice);
        _accounts.Calls.Should().Be(2, "the second request waited on the running reload");
    }

    [Fact]
    public async Task The_lifetime_starts_when_the_load_completes()
    {
        _accounts.Voices = [AccountVoice];
        _accounts.Gate = new TaskCompletionSource();
        var resolver = Resolver(_accounts);

        var first = resolver.ResolveAsync(AccountVoice, CancellationToken.None);
        await _accounts.Started.Task;
        _time.Advance(TimeSpan.FromMinutes(5)); // a slow load
        _accounts.Gate.SetResult();
        await first;

        _time.Advance(SpeechVoiceResolver.AccountVoicesLifetime - TimeSpan.FromSeconds(1));
        await resolver.ResolveAsync(AccountVoice, CancellationToken.None);

        _accounts.Calls.Should().Be(1, "the snapshot is ten minutes old from completion, not from the start");
    }

    [Fact]
    public async Task A_failed_load_is_never_kept_and_is_retried_after_the_backoff()
    {
        _accounts.Fail = true;
        var resolver = Resolver(_accounts);

        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None))
            .Should().Be(new ResolvedSpeechVoice(Rachel, Substituted: true));
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be checked"));

        // Within the back-off the list is not asked for again.
        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None)).Substituted.Should().BeTrue();
        _accounts.Calls.Should().Be(1);

        // ElevenLabs is back; after the back-off the next request loads it (no ten-minute wait).
        _accounts.Fail = false;
        _accounts.Voices = [AccountVoice];
        _time.Advance(SpeechVoiceResolver.FailureBackoff);

        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None))
            .Should().Be(new ResolvedSpeechVoice(AccountVoice, Substituted: false));
        _accounts.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_throwing_source_is_treated_as_a_failed_load()
    {
        _accounts.Throw = new HttpRequestException("voice list unavailable");
        var resolver = Resolver(_accounts);

        (await resolver.ResolveAsync(AccountVoice, CancellationToken.None)).Substituted.Should().BeTrue();
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Exception is HttpRequestException);
    }

    internal sealed class FakeAccountVoices : IAccountVoiceSource
    {
        private int _calls;

        public string[] Voices { get; set; } = [];

        public bool Fail { get; set; }

        public Exception? Throw { get; set; }

        /// <summary>When set, a load waits for it (signalling <see cref="Started"/> first).</summary>
        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public async Task<AccountVoiceSnapshot> GetAccountVoicesAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Gate is { } gate)
            {
                Started.TrySetResult();
                await gate.Task;
                Gate = null;
                Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (Throw is not null)
                throw Throw;
            return Fail
                ? AccountVoiceSnapshot.Unavailable
                : new AccountVoiceSnapshot(true, Voices.Select(id => new VoiceInfo { VoiceId = id, Name = id }).ToList());
        }
    }

    internal sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
