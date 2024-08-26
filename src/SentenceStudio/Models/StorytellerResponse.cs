
using System.Text.Json.Serialization;

namespace SentenceStudio.Models;
public class StorytellerResponse
{
    [JsonPropertyName("story")]
    public Story Story { get; set; }
}