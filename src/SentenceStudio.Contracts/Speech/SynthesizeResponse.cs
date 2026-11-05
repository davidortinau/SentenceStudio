namespace SentenceStudio.Contracts.Speech;

/// <summary>
/// Response for <c>POST /api/v1/speech/synthesize</c>.
/// </summary>
/// <remarks>
/// <see cref="VoiceId"/> and <see cref="VoiceSubstituted"/> were added 2026-11-04 and are absent from
/// older servers. A client that keeps clips by voice (an audio history row, a file named by voice)
/// must not keep a clip under the requested voice when <see cref="VoiceSubstituted"/> is true: the
/// clip is in the API's default voice, not the one asked for. It may still play it.
/// Sample: <c>{"audioUrl":"data:audio/mpeg;base64,...","voiceId":"AW5wrnG1jVizOYY7R1Oo","voiceSubstituted":false}</c>.
/// </remarks>
public sealed class SynthesizeResponse
{
    /// <summary>The clip as <c>data:audio/mpeg;base64,...</c>.</summary>
    public string? AudioUrl { get; set; }

    /// <summary>The ElevenLabs voice ID the clip was made with. Null from servers older than 2026-11-04.</summary>
    public string? VoiceId { get; set; }

    /// <summary>
    /// True when the request named a voice and the API used its default voice instead (a voice it
    /// does not offer, or its account voice list could not be read just then). Null or false: the
    /// requested voice was used.
    /// </summary>
    public bool? VoiceSubstituted { get; set; }
}
