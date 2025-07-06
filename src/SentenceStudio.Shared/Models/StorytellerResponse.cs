using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

public class StorytellerResponse
{
    [JsonPropertyName("story")]
    public Story? Story { get; set; }
}
