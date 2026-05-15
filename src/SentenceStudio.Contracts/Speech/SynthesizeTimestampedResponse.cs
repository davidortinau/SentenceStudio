namespace SentenceStudio.Contracts.Speech;

/// <summary>
/// One Unicode character spanning <c>[StartMs, EndMs]</c> in the synthesized
/// audio stream. ElevenLabs reports timestamps in seconds (double); the server
/// multiplies by 1000 so the wire values are clean milliseconds.
/// </summary>
public sealed class CharacterTimestamp
{
    public string Char { get; set; } = string.Empty;
    public double StartMs { get; set; }
    public double EndMs { get; set; }
}

/// <summary>
/// Response for <c>POST /api/v1/speech/synthesize-timestamped</c>.
/// Audio is delivered inline as a <c>data:audio/mpeg;base64,...</c> URI to
/// match the existing <see cref="SynthesizeResponse"/> contract used by the
/// Flutter client.
/// </summary>
public sealed class SynthesizeTimestampedResponse
{
    public string AudioUrl { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public List<CharacterTimestamp> Characters { get; set; } = new();
}
