using System.ComponentModel;
using SentenceStudio.Shared.Models;

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

    [Description("Korean speech level / register of the sentence: FormalPolite (합쇼체), InformalPolite (해요체), Casual (반말), PlainWritten (한다체), or Unspecified")]
    public SpeechRegister Register { get; set; } = SpeechRegister.Unspecified;

    [Description("Difficulty hint from 1 (easiest, short and concrete) to 5 (hardest), based on length, rare words, and honorific complexity")]
    public int DifficultyLevel { get; set; } = 2;
}
