using System.Net.Http;
using System.Text.Json;
using ElevenLabs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Speech;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Default ElevenLabs voice IDs. Config (AI:ElevenLabs:Voices) takes precedence at runtime;
/// these constants serve as compile-time fallbacks and are used by SpeechVoicePreferences.
/// </summary>
public static class Voices
{
    // Korean voices
    public const string HyunBin = "s07IwTCOrCDCaETjUVjx"; // Cool Korean male voice. Great for Professional corporate PR narration.
    public const string DoHyeon = "FQ3MuLxZh0jHcZmA5vW1"; // Older male
    public const string YohanKoo = "4JJwo477JUAx3HV0T7n7"; // Conversational - The voice of a confident, authoritative man in his 30s.
    public const string Jina = "sSoVF9lUgTGJz0Xz3J9y"; // A mid-aged Korean female voice. Works well for News broadcasting
    public const string JiYoung = "AW5wrnG1jVizOYY7R1Oo"; // A warm and clear Korean female voice with a friendly and natural tone. Suitable for narration, tutorials, and conversational content.
    public const string Jennie = "z6Kj0hecH20CdetSElRT"; // Informative and youthful, exuding professionalism with a friendly, engaging tone that captivates listeners. It's perfect for podcasts, tutorials, and content creation, delivering clarity and enthusiasm that keeps audiences connected and informed.
    public const string Yuna = "xi3rF0t7dg7uN2M0WUhr"; // Young Korean female voice with soft/cheerful voice specialized in narrative and storytelling.

    // English voices
    public const string Rachel = "21m00Tcm4TlvDq8ikWAM"; // Female - American (default)
    public const string Antoni = "ED0k6LqFEfpMua5GXpMG"; // Male - American
    public const string Elli = "jsCqWAovK2LkecY7zXl4"; // Female - American
    public const string Adam = "kgG8YXSrynzpPIncHKrx"; // Male - American
    public const string Dorothy = "5Q0t7uMcjvnagumLfvZi"; // Female - British
}

/// <summary>
/// Text to speech for the shared Razor activity pages (WebApp and MAUI). Every clip is synthesized
/// by the API's ElevenLabs gateway as the signed-in learner: <c>POST /api/v1/speech/synthesize</c>
/// for short clips and <c>POST /api/v1/speech/synthesize-timestamped</c> for Reading. The API keeps
/// the ElevenLabs key and the shared clip cache, so neither host needs the key.
/// </summary>
/// <remarks>
/// <para>
/// Failures are thrown, never returned as an empty stream: no active learner throws
/// <see cref="UnauthorizedAccessException"/>; an API refusal throws <see cref="HttpRequestException"/>
/// with its status; a response without usable audio throws <see cref="InvalidOperationException"/>.
/// Learner text is never logged.
/// </para>
/// <para>
/// Voices are sent as the pages hold them (an ElevenLabs voice ID or a slug such as
/// <c>jiyoung</c>); the API resolves them and falls back to its default voice for any voice it does
/// not offer. Such a clip comes back as a <see cref="SpeechClipStream"/> with
/// <see cref="SpeechClipStream.VoiceSubstituted"/> set, and is never kept under the requested voice;
/// nor is a clip from an older API that does not say which voice it used
/// (<see cref="SpeechClipStream.CanCache"/>).
/// </para>
/// </remarks>
public class ElevenLabsSpeechService
{
    /// <summary>Largest clip accepted from the API (a long Reading transcript runs to several MB).</summary>
    internal const int MaxAudioBytes = 32 * 1024 * 1024;

    private readonly ISpeechApiClient _speechApi;
    private readonly ITimestampedSpeechApiClient _timestampedApi;
    private readonly IServiceProvider _services;
    private readonly ILogger<ElevenLabsSpeechService> _logger;
    private readonly IFileSystemService _fileSystem;

    public ElevenLabsSpeechService(
        ISpeechApiClient speechApi,
        ITimestampedSpeechApiClient timestampedApi,
        IServiceProvider services,
        ILogger<ElevenLabsSpeechService> logger,
        IFileSystemService fileSystem)
    {
        _speechApi = speechApi;
        _timestampedApi = timestampedApi;
        _services = services;
        _logger = logger;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Speaks <paramref name="text"/> through <c>/api/v1/speech/synthesize</c> with the page's voice
    /// settings and returns the MP3 audio.
    /// </summary>
    /// <param name="text">The text to convert to speech. Learner content: never logged.</param>
    /// <param name="voiceId">An ElevenLabs voice ID or slug (for example <c>echo</c>).</param>
    /// <param name="stability">Voice stability (0.0 to 1.0).</param>
    /// <param name="similarityBoost">Similarity boost (0.0 to 1.0).</param>
    /// <param name="speed">Speech speed (the API accepts 0.7 to 1.2).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    [Obsolete("Use GenerateTimestampedAudioAsync for better performance and synchronization")]
    public async Task<Stream> TextToSpeechAsync(
        string text,
        string voiceId = "echo",
        float stability = 0.75f,
        float similarityBoost = 0.75f,
        float speed = 1.0f,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        var owner = RequireOwner("Speech synthesis");

        SynthesizeResponse response;
        try
        {
            response = await _speechApi.SynthesizeAsync(new SynthesizeRequest
            {
                Text = text,
                VoiceId = voiceId,
                Stability = stability,
                SimilarityBoost = similarityBoost,
                Speed = speed
            }, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("Speech synthesis was refused by the API: {Status}.", DescribeStatus(ex));
            throw;
        }

        RequireSameOwner(owner, "Speech synthesis");
        var audio = DecodeAudio(response.AudioUrl);
        var clip = new SpeechClipStream(audio, response.VoiceId, response.VoiceSubstituted);
        if (!clip.CanCache)
        {
            // Played, but callers must not keep it under the requested voice (SpeechClipStream).
            _logger.LogWarning(
                "The API did not confirm voice {VoiceId} (voice used: {VoiceUsed}, substituted: {Substituted}); the clip will not be cached.",
                voiceId, response.VoiceId ?? "not reported", response.VoiceSubstituted?.ToString() ?? "not reported");
        }
        _logger.LogDebug("TextToSpeech: received {Bytes} bytes for voice {VoiceId}", audio.Length, voiceId);
        return clip;
    }

    /// <summary>
    /// Audio for a learning resource's transcript with character-level timings, from
    /// <c>/api/v1/speech/synthesize-timestamped</c>. The API reads the transcript from the learner's
    /// own copy of the resource on the server, so the resource must have synced. Results are kept
    /// in AppData under the same names as before, so a device that already has a clip reuses it.
    /// </summary>
    /// <param name="resource">The active learner's learning resource.</param>
    /// <param name="voiceId">The voice slug or ID (default <c>jiyoung</c>).</param>
    /// <param name="stability">Voice stability (0.0 to 1.0).</param>
    /// <param name="similarityBoost">Similarity boost (0.0 to 1.0).</param>
    /// <param name="speed">Speech speed (the API accepts 0.7 to 1.2).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task<TimestampedAudioResult> GenerateTimestampedAudioAsync(
        LearningResource resource,
        string voiceId = "jiyoung",  // Default to Korean voice
        float stability = 0.5f,
        float similarityBoost = 0.75f,
        float speed = 1.0f,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        cancellationToken.ThrowIfCancellationRequested();
        var owner = RequireOwner("Reading audio");
        if (string.IsNullOrWhiteSpace(resource.Id)
            || !string.Equals(resource.UserProfileId, owner, StringComparison.Ordinal))
        {
            _logger.LogWarning("Reading audio refused: resource {ResourceId} is not the active learner's.", resource.Id);
            throw new UnauthorizedAccessException("Reading audio needs a resource that belongs to the signed-in learner.");
        }

        if (string.IsNullOrWhiteSpace(resource.Transcript))
            throw new InvalidOperationException("This resource has no transcript to read aloud.");

        // Same AppData names as the in-process service used. A cached clip is used only when it was
        // made from this exact transcript (its fingerprint is in the metadata); a clip cached before
        // the fingerprint existed is made again once.
        var transcriptSha256 = TimestampedSpeech.TranscriptSha256(resource.Transcript);
        var cacheKey = $"timestamped_{resource.Id}_{voiceId}_{speed:F1}";
        var cacheFilePath = Path.Combine(_fileSystem.AppDataDirectory, $"{cacheKey}.mp3");
        var cacheMetaPath = Path.Combine(_fileSystem.AppDataDirectory, $"{cacheKey}.json");

        if (File.Exists(cacheFilePath) && File.Exists(cacheMetaPath))
        {
            try
            {
                var metaJson = await File.ReadAllTextAsync(cacheMetaPath, cancellationToken);
                var meta = JsonSerializer.Deserialize<ReadingAudioMeta>(metaJson);

                if (meta is not null && string.Equals(meta.TranscriptSha256, transcriptSha256, StringComparison.Ordinal))
                {
                    var audioData = await File.ReadAllBytesAsync(cacheFilePath, cancellationToken);
                    if (audioData.Length > 0)
                    {
                        _logger.LogDebug("Using cached timestamped audio for resource {ResourceId}", resource.Id);
                        return new TimestampedAudioResult
                        {
                            AudioData = audioData,
                            Characters = meta.Characters ?? [],
                            Duration = meta.Duration,
                            CacheFilePath = cacheFilePath
                        };
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "Cached reading audio for resource {ResourceId} is not for this transcript; generating again.",
                        resource.Id);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to load cached audio; generating again.");
            }
        }

        _logger.LogInformation("Requesting timestamped audio for resource {ResourceId} with voice {VoiceId}", resource.Id, voiceId);

        SynthesizeTimestampedResponse response;
        try
        {
            response = await _timestampedApi.SynthesizeTimestampedAsync(new SynthesizeTimestampedRequest
            {
                ResourceId = resource.Id,
                VoiceId = voiceId,
                Stability = stability,
                SimilarityBoost = similarityBoost,
                Speed = speed
            }, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // 404: the API has no copy of this resource for this learner (for example, one created
            // on this device that has not synced yet).
            _logger.LogError("Timestamped audio for resource {ResourceId} was refused by the API: {Status}.",
                resource.Id, DescribeStatus(ex));
            throw;
        }

        RequireSameOwner(owner, "Reading audio");

        // The API synthesized its own copy of the transcript. Timings for different text would not
        // line up with what this page shows, so such a clip is neither played nor kept.
        var use = TimestampedSpeech.Classify(response, resource.Transcript);
        if (use == TimestampedClipUse.Reject)
        {
            _logger.LogWarning(
                "Reading audio for resource {ResourceId} is for a different transcript than this device has; not used.",
                resource.Id);
            throw new InvalidOperationException(
                "This reading's text differs from the copy on the server. Sync, then try the audio again.");
        }

        var audio = DecodeAudio(response.AudioUrl);
        var result = new TimestampedAudioResult
        {
            AudioData = audio,
            Characters = TimestampedSpeech.ToCharacters(response.Characters ?? []),
            Duration = TimeSpan.FromSeconds(response.DurationSeconds),
            CacheFilePath = cacheFilePath
        };

        if (use != TimestampedClipUse.Keep)
        {
            // An older server (no fingerprint or voice) or the API's default voice instead of the
            // requested one: play it, but do not keep it under this resource and voice.
            _logger.LogWarning(
                "Reading audio for resource {ResourceId} will not be cached (transcript verified: {Verified}, voice used: {VoiceUsed}, substituted: {Substituted}).",
                resource.Id, !string.IsNullOrEmpty(response.TranscriptSha256),
                response.VoiceId ?? "not reported", response.VoiceSubstituted?.ToString() ?? "not reported");
            result.CacheFilePath = string.Empty;
            return result;
        }

        try
        {
            await File.WriteAllBytesAsync(cacheFilePath, audio, cancellationToken);

            // Metadata without the audio, with the transcript it belongs to.
            var metaJson = JsonSerializer.Serialize(new ReadingAudioMeta
            {
                Characters = result.Characters,
                Duration = result.Duration,
                TranscriptSha256 = transcriptSha256
            });
            await File.WriteAllTextAsync(cacheMetaPath, metaJson, cancellationToken);

            _logger.LogDebug("Cached timestamped audio: {AudioSize} bytes, {CharCount} characters, {Duration:F1}s",
                audio.Length, result.Characters.Length, result.Duration.TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The audio still plays without a cache entry.
            _logger.LogWarning(ex, "Failed to cache timestamped audio");
        }

        return result;
    }

    /// <summary>The audio bytes in an API <c>data:audio/...;base64,</c> URL; throws when there are none.</summary>
    internal static byte[] DecodeAudio(string? dataUri)
    {
        const string marker = "base64,";
        var start = dataUri?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (dataUri is null || !dataUri.StartsWith("data:audio/", StringComparison.Ordinal) || start < 0)
            throw new InvalidOperationException("The speech service returned no audio.");

        byte[] audio;
        try
        {
            audio = Convert.FromBase64String(dataUri[(start + marker.Length)..]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The speech service returned unreadable audio.", ex);
        }

        if (audio.Length == 0)
            throw new InvalidOperationException("The speech service returned empty audio.");
        if (audio.Length > MaxAudioBytes)
            throw new InvalidOperationException("The speech service returned more audio than this app accepts.");
        return audio;
    }

    /// <summary>The AppData metadata kept beside a cached Reading clip.</summary>
    private sealed class ReadingAudioMeta
    {
        public TimestampedTranscriptCharacter[]? Characters { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>The transcript fingerprint the clip was made from (TimestampedSpeech.TranscriptSha256).</summary>
        public string? TranscriptSha256 { get; set; }
    }

    private static object DescribeStatus(HttpRequestException ex) =>
        ex.StatusCode is { } status ? (int)status : "no response";

    /// <summary>The active learner (shared <see cref="ActiveOwner"/> rules); none fails closed.</summary>
    private string RequireOwner(string operation)
    {
        using var scope = _services.CreateScope();
        if (!ActiveOwner.TryResolve(scope.ServiceProvider, _logger, operation, out var owner))
            throw new UnauthorizedAccessException($"{operation} needs a signed-in learner.");
        return owner;
    }

    /// <summary>A learner switch while the API answered must not hand one learner's audio to another.</summary>
    private void RequireSameOwner(string owner, string operation)
    {
        if (!string.Equals(RequireOwner(operation), owner, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"{operation}: the signed-in learner changed.");
    }
}
