using SentenceStudio.Services.Speech;

namespace SentenceStudio.Services.Api;

/// <summary>
/// A clip from <c>POST /api/v1/speech/synthesize</c> and what the API said about its voice.
/// </summary>
/// <remarks>
/// Callers that keep clips by voice (StreamHistory rows, files named for a voice) check
/// <see cref="MustNotKeep"/> first. Only a clip whose response named the voice used and said it was
/// the requested one may be kept; a substituted clip, or one from an older server that does not say,
/// may be played but must not be kept under the voice that was asked for.
/// </remarks>
public sealed class SpeechClipStream : MemoryStream
{
    public SpeechClipStream(byte[] audio, string? voiceId, bool? voiceSubstituted)
        : base(audio, writable: false)
    {
        VoiceId = voiceId;
        VoiceSubstituted = voiceSubstituted;
    }

    /// <summary>The ElevenLabs voice ID the API made the clip with; null from older servers.</summary>
    public string? VoiceId { get; }

    /// <summary>True when the API used its default voice instead; null from older servers.</summary>
    public bool? VoiceSubstituted { get; }

    /// <summary>True when the response confirmed the requested voice (<see cref="TimestampedSpeech.VoiceVerified"/>).</summary>
    public bool CanCache => TimestampedSpeech.VoiceVerified(VoiceId, VoiceSubstituted);

    /// <summary>
    /// True when <paramref name="clip"/> came from the speech API without confirming the requested
    /// voice, so it must not be kept under that voice. Streams from other sources are unaffected.
    /// </summary>
    public static bool MustNotKeep(Stream? clip) => clip is SpeechClipStream { CanCache: false };
}
