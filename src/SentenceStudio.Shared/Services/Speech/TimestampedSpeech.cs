using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElevenLabs;
using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Services.Speech;

/// <summary>
/// Shared pieces of timestamped (Reading) speech, used by the API, the web and MAUI service and the
/// native Reading source so all of them treat the wire format the same way.
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
