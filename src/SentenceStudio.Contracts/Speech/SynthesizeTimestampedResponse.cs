using System.Text.Json.Serialization;

namespace SentenceStudio.Contracts.Speech;

/// <summary>
/// One Unicode character spanning <c>[StartMs, EndMs]</c> in the synthesized
/// audio stream. ElevenLabs reports timestamps in seconds (double); the server
/// multiplies by 1000 so the wire values are clean milliseconds.
/// </summary>
public sealed class CharacterTimestamp
{
    [JsonPropertyName("Char")] public string Char { get; set; } = string.Empty;
    [JsonPropertyName("StartMs")] public double StartMs { get; set; }
    [JsonPropertyName("EndMs")] public double EndMs { get; set; }
}

/// <summary>
/// Response for <c>POST /api/v1/speech/synthesize-timestamped</c>.
/// Audio is delivered inline as a <c>data:audio/mpeg;base64,...</c> URI to
/// match the existing <see cref="SynthesizeResponse"/> contract used by the
/// Flutter client.
/// </summary>
/// <remarks>
/// <see cref="VoiceId"/>, <see cref="VoiceSubstituted"/> and <see cref="TranscriptSha256"/> were
/// added 2026-11-04 and are absent from older servers. The audio and timings are of the server's
/// copy of the resource transcript. A client must compare <see cref="TranscriptSha256"/> with the
/// SHA-256 of its own transcript (UTF-8, lowercase hex) and neither use nor keep a clip that differs:
/// the timings would not line up with the text it shows. Like <see cref="SynthesizeResponse"/>, a clip
/// with <see cref="VoiceSubstituted"/> true must not be kept under the requested voice.
/// </remarks>
public sealed class SynthesizeTimestampedResponse
{
    [JsonPropertyName("AudioUrl")] public string AudioUrl { get; set; } = string.Empty;
    [JsonPropertyName("DurationSeconds")] public double DurationSeconds { get; set; }
    [JsonPropertyName("Characters")] public List<CharacterTimestamp> Characters { get; set; } = new();

    /// <summary>The ElevenLabs voice ID the clip was made with. Null from older servers.</summary>
    [JsonPropertyName("VoiceId")] public string? VoiceId { get; set; }

    /// <summary>True when the request named a voice and the API used its default voice instead.</summary>
    [JsonPropertyName("VoiceSubstituted")] public bool? VoiceSubstituted { get; set; }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 transcript that was synthesized. Null from older servers.</summary>
    [JsonPropertyName("TranscriptSha256")] public string? TranscriptSha256 { get; set; }
}
