using System.ComponentModel;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// DTO specifically designed for AI responses in clozure (fill-in-the-blank) exercises.
/// This provides clear structure for the AI to generate clozure-specific data.
/// </summary>
public class ClozureDto
{
    [Description("Complete Korean sentence using the vocabulary word (UI will replace vocabulary word with blank)")]
    public string SentenceText { get; set; } = string.Empty;
    
    [Description("Natural English translation of the SentenceText")]
    public string RecommendedTranslation { get; set; } = string.Empty;
    
    [Description("The target vocabulary word in dictionary form from the provided vocabulary list")]
    public string VocabularyWord { get; set; } = string.Empty;
    
    [Description("The conjugated/inflected form of the vocabulary word that fits naturally in the sentence")]
    public string VocabularyWordAsUsed { get; set; } = string.Empty;
    
    [Description("Exactly 5 comma-separated Korean words: the correct answer plus 4 plausible distractors")]
    public string VocabularyWordGuesses { get; set; } = string.Empty;
}

/// <summary>
/// Response wrapper for AI-generated clozure exercises
/// </summary>
public class ClozureResponse
{
    public List<ClozureDto> Sentences { get; set; } = new();
}
