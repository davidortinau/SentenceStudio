namespace SentenceStudio.Contracts.Speech;

using System.Text.Json.Serialization;

/// <summary>
/// Request body for <c>POST /api/v1/speech/synthesize-timestamped</c>.
/// Generates audio for a <see cref="SentenceStudio.Shared.Models.LearningResource"/>
/// transcript with character-level timing data for reading-activity sync.
/// </summary>
public sealed class SynthesizeTimestampedRequest
{
    [JsonPropertyName("ResourceId")] public string ResourceId { get; set; } = string.Empty;
    [JsonPropertyName("VoiceId")] public string? VoiceId { get; set; }
    [JsonPropertyName("Stability")] public float Stability { get; set; } = 0.5f;
    [JsonPropertyName("SimilarityBoost")] public float SimilarityBoost { get; set; } = 0.75f;
    [JsonPropertyName("Speed")] public float Speed { get; set; } = 1.0f;
}
