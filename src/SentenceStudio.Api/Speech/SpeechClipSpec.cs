using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElevenLabs;
using ElevenLabs.Models;

namespace SentenceStudio.Api.Speech;

/// <summary>Which speech endpoint produced a clip. Each variant has its own cache namespace.</summary>
public enum SpeechClipVariant
{
    /// <summary><c>POST /api/v1/speech/synthesize</c>: audio only.</summary>
    Synthesize,

    /// <summary><c>POST /api/v1/speech/synthesize-timestamped</c>: audio plus character timings.</summary>
    Timestamped
}

/// <summary>
/// Everything that shapes a generated clip. The provider receives only this record, so a hash of
/// it covers every input that can change the output bytes.
/// </summary>
/// <remarks>
/// Add a field here (not in the provider) when a new request option is introduced; the cache key
/// then changes with it automatically. Changing what an existing field means requires bumping
/// <see cref="SpeechClipCacheKey.Version"/>.
/// </remarks>
public sealed record SpeechClipSpec
{
    public required SpeechClipVariant Variant { get; init; }
    public required string VoiceId { get; init; }
    public required string ModelId { get; init; }
    public required float Stability { get; init; }
    public required float SimilarityBoost { get; init; }
    public required float Style { get; init; }
    public required bool SpeakerBoost { get; init; }
    public required float Speed { get; init; }
    public required OutputFormat OutputFormat { get; init; }

    /// <summary>The exact text sent to the provider. Learner content: never log it.</summary>
    public required string Text { get; init; }

    /// <summary>The <c>/synthesize</c> stability when a request does not name one.</summary>
    public const float DefaultSynthesizeStability = 0.75f;

    /// <summary>The <c>/synthesize</c> similarity boost when a request does not name one.</summary>
    public const float DefaultSynthesizeSimilarityBoost = 0.75f;

    /// <summary>The <c>/synthesize</c> speed when a request does not name one.</summary>
    public const float DefaultSynthesizeSpeed = 1.0f;

    /// <summary>The settings <c>/synthesize</c> has always used.</summary>
    public static SpeechClipSpec ForSynthesize(string voiceId, string modelId, string text) =>
        ForSynthesize(voiceId, modelId, text,
            DefaultSynthesizeStability, DefaultSynthesizeSimilarityBoost, DefaultSynthesizeSpeed);

    /// <summary>
    /// <c>/synthesize</c> with the voice settings a request names (already validated and clamped).
    /// Style and speaker boost stay what <c>/synthesize</c> has always sent.
    /// </summary>
    public static SpeechClipSpec ForSynthesize(
        string voiceId,
        string modelId,
        string text,
        float stability,
        float similarityBoost,
        float speed) => new()
    {
        Variant = SpeechClipVariant.Synthesize,
        VoiceId = voiceId,
        ModelId = modelId,
        Stability = stability,
        SimilarityBoost = similarityBoost,
        Style = 0f,
        SpeakerBoost = true,
        Speed = speed,
        OutputFormat = OutputFormat.MP3_44100_128,
        Text = text
    };

    /// <summary>
    /// The settings <c>/synthesize-timestamped</c> has always used. Style and speaker boost are the
    /// SDK's <c>VoiceSettings</c> defaults, written out so they are part of the key.
    /// </summary>
    public static SpeechClipSpec ForTimestamped(
        string voiceId,
        float stability,
        float similarityBoost,
        float speed,
        string text) => new()
    {
        Variant = SpeechClipVariant.Timestamped,
        VoiceId = voiceId,
        ModelId = Model.MultiLingualV2.Id,
        Stability = stability,
        SimilarityBoost = similarityBoost,
        Style = 0.45f,
        SpeakerBoost = true,
        Speed = speed,
        OutputFormat = OutputFormat.MP3_44100_128,
        Text = text
    };
}

/// <summary>
/// Names a cached clip by a SHA-256 of its <see cref="SpeechClipSpec"/>. The name carries no text,
/// user ID or resource ID, so the same text and voice share one clip across every learner.
/// </summary>
public static class SpeechClipCacheKey
{
    /// <summary>Bump to start a fresh namespace when the key material or stored format changes.</summary>
    public const string Version = "v1";

    public const string Prefix = "speech-cache/" + Version + "/";

    private const string Provider = "elevenlabs";

    public static string BlobName(SpeechClipSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var (folder, extension) = spec.Variant switch
        {
            SpeechClipVariant.Synthesize => ("synthesize", ".mp3"),
            SpeechClipVariant.Timestamped => ("synthesize-timestamped", ".json"),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Variant, "Unknown speech clip variant.")
        };

        return $"{Prefix}{folder}/{Hash(spec)}{extension}";
    }

    /// <summary>Hex SHA-256 of the canonical key material.</summary>
    public static string Hash(SpeechClipSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Fixed property order and invariant number formatting make this canonical. The output
        // format is written the way the SDK sends it in the query string.
        var material = JsonSerializer.SerializeToUtf8Bytes(new KeyMaterial(
            Provider,
            spec.Variant.ToString(),
            spec.VoiceId,
            spec.ModelId,
            spec.Stability,
            spec.SimilarityBoost,
            spec.Style,
            spec.SpeakerBoost,
            spec.Speed,
            spec.OutputFormat.ToString().ToLowerInvariant(),
            spec.Text));

        return Convert.ToHexStringLower(SHA256.HashData(material));
    }

    /// <summary>A short, content-free prefix of the hash for log correlation.</summary>
    public static string LogId(string blobName)
    {
        var start = blobName.LastIndexOf('/') + 1;
        return blobName.Length - start >= 12 ? blobName.Substring(start, 12) : blobName[start..];
    }

    private sealed record KeyMaterial(
        string Provider,
        string Variant,
        string VoiceId,
        string ModelId,
        float Stability,
        float SimilarityBoost,
        float Style,
        bool SpeakerBoost,
        float Speed,
        string OutputFormat,
        string Text);
}
