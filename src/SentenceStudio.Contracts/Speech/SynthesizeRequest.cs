namespace SentenceStudio.Contracts.Speech;

/// <summary>
/// Request body for <c>POST /api/v1/speech/synthesize</c> (short interactive clips: quiz prompts,
/// phrases, shadowing sentences). The response is <see cref="SynthesizeResponse"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every client (web, MAUI, native iOS/Android, Flutter) sends this shape. The three voice
/// settings are optional and were added 2026-11-04; a request that omits them (or sends
/// <c>null</c>) is handled exactly as before they existed: stability 0.75, similarity boost 0.75,
/// speed 1.0, and the same stored clip.
/// </para>
/// <para>
/// Sample: <c>{"Text":"사과","VoiceId":"AW5wrnG1jVizOYY7R1Oo","Stability":0.5,"SimilarityBoost":0.75,"Speed":0.85}</c>.
/// Property names are matched case-insensitively.
/// </para>
/// </remarks>
public sealed class SynthesizeRequest
{
    /// <summary>The exact text to speak. Learner content.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// A voice slug (<c>jiyoung</c>, <c>yuna</c>, <c>echo</c>, ...) or an ElevenLabs voice ID that
    /// this API offers: a built-in default voice, a configured voice, or one listed by
    /// <c>GET /api/v1/speech/voices</c>. Anything else, or no value, uses the API's default voice.
    /// </summary>
    public string? VoiceId { get; set; }

    /// <summary>Optional voice stability, clamped to 0 to 1. Omitted: 0.75. Not a finite number: 400.</summary>
    public float? Stability { get; set; }

    /// <summary>Optional similarity boost, clamped to 0 to 1. Omitted: 0.75. Not a finite number: 400.</summary>
    public float? SimilarityBoost { get; set; }

    /// <summary>Optional speech speed, clamped to 0.7 to 1.2 (the ElevenLabs range). Omitted: 1.0. Not a finite number: 400.</summary>
    public float? Speed { get; set; }
}
