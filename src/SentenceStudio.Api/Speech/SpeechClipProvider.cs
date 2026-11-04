using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;
using ElevenLabs.Voices;
using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Api.Speech;

/// <summary>Audio plus per-character timings, in the wire units the API returns.</summary>
public sealed record TimestampedSpeechClip(
    byte[] Audio,
    IReadOnlyList<CharacterTimestamp> Characters,
    double DurationSeconds);

/// <summary>Generates speech clips. Every call is paid; <see cref="SpeechClipCache"/> sits in front of it.</summary>
public interface ISpeechClipProvider
{
    Task<byte[]> GenerateAsync(SpeechClipSpec spec, CancellationToken cancellationToken);

    Task<TimestampedSpeechClip> GenerateTimestampedAsync(SpeechClipSpec spec, CancellationToken cancellationToken);
}

/// <summary>
/// ElevenLabs text to speech. The request is built only from <see cref="SpeechClipSpec"/>, which is
/// what makes the spec a complete cache key.
/// </summary>
public sealed class ElevenLabsSpeechClipProvider(ElevenLabsClient client) : ISpeechClipProvider
{
    public async Task<byte[]> GenerateAsync(SpeechClipSpec spec, CancellationToken cancellationToken)
    {
        var request = BuildRequest(new Voice(spec.VoiceId, spec.VoiceId), spec, withTimestamps: false);
        var clip = await client.TextToSpeechEndpoint.TextToSpeechAsync(
            request,
            cancellationToken: cancellationToken);
        return clip.ClipData.ToArray();
    }

    public async Task<TimestampedSpeechClip> GenerateTimestampedAsync(
        SpeechClipSpec spec,
        CancellationToken cancellationToken)
    {
        // Kept from the original endpoint: the voice is looked up first, so an unknown voice fails
        // before the paid call. Its stored settings are not used; the spec's settings are explicit.
        var voice = await client.VoicesEndpoint.GetVoiceAsync(spec.VoiceId, cancellationToken: cancellationToken);
        var request = BuildRequest(voice, spec, withTimestamps: true);
        var clip = await client.TextToSpeechEndpoint.TextToSpeechAsync(request, cancellationToken: cancellationToken);

        var chars = clip.TimestampedTranscriptCharacters;
        var characters = new List<CharacterTimestamp>(chars?.Length ?? 0);
        if (chars is not null)
        {
            foreach (var c in chars)
            {
                characters.Add(new CharacterTimestamp
                {
                    Char = c.Character.ToString() ?? string.Empty,
                    StartMs = c.StartTime * 1000.0,
                    EndMs = c.EndTime * 1000.0
                });
            }
        }

        var durationSeconds = chars is { Length: > 0 } ? chars[^1].EndTime : 0.0;
        return new TimestampedSpeechClip(clip.ClipData.ToArray(), characters, durationSeconds);
    }

    private static TextToSpeechRequest BuildRequest(Voice voice, SpeechClipSpec spec, bool withTimestamps) =>
        new(
            voice,
            spec.Text,
            voiceSettings: new VoiceSettings(spec.Stability, spec.SimilarityBoost)
            {
                Style = spec.Style,
                SpeakerBoost = spec.SpeakerBoost,
                Speed = spec.Speed
            },
            outputFormat: spec.OutputFormat,
            model: new Model(spec.ModelId),
            withTimestamps: withTimestamps);
}
