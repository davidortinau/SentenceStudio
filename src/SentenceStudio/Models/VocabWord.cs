using System.Text.Json.Serialization;

namespace SentenceStudio.Models;
public class VocabWord
{
    [JsonPropertyName("original")]
    public string NativeLanguageTerm { get; set; }
    
    [JsonPropertyName("translation")]
    public string TargetLanguageTerm { get; set; }
}