using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// AI response DTO for vocabulary extracted from a transcript.
/// Deserialized directly from the LLM's JSON output via AiService.SendPrompt&lt;T&gt;.
/// </summary>
public class VocabularyExtractionResponse
{
    [JsonPropertyName("vocabulary")]
    public List<ExtractedVocabularyItem> Vocabulary { get; set; } = new();
}

/// <summary>
/// A single vocabulary item extracted by the AI from transcript content.
/// Maps to VocabularyWord for persistence but carries additional AI-provided metadata.
/// </summary>
public class ExtractedVocabularyItem
{
    [JsonPropertyName("targetLanguageTerm")]
    [Description("Korean word/phrase in dictionary form")]
    public string TargetLanguageTerm { get; set; } = string.Empty;

    [JsonPropertyName("nativeLanguageTerm")]
    [Description("English definition — clear, concise")]
    public string NativeLanguageTerm { get; set; } = string.Empty;

    [JsonPropertyName("romanization")]
    [Description("Revised Romanization of Korean")]
    public string? Romanization { get; set; }

    [JsonPropertyName("lemma")]
    [Description("Root morpheme if different from targetLanguageTerm")]
    public string? Lemma { get; set; }

    [JsonPropertyName("partOfSpeech")]
    [Description("noun, verb, adjective, adverb, expression, counter, or particle")]
    public string? PartOfSpeech { get; set; }

    [JsonPropertyName("topikLevel")]
    [Description("TOPIK difficulty level 1-6")]
    public int TopikLevel { get; set; } = 2;

    [JsonPropertyName("frequencyInTranscript")]
    [Description("How many times this word appears in the transcript")]
    public int FrequencyInTranscript { get; set; } = 1;

    [JsonPropertyName("exampleSentence")]
    [Description("Actual sentence from the transcript containing this word")]
    public string? ExampleSentence { get; set; }

    [JsonPropertyName("exampleSentenceTranslation")]
    [Description("English translation of the example sentence")]
    public string? ExampleSentenceTranslation { get; set; }

    [JsonPropertyName("tags")]
    [Description("Comma-separated context tags")]
    public string? Tags { get; set; }

    [JsonPropertyName("lexicalUnitType")]
    [Description("Classification of this lexical unit. Word = single dictionary entry (including Korean compounds like 공부하다). Phrase = multi-word unit or fragment (noun phrase, idiom, collocation). Sentence = complete utterance, typically ending in terminal punctuation. Be conservative — if uncertain between Word and Phrase, choose Word.")]
    public LexicalUnitType LexicalUnitType { get; set; } = LexicalUnitType.Word;

    [JsonPropertyName("relatedTerms")]
    [Description("If this item is a Phrase or Sentence, list the target-language words that compose it as they should appear as separate vocabulary entries. Use dictionary/lemma form (e.g. 공부하다, not 공부하고). Empty array for single Words.")]
    public List<string> RelatedTerms { get; set; } = new();

    /// <summary>
    /// Converts this extraction DTO into a persistable VocabularyWord.
    /// </summary>
    public VocabularyWord ToVocabularyWord(string language = "Korean")
    {
        // Build tags including RelatedTerms hint if present
        var tagsList = new List<string>();
        if (!string.IsNullOrWhiteSpace(Tags))
            tagsList.Add(Tags);
        
        if (RelatedTerms.Any())
            tagsList.Add($"constituents:{string.Join(",", RelatedTerms)}");

        return new VocabularyWord
        {
            TargetLanguageTerm = TargetLanguageTerm,
            NativeLanguageTerm = NativeLanguageTerm,
            Lemma = string.IsNullOrWhiteSpace(Lemma) ? TargetLanguageTerm : Lemma,
            Language = language,
            Tags = tagsList.Any() ? string.Join("; ", tagsList) : null,
            LexicalUnitType = LexicalUnitType
        };
    }
}
