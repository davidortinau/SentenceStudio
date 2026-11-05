namespace SentenceStudio.Services.Api;

/// <summary>
/// A clip from <c>POST /api/v1/speech/synthesize</c> and the voice the API made it with.
/// </summary>
/// <remarks>
/// Callers that keep clips by voice (StreamHistory rows, files named for a voice) check
/// <see cref="IsSubstituted"/> first: a substituted clip is in the API's default voice, so it may be
/// played but must not be kept under the voice that was asked for.
/// </remarks>
public sealed class SpeechClipStream : MemoryStream
{
    public SpeechClipStream(byte[] audio, string? voiceId, bool voiceSubstituted)
        : base(audio, writable: false)
    {
        VoiceId = voiceId;
        VoiceSubstituted = voiceSubstituted;
    }

    /// <summary>The ElevenLabs voice ID the clip was made with; null from older servers.</summary>
    public string? VoiceId { get; }

    /// <summary>True when the API used its default voice instead of the one requested.</summary>
    public bool VoiceSubstituted { get; }

    /// <summary>True when <paramref name="clip"/> must not be kept under the requested voice.</summary>
    public static bool IsSubstituted(Stream? clip) => clip is SpeechClipStream { VoiceSubstituted: true };
}
