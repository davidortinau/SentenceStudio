
using System.Text.Json.Serialization;

namespace SentenceStudio.Models;
public class SyntacticSentencesResponse
{
    [JsonPropertyName("sentences")]
    public List<SyntacticSentence> Sentences { get; set; }
}

public class SyntacticSentence
{
    [JsonPropertyName("sentence")]
    public string SentenceText { get; set; }
    
    [JsonPropertyName("chunks")]
    public List<Chunk> Chunks { get; set; }
}

public class Chunk
{
    [JsonPropertyName("chunk")]
    public string ChunkText { get; set; }

    [JsonPropertyName("part_of_speech")]
    public string PartOfSpeech { get; set; }
}