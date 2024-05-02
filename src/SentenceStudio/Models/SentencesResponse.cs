
using System.Text.Json.Serialization;

namespace SentenceStudio.Models;
public class SentencesResponse
{
    [JsonPropertyName("sentences")]
    public List<Challenge> Sentences { get; set; }
}