using System.ComponentModel;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// DTO specifically designed for AI responses in translation exercises.
/// This provides clear structure for the AI to generate translation-specific data.
/// </summary>
public class TranslationDto
{
    [Description("Natural Korean sentence using vocabulary from the provided list")]
    public string SentenceText { get; set; } = string.Empty;
    
    [Description("Natural English translation of the Korean sentence")]
    public string RecommendedTranslation { get; set; } = string.Empty;
    
    [Description("List of all Korean words/components from the sentence so learners can see every building block in multiple choice mode")]
    public List<string> TranslationVocabulary { get; set; } = new();
}

/// <summary>
/// Response wrapper for AI-generated translation exercises
/// </summary>
public class TranslationResponse
{
    public List<TranslationDto> Sentences { get; set; } = new();
}
