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
public sealed class SynthesizeTimestampedResponse
{
    [JsonPropertyName("AudioUrl")] public string AudioUrl { get; set; } = string.Empty;
    [JsonPropertyName("DurationSeconds")] public double DurationSeconds { get; set; }
    [JsonPropertyName("Characters")] public List<CharacterTimestamp> Characters { get; set; } = new();
}
