using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// DTO specifically designed for AI responses in clozure (fill-in-the-blank) exercises.
/// This provides clear structure for the AI to generate clozure-specific data.
/// </summary>
public class ClozureDto
{
    [Description("Complete Korean sentence using the vocabulary word (UI will replace vocabulary word with blank)")]
    [JsonPropertyName("sentenceText")]
    [JsonRequired]
    public string SentenceText { get; set; } = string.Empty;

    [Description("Natural English translation of the SentenceText")]
    [JsonPropertyName("recommendedTranslation")]
    [JsonRequired]
    public string RecommendedTranslation { get; set; } = string.Empty;

    [Description("The target vocabulary word in dictionary form from the provided vocabulary list")]
    [JsonPropertyName("vocabularyWord")]
    [JsonRequired]
    public string VocabularyWord { get; set; } = string.Empty;

    [Description("The conjugated/inflected form of the vocabulary word that fits naturally in the sentence")]
    [JsonPropertyName("vocabularyWordAsUsed")]
    [JsonRequired]
    public string VocabularyWordAsUsed { get; set; } = string.Empty;

    [Description("Exactly 5 Korean words as an array: the correct answer plus 4 plausible distractors")]
    [JsonPropertyName("vocabularyWordGuesses")]
    [JsonRequired]
    public List<string> VocabularyWordGuesses { get; set; } = new();
}

/// <summary>
/// Response wrapper for AI-generated clozure exercises
/// </summary>
public class ClozureResponse
{
    [Description("List of generated clozure exercise sentences")]
    [JsonPropertyName("sentences")]
    [JsonRequired]
    public List<ClozureDto> Sentences { get; set; } = new();
}
