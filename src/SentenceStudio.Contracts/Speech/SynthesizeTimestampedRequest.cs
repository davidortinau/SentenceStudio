namespace SentenceStudio.Contracts.Speech;

/// <summary>
/// Request body for <c>POST /api/v1/speech/synthesize-timestamped</c>.
/// Generates audio for a <see cref="SentenceStudio.Shared.Models.LearningResource"/>
/// transcript with character-level timing data for reading-activity sync.
/// </summary>
public sealed class SynthesizeTimestampedRequest
{
    public string ResourceId { get; set; } = string.Empty;
    public string? VoiceId { get; set; }
    public float Stability { get; set; } = 0.5f;
    public float SimilarityBoost { get; set; } = 0.75f;
    public float Speed { get; set; } = 1.0f;
}
