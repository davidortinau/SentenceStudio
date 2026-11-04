using System.Text.Json;
using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Api.Speech;

/// <summary>
/// Serves a stored clip for a repeat request and otherwise generates and stores one, so each
/// distinct clip is paid for once across every client and learner.
/// </summary>
/// <remarks>
/// <para>
/// The cache is best effort. A store that is missing, slow or failing is logged and the request is
/// served by the provider exactly as it was before the cache existed. Provider failures propagate
/// unchanged and nothing is stored for them. Empty audio, and timestamped audio without timings,
/// is returned to the caller but never stored.
/// </para>
/// <para>
/// Concurrent identical misses may each pay once. Writes are write-if-absent, so the first stored
/// clip stays and every reader sees one complete upload.
/// </para>
/// <para>
/// The store is never allowed to hold a learner's response for long. Each read and write has a
/// small budget (<see cref="SpeechClipCacheTimeouts"/>); when it runs out the cache gives up on
/// the store for that request, logs it, and the response goes out. A write that runs out is simply
/// not stored, and the next request for that clip pays again.
/// </para>
/// </remarks>
public sealed class SpeechClipCache
{
    public const string AudioContentType = "audio/mpeg";
    public const string TimestampedContentType = "application/json";

    private readonly ISpeechClipStore? _store;
    private readonly ILogger<SpeechClipCache> _logger;
    private readonly SpeechClipCacheTimeouts _timeouts;

    public SpeechClipCache(
        ILogger<SpeechClipCache> logger,
        ISpeechClipStore? store = null,
        SpeechClipCacheTimeouts? timeouts = null)
    {
        _logger = logger;
        _store = store;
        _timeouts = timeouts ?? SpeechClipCacheTimeouts.Default;
    }

    /// <summary>Whether a store is configured. False means every request goes to the provider.</summary>
    public bool IsEnabled => _store is not null;

    public async Task<byte[]> GetOrGenerateAsync(
        SpeechClipSpec spec,
        ISpeechClipProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(provider);

        if (_store is null || string.IsNullOrWhiteSpace(spec.Text))
        {
            return await provider.GenerateAsync(spec, cancellationToken);
        }

        var name = SpeechClipCacheKey.BlobName(spec);
        var lookup = await TryReadAsync(name, spec.Variant, cancellationToken);
        if (lookup.Bytes is { Length: > 0 } stored)
        {
            LogHit(spec.Variant, name, stored.Length);
            return stored;
        }

        var audio = await provider.GenerateAsync(spec, cancellationToken);
        if (audio is not { Length: > 0 })
        {
            _logger.LogWarning(
                "Speech cache: the provider returned no audio; not storing it. Variant={Variant} Key={CacheKey}",
                spec.Variant, SpeechClipCacheKey.LogId(name));
            return audio ?? [];
        }

        await TryWriteAsync(name, audio, AudioContentType, overwrite: lookup.Unreadable, spec.Variant);
        return audio;
    }

    public async Task<TimestampedSpeechClip> GetOrGenerateTimestampedAsync(
        SpeechClipSpec spec,
        ISpeechClipProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(provider);

        if (_store is null || string.IsNullOrWhiteSpace(spec.Text))
        {
            return await provider.GenerateTimestampedAsync(spec, cancellationToken);
        }

        var name = SpeechClipCacheKey.BlobName(spec);
        var lookup = await TryReadAsync(name, spec.Variant, cancellationToken);
        var unreadable = lookup.Unreadable;
        if (lookup.Bytes is { Length: > 0 } bytes)
        {
            var entry = TryParseTimestamped(bytes);
            if (entry is not null)
            {
                LogHit(spec.Variant, name, entry.Audio.Length);
                return entry;
            }

            unreadable = true;
            _logger.LogWarning(
                "Speech cache: stored entry is unreadable; regenerating and replacing it. Variant={Variant} Key={CacheKey}",
                spec.Variant, SpeechClipCacheKey.LogId(name));
        }

        var clip = await provider.GenerateTimestampedAsync(spec, cancellationToken);
        if (!IsComplete(clip))
        {
            _logger.LogWarning(
                "Speech cache: the provider returned no audio or no timings; not storing it. " +
                "Variant={Variant} Key={CacheKey} AudioBytes={AudioBytes} CharCount={CharCount}",
                spec.Variant, SpeechClipCacheKey.LogId(name), clip?.Audio?.Length ?? 0, clip?.Characters?.Count ?? 0);
            return clip!;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new StoredTimestampedClip
        {
            Audio = clip.Audio,
            DurationSeconds = clip.DurationSeconds,
            Characters = clip.Characters.ToList()
        });

        await TryWriteAsync(name, payload, TimestampedContentType, overwrite: unreadable, spec.Variant);
        return clip;
    }

    private async Task<Lookup> TryReadAsync(string name, SpeechClipVariant variant, CancellationToken requestToken)
    {
        var budget = _timeouts.ReadFor(variant);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        timeout.CancelAfter(budget);

        try
        {
            var bytes = await _store!.ReadAsync(name, timeout.Token);
            if (bytes is null)
            {
                return new Lookup(null, Unreadable: false);
            }

            if (bytes.Length == 0)
            {
                _logger.LogWarning(
                    "Speech cache: stored entry is empty; regenerating and replacing it. Variant={Variant} Key={CacheKey}",
                    variant, SpeechClipCacheKey.LogId(name));
                return new Lookup(null, Unreadable: true);
            }

            return new Lookup(bytes, Unreadable: false);
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
        {
            // The caller went away; that is not a cache fault.
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            LogStoreTimeout("read", variant, name, budget);
            return new Lookup(null, Unreadable: false);
        }
        catch (Exception ex)
        {
            LogStoreFailure(ex, "read", variant, name);
            return new Lookup(null, Unreadable: false);
        }
    }

    private async Task TryWriteAsync(
        string name,
        byte[] content,
        string contentType,
        bool overwrite,
        SpeechClipVariant variant)
    {
        // Inline but bounded: the response waits at most this budget for the store. Not linked to
        // the request, so a clip that is already paid for is still kept if the caller has gone.
        var budget = _timeouts.WriteFor(variant);
        using var timeout = new CancellationTokenSource(budget);

        try
        {
            var stored = await _store!.WriteAsync(name, content, contentType, overwrite, timeout.Token);
            if (stored)
            {
                _logger.LogInformation(
                    "Speech cache: stored a generated clip. Variant={Variant} Key={CacheKey} Bytes={Bytes}",
                    variant, SpeechClipCacheKey.LogId(name), content.Length);
            }
            else
            {
                _logger.LogInformation(
                    "Speech cache: a concurrent request already stored this clip. Variant={Variant} Key={CacheKey}",
                    variant, SpeechClipCacheKey.LogId(name));
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            LogStoreTimeout("write", variant, name, budget);
        }
        catch (Exception ex)
        {
            LogStoreFailure(ex, "write", variant, name);
        }
    }

    private void LogStoreTimeout(string operation, SpeechClipVariant variant, string name, TimeSpan budget) =>
        _logger.LogWarning(
            "Speech cache: {Operation} timed out after {BudgetMs} ms; serving from the provider instead. " +
            "Variant={Variant} Key={CacheKey}",
            operation, (int)budget.TotalMilliseconds, variant, SpeechClipCacheKey.LogId(name));

    private void LogHit(SpeechClipVariant variant, string name, int audioBytes) =>
        _logger.LogInformation(
            "Speech cache: served a stored clip. Variant={Variant} Key={CacheKey} AudioBytes={AudioBytes}",
            variant, SpeechClipCacheKey.LogId(name), audioBytes);

    private void LogStoreFailure(Exception ex, string operation, SpeechClipVariant variant, string name)
    {
        // Storage exceptions carry the blob URI and service error, never learner text: the blob
        // name is a hash. Serving continues from the provider.
        var storageFault = ex as Azure.RequestFailedException;
        _logger.LogWarning(
            ex,
            "Speech cache: {Operation} failed; serving from the provider instead. Variant={Variant} Key={CacheKey} " +
            "ExceptionType={ExceptionType} Status={Status} ErrorCode={ErrorCode}",
            operation, variant, SpeechClipCacheKey.LogId(name), ex.GetType().Name,
            storageFault?.Status, storageFault?.ErrorCode);
    }

    private static bool IsComplete(TimestampedSpeechClip? clip) =>
        clip is { Audio.Length: > 0, Characters.Count: > 0 };

    private static TimestampedSpeechClip? TryParseTimestamped(byte[] bytes)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredTimestampedClip>(bytes);
            if (stored?.Audio is not { Length: > 0 } || stored.Characters is not { Count: > 0 })
            {
                return null;
            }

            return new TimestampedSpeechClip(stored.Audio, stored.Characters, stored.DurationSeconds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly record struct Lookup(byte[]? Bytes, bool Unreadable);

    /// <summary>The stored form of a timestamped clip. Audio is base64 in the JSON.</summary>
    private sealed class StoredTimestampedClip
    {
        public byte[] Audio { get; set; } = [];
        public double DurationSeconds { get; set; }
        public List<CharacterTimestamp> Characters { get; set; } = [];
    }
}

/// <summary>How long a request may wait on the store before the cache gives up on it.</summary>
/// <remarks>
/// Plain clips (quiz words, sentences) are a few KB to a few hundred KB and take ElevenLabs about a
/// second, so the store gets 2 s to read and 3 s to write: at most 5 s added while the store is
/// hung, and tens of milliseconds when it is healthy. Timestamped clips are whole transcripts: the
/// stored entry can run to several MB and generation takes tens of seconds or more, so they get
/// 10 s each. Giving up on a large stored entry too early would pay ElevenLabs again for the most
/// expensive clip there is.
/// </remarks>
public sealed record SpeechClipCacheTimeouts
{
    public static SpeechClipCacheTimeouts Default { get; } = new();

    public TimeSpan SynthesizeRead { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SynthesizeWrite { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan TimestampedRead { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan TimestampedWrite { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReadFor(SpeechClipVariant variant) =>
        variant is SpeechClipVariant.Timestamped ? TimestampedRead : SynthesizeRead;

    public TimeSpan WriteFor(SpeechClipVariant variant) =>
        variant is SpeechClipVariant.Timestamped ? TimestampedWrite : SynthesizeWrite;
}
