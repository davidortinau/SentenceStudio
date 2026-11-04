using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// The cache in front of the paid speech provider: hits skip the provider, failures of the provider
/// are never stored, and failures of the store never fail the request.
/// </summary>
public sealed class SpeechClipCacheTests
{
    private const string LearnerText = "오늘 날씨가 정말 좋네요";

    private readonly FakeSpeechClipProvider _provider = new();
    private readonly InMemorySpeechClipStore _store = new();
    private readonly CapturingLogger<SpeechClipCache> _logger = new();

    private SpeechClipCache Cache(SpeechClipCacheTimeouts? timeouts = null) =>
        new(_logger, _store, timeouts);

    private static readonly SpeechClipCacheTimeouts Short = new()
    {
        SynthesizeRead = TimeSpan.FromMilliseconds(100),
        SynthesizeWrite = TimeSpan.FromMilliseconds(100),
        TimestampedRead = TimeSpan.FromMilliseconds(100),
        TimestampedWrite = TimeSpan.FromMilliseconds(100)
    };

    private static SpeechClipSpec Spec(string text = LearnerText, string voice = "yuna-voice") =>
        SpeechClipSpec.ForSynthesize(voice, "eleven_v3", text);

    private static SpeechClipSpec TimedSpec(string text = LearnerText) =>
        SpeechClipSpec.ForTimestamped("yuna-voice", 0.5f, 0.75f, 1.0f, text);

    [Fact]
    public async Task Hit_returns_the_stored_bytes_without_calling_the_provider()
    {
        var spec = Spec();
        var stored = Encoding.UTF8.GetBytes("stored-clip");
        _store.Blobs[SpeechClipCacheKey.BlobName(spec)] = new(stored, SpeechClipCache.AudioContentType);

        var audio = await Cache().GetOrGenerateAsync(spec, _provider, CancellationToken.None);

        audio.Should().Equal(stored);
        _provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Miss_generates_once_stores_write_if_absent_and_then_serves_the_stored_clip()
    {
        var cache = Cache();
        var spec = Spec();

        var first = await cache.GetOrGenerateAsync(spec, _provider, CancellationToken.None);
        var second = await cache.GetOrGenerateAsync(spec, _provider, CancellationToken.None);

        _provider.Calls.Should().Be(1, "the second request is served from the store");
        second.Should().Equal(first);
        var blob = _store.Blobs.Should().ContainSingle().Subject;
        blob.Key.Should().Be(SpeechClipCacheKey.BlobName(spec));
        blob.Value.ContentType.Should().Be("audio/mpeg");
        _store.OverwriteFlags.Should().Equal(false);
    }

    [Fact]
    public async Task Different_text_or_voice_is_generated_and_stored_separately()
    {
        var cache = Cache();

        await cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);
        await cache.GetOrGenerateAsync(Spec(text: LearnerText + "."), _provider, CancellationToken.None);
        await cache.GetOrGenerateAsync(Spec(voice: "jennie-voice"), _provider, CancellationToken.None);

        _provider.Calls.Should().Be(3);
        _store.Blobs.Should().HaveCount(3);
    }

    [Fact]
    public async Task Provider_failure_propagates_and_nothing_is_stored()
    {
        var cache = Cache();
        _provider.FailWith = new HttpRequestException("provider unavailable");

        var act = () => cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _store.Blobs.Should().BeEmpty();
        _store.Writes.Should().Be(0);

        _provider.FailWith = null;
        var audio = await cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);

        audio.Should().NotBeEmpty();
        _provider.Calls.Should().Be(2, "the failure was not cached, so the retry pays for a real clip");
    }

    [Fact]
    public async Task Empty_audio_is_returned_but_never_stored()
    {
        _provider.ReturnEmptyAudio = true;

        var audio = await Cache().GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);

        audio.Should().BeEmpty();
        _store.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Store_read_failure_falls_back_to_generating_and_is_logged()
    {
        _store.ReadFailure = new Azure.RequestFailedException(403, "denied", "AuthorizationPermissionMismatch", null);

        var audio = await Cache().GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);

        audio.Should().NotBeEmpty();
        _provider.Calls.Should().Be(1);
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Message.Contains("read failed")
            && e.Message.Contains("Status=403")
            && e.Message.Contains("AuthorizationPermissionMismatch"));
    }

    [Fact]
    public async Task Store_write_failure_still_returns_the_generated_audio()
    {
        _store.WriteFailure = new Azure.RequestFailedException(404, "gone", "ContainerNotFound", null);

        var audio = await Cache().GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);

        audio.Should().NotBeEmpty();
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("write failed"));
    }

    [Fact]
    public async Task Unreachable_store_times_out_and_falls_back_quickly()
    {
        _store.HangReads = true;

        var audio = await Cache(Short)
            .GetOrGenerateAsync(Spec(), _provider, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        audio.Should().NotBeEmpty();
        _provider.Calls.Should().Be(1);
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Message.Contains("read timed out after 100 ms"));
    }

    [Fact]
    public void Plain_clips_wait_on_the_store_for_only_a_few_seconds_by_default()
    {
        var defaults = SpeechClipCacheTimeouts.Default;

        defaults.ReadFor(SpeechClipVariant.Synthesize).Should().Be(TimeSpan.FromSeconds(2));
        defaults.WriteFor(SpeechClipVariant.Synthesize).Should().Be(TimeSpan.FromSeconds(3));
        defaults.ReadFor(SpeechClipVariant.Timestamped).Should().Be(TimeSpan.FromSeconds(10));
        defaults.WriteFor(SpeechClipVariant.Timestamped).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Stalled_write_does_not_hold_the_response_beyond_the_default_write_budget()
    {
        // The shipped budget, not a test override: ElevenLabs has already answered, and a store
        // that accepts the upload and stalls may delay the learner by at most 3 s.
        _store.HangWrites = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var audio = await Cache().GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4.5));
        audio.Should().NotBeEmpty();
        _store.Blobs.Should().BeEmpty("a write that ran out of budget stores nothing");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Message.Contains("write timed out after 3000 ms"));
    }

    [Fact]
    public async Task Stalled_read_does_not_hold_the_request_beyond_the_default_read_budget()
    {
        _store.HangReads = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var audio = await Cache().GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3.5));
        audio.Should().NotBeEmpty();
        _provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Stalled_timestamped_write_is_bounded_by_its_own_budget()
    {
        _store.HangWrites = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var clip = await Cache(Short).GetOrGenerateTimestampedAsync(TimedSpec(), _provider, CancellationToken.None);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        clip.Audio.Should().NotBeEmpty();
        _store.Blobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_is_not_mistaken_for_a_store_failure()
    {
        _store.HangReads = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => Cache().GetOrGenerateAsync(Spec(), _provider, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _provider.Calls.Should().Be(0, "a request the caller abandoned must not pay for a clip");
    }

    [Fact]
    public async Task Concurrent_identical_misses_may_both_pay_but_the_first_stored_clip_stays()
    {
        var cache = Cache();
        _provider.Delay = TimeSpan.FromMilliseconds(200);

        var results = await Task.WhenAll(
            cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None),
            cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None));

        _provider.Calls.Should().Be(2);
        var stored = _store.Blobs.Should().ContainSingle().Subject.Value.Content;
        results.Should().Contain(r => r.SequenceEqual(stored));
        _store.OverwriteFlags.Should().Equal(false, false);
    }

    [Fact]
    public async Task No_store_means_every_request_goes_to_the_provider()
    {
        var cache = new SpeechClipCache(_logger, store: null);

        await cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);
        await cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);

        cache.IsEnabled.Should().BeFalse();
        _provider.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Learner_text_never_reaches_the_log()
    {
        var cache = Cache();
        await cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);
        await cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);
        _store.Reset();
        _store.ReadFailure = new InvalidOperationException("boom");
        _store.WriteFailure = new InvalidOperationException("boom");
        await cache.GetOrGenerateAsync(Spec(), _provider, CancellationToken.None);

        _logger.Entries.Should().NotBeEmpty();
        _logger.Entries.Should().NotContain(e => e.Message.Contains("날씨"));
    }

    [Fact]
    public async Task Timestamped_hit_returns_the_stored_audio_and_timings_without_calling_the_provider()
    {
        var cache = Cache();
        var spec = TimedSpec();

        var first = await cache.GetOrGenerateTimestampedAsync(spec, _provider, CancellationToken.None);
        var second = await cache.GetOrGenerateTimestampedAsync(spec, _provider, CancellationToken.None);

        _provider.Calls.Should().Be(1);
        second.Audio.Should().Equal(first.Audio);
        second.DurationSeconds.Should().Be(first.DurationSeconds);
        second.Characters.Should().BeEquivalentTo(first.Characters, o => o.WithStrictOrdering());
        _store.Blobs.Single().Value.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task Timestamped_audio_without_timings_is_returned_but_not_stored()
    {
        _provider.ReturnNoTimings = true;

        var clip = await Cache().GetOrGenerateTimestampedAsync(TimedSpec(), _provider, CancellationToken.None);

        clip.Audio.Should().NotBeEmpty();
        _store.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Timestamped_provider_failure_is_not_stored()
    {
        _provider.FailWith = new HttpRequestException("provider unavailable");

        var act = () => Cache().GetOrGenerateTimestampedAsync(TimedSpec(), _provider, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _store.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Unreadable_stored_entry_is_regenerated_and_replaced()
    {
        var spec = TimedSpec();
        var name = SpeechClipCacheKey.BlobName(spec);
        _store.Blobs[name] = new(Encoding.UTF8.GetBytes("{not json"), SpeechClipCache.TimestampedContentType);

        var clip = await Cache().GetOrGenerateTimestampedAsync(spec, _provider, CancellationToken.None);

        _provider.Calls.Should().Be(1);
        _store.OverwriteFlags.Should().Equal(true);
        var stored = JsonDocument.Parse(_store.Blobs[name].Content).RootElement;
        stored.GetProperty("Audio").GetBytesFromBase64().Should().Equal(clip.Audio);
    }

    [Fact]
    public async Task Empty_stored_audio_blob_is_regenerated_and_replaced()
    {
        var spec = Spec();
        _store.Blobs[SpeechClipCacheKey.BlobName(spec)] = new([], SpeechClipCache.AudioContentType);

        var audio = await Cache().GetOrGenerateAsync(spec, _provider, CancellationToken.None);

        audio.Should().NotBeEmpty();
        _store.OverwriteFlags.Should().Equal(true);
        _store.Blobs[SpeechClipCacheKey.BlobName(spec)].Content.Should().Equal(audio);
    }
}
