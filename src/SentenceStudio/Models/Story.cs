
using System.Text.Json.Serialization;

namespace SentenceStudio.Models;

public class Story
{
    [JsonPropertyName("body")]
    public string Body { get; set; }

    [JsonPropertyName("questions")]
    public List<Question> Questions { get; set; }
}