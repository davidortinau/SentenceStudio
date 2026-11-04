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
/// </remarks>
public sealed class SpeechClipCache
{
    public const string AudioContentType = "audio/mpeg";
    public const string TimestampedContentType = "application/json";

    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(30);

    private readonly ISpeechClipStore? _store;
    private readonly ILogger<SpeechClipCache> _logger;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _writeTimeout;

    public SpeechClipCache(
        ILogger<SpeechClipCache> logger,
        ISpeechClipStore? store = null,
        TimeSpan? readTimeout = null,
        TimeSpan? writeTimeout = null)
    {
        _logger = logger;
        _store = store;
        _readTimeout = readTimeout ?? DefaultReadTimeout;
        _writeTimeout = writeTimeout ?? DefaultWriteTimeout;
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        timeout.CancelAfter(_readTimeout);

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
        // Not linked to the request: the clip is already paid for, so keep it even if the caller
        // has gone. The timeout still bounds how long the response waits.
        using var timeout = new CancellationTokenSource(_writeTimeout);

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
        catch (Exception ex)
        {
            LogStoreFailure(ex, "write", variant, name);
        }
    }

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
