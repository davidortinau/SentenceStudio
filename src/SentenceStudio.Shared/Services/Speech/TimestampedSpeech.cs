using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElevenLabs;
using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Services.Speech;

/// <summary>What a client may do with a timestamped (Reading) clip.</summary>
public enum TimestampedClipUse
{
    /// <summary>Made from this transcript in the requested voice: play it and keep it.</summary>
    Keep,

    /// <summary>Cannot be confirmed (an older server, or the API's default voice): play it, keep nothing.</summary>
    PlayOnly,

    /// <summary>Made from other text than this device's transcript: neither play nor keep it.</summary>
    Reject
}

/// <summary>
/// Shared pieces of API speech, used by the API, the web and MAUI service and the native clients
/// so all of them read the wire format and decide what may be cached the same way.
/// </summary>
public static class TimestampedSpeech
{
    /// <summary>
    /// The transcript fingerprint the API returns as <c>TranscriptSha256</c>: lowercase hex SHA-256 of
    /// the UTF-8 text. Clients compare it with their own copy before using or keeping a clip.
    /// </summary>
    public static string TranscriptSha256(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(transcript)));
    }

    /// <summary>
    /// True only when a speech response says which voice it used and that it was the requested one.
    /// A response without those fields (an older server) is unverified: play it, never keep it under
    /// the requested voice.
    /// </summary>
    public static bool VoiceVerified(string? voiceId, bool? voiceSubstituted) =>
        !string.IsNullOrWhiteSpace(voiceId) && voiceSubstituted == false;

    /// <summary>What a client may do with a timestamped clip for its own <paramref name="transcript"/>.</summary>
    public static TimestampedClipUse Classify(SynthesizeTimestampedResponse response, string transcript)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(transcript);
        if (string.IsNullOrEmpty(response.TranscriptSha256))
            return TimestampedClipUse.PlayOnly;
        if (!string.Equals(response.TranscriptSha256, TranscriptSha256(transcript), StringComparison.OrdinalIgnoreCase))
            return TimestampedClipUse.Reject;
        return VoiceVerified(response.VoiceId, response.VoiceSubstituted)
            ? TimestampedClipUse.Keep
            : TimestampedClipUse.PlayOnly;
    }

    /// <summary>The API's millisecond character timings as the ElevenLabs characters Reading plays against.</summary>
    public static TimestampedTranscriptCharacter[] ToCharacters(IEnumerable<CharacterTimestamp> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        return ToCharacters(characters.Select(c => (c.Char, c.StartMs / 1000.0, c.EndMs / 1000.0)));
    }

    /// <summary>
    /// Builds ElevenLabs characters from text and second timings. Their setters are JSON-only, so
    /// they are created through the SDK's own wire names.
    /// </summary>
    public static TimestampedTranscriptCharacter[] ToCharacters(
        IEnumerable<(string Character, double Start, double End)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var json = JsonSerializer.Serialize(items.Select(item => new Dictionary<string, object>
        {
            ["character"] = item.Character,
            ["character_start_times_seconds"] = item.Start,
            ["character_end_times_seconds"] = item.End
        }));
        return JsonSerializer.Deserialize<TimestampedTranscriptCharacter[]>(json) ?? [];
    }
}
