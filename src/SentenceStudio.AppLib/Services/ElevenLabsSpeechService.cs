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
/// not offer.
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
        _logger.LogDebug("TextToSpeech: received {Bytes} bytes for voice {VoiceId}", audio.Length, voiceId);
        return new MemoryStream(audio, writable: false);
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

        // Same AppData names as the in-process service used, so existing clips are reused.
        var cacheKey = $"timestamped_{resource.Id}_{voiceId}_{speed:F1}";
        var cacheFilePath = Path.Combine(_fileSystem.AppDataDirectory, $"{cacheKey}.mp3");
        var cacheMetaPath = Path.Combine(_fileSystem.AppDataDirectory, $"{cacheKey}.json");

        if (File.Exists(cacheFilePath) && File.Exists(cacheMetaPath))
        {
            try
            {
                var metaJson = await File.ReadAllTextAsync(cacheMetaPath, cancellationToken);
                var cachedResult = JsonSerializer.Deserialize<TimestampedAudioResult>(metaJson);

                if (cachedResult != null)
                {
                    var audioData = await File.ReadAllBytesAsync(cacheFilePath, cancellationToken);
                    cachedResult.AudioData = new ReadOnlyMemory<byte>(audioData);
                    cachedResult.CacheFilePath = cacheFilePath;

                    _logger.LogDebug("Using cached timestamped audio for resource {ResourceId}", resource.Id);
                    return cachedResult;
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
        var audio = DecodeAudio(response.AudioUrl);
        var result = new TimestampedAudioResult
        {
            AudioData = audio,
            Characters = ToCharacters(response.Characters ?? []),
            Duration = TimeSpan.FromSeconds(response.DurationSeconds),
            CacheFilePath = cacheFilePath
        };

        try
        {
            await File.WriteAllBytesAsync(cacheFilePath, audio, cancellationToken);

            // Metadata without the audio, as before.
            var metaJson = JsonSerializer.Serialize(new TimestampedAudioResult
            {
                AudioData = ReadOnlyMemory<byte>.Empty,
                Characters = result.Characters,
                Duration = result.Duration,
                CacheFilePath = result.CacheFilePath
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

    /// <summary>
    /// The API's millisecond timings as the ElevenLabs characters the Reading page already uses.
    /// Their setters are JSON-only, so they are built through the SDK's own wire names.
    /// </summary>
    internal static TimestampedTranscriptCharacter[] ToCharacters(IEnumerable<CharacterTimestamp> characters)
    {
        var json = JsonSerializer.Serialize(characters.Select(c => new Dictionary<string, object>
        {
            ["character"] = c.Char,
            ["character_start_times_seconds"] = c.StartMs / 1000.0,
            ["character_end_times_seconds"] = c.EndMs / 1000.0
        }));
        return JsonSerializer.Deserialize<TimestampedTranscriptCharacter[]>(json) ?? [];
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
