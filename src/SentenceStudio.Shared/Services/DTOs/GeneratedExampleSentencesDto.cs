using System.ComponentModel;

namespace SentenceStudio.Services.DTOs;

public class GeneratedExampleSentencesDto
{
    [Description("List of example sentences generated for the vocabulary word")]
    public List<GeneratedSentenceDto> Sentences { get; set; } = new();
}

public class GeneratedSentenceDto
{
    [Description("The example sentence in the target language showing the vocabulary word in context")]
    public string TargetSentence { get; set; } = string.Empty;
    
    [Description("The translation of the sentence in the native language")]
    public string NativeSentence { get; set; } = string.Empty;
    
    [Description("Whether this should be marked as a core teaching example (true for the most useful/common usage)")]
    public bool IsCore { get; set; } = false;
}
