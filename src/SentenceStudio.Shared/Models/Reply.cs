using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

public class Reply
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("comprehension_score")]
    public double Comprehension { get; set; }

    [JsonPropertyName("comprehension_notes")]
    public string? ComprehensionNotes { get; set; }
}
