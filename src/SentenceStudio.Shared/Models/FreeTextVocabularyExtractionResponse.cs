using System.ComponentModel;
using System.Collections.Generic;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// AI response DTO for vocabulary extracted from free-form text.
/// Used with FreeTextToVocab.scriban-txt template.
/// Deserialized directly from the LLM's JSON output via AiService.SendPrompt&lt;T&gt;.
/// </summary>
public class FreeTextVocabularyExtractionResponse
{
    [Description("List of vocabulary items extracted from the free-form text")]
    public List<ExtractedVocabularyItemWithConfidence> Vocabulary { get; set; } = new();
}

/// <summary>
/// A single vocabulary item extracted by the AI from free-form text input.
/// Extends the base extraction model with confidence scoring for uncertain extractions.
/// </summary>
public class ExtractedVocabularyItemWithConfidence
{
    [Description("Target language word/phrase in dictionary form")]
    public string TargetLanguageTerm { get; set; } = string.Empty;

    [Description("Native language definition — clear, concise")]
    public string NativeLanguageTerm { get; set; } = string.Empty;

    [Description("Confidence level for this extraction: high, medium, or low")]
    public string Confidence { get; set; } = "high";

    [Description("Optional notes about uncertainty or extraction context")]
    public string? Notes { get; set; }

    [Description("noun, verb, adjective, adverb, expression, counter, or particle")]
    public string? PartOfSpeech { get; set; }

    [Description("Classification of this lexical unit. Word = single dictionary entry (including Korean compounds like 공부하다). Phrase = multi-word unit or fragment (noun phrase, idiom, collocation). Sentence = complete utterance, typically ending in terminal punctuation. Be conservative — if uncertain between Word and Phrase, choose Word.")]
    public LexicalUnitType LexicalUnitType { get; set; } = LexicalUnitType.Word;

    [Description("If this item is a Phrase or Sentence, list the target-language words that compose it as they should appear as separate vocabulary entries. Use dictionary/lemma form (e.g. 공부하다, not 공부하고). Empty array for single Words.")]
    public List<string> RelatedTerms { get; set; } = new();

    /// <summary>
    /// Converts this extraction DTO into a persistable VocabularyWord.
    /// Maps confidence and notes into the Tags field for later review.
    /// </summary>
    public VocabularyWord ToVocabularyWord(string language = "Korean")
    {
        var tagsList = new List<string>();
        
        // Include confidence level if not high
        if (!string.IsNullOrWhiteSpace(Confidence) && Confidence.ToLowerInvariant() != "high")
            tagsList.Add($"confidence:{Confidence}");
        
        // Include notes if present
        if (!string.IsNullOrWhiteSpace(Notes))
            tagsList.Add($"notes:{Notes}");
        
        // Include related terms hint if present
        if (RelatedTerms.Any())
            tagsList.Add($"constituents:{string.Join(",", RelatedTerms)}");

        return new VocabularyWord
        {
            TargetLanguageTerm = TargetLanguageTerm,
            NativeLanguageTerm = NativeLanguageTerm,
            Lemma = TargetLanguageTerm, // Free-text extraction doesn't decompose lemmas
            Language = language,
            Tags = tagsList.Any() ? string.Join("; ", tagsList) : null,
            LexicalUnitType = LexicalUnitType
        };
    }
}
