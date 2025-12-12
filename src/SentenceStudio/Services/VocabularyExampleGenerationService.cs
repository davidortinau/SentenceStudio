using Microsoft.Extensions.Logging;
using SentenceStudio.Services.DTOs;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public class VocabularyExampleGenerationService
{
    private readonly AiService _aiService;
    private readonly ILogger<VocabularyExampleGenerationService> _logger;

    public VocabularyExampleGenerationService(
        AiService aiService,
        ILogger<VocabularyExampleGenerationService> logger)
    {
        _aiService = aiService;
        _logger = logger;
    }

    /// <summary>
    /// Generate example sentences for a vocabulary word using AI
    /// </summary>
    /// <param name="word">The vocabulary word to generate examples for</param>
    /// <param name="nativeLanguage">Native language for translations (e.g., "English")</param>
    /// <param name="targetLanguage">Target language of the vocabulary (e.g., "Korean")</param>
    /// <param name="count">Number of example sentences to generate (default: 3)</param>
    /// <returns>Generated example sentences with translations</returns>
    public async Task<List<GeneratedSentenceDto>> GenerateExampleSentencesAsync(
        VocabularyWord word,
        string nativeLanguage = "English",
        string targetLanguage = "Korean",
        int count = 3)
    {
        _logger.LogInformation("🤖 Generating {Count} example sentences for word: {Word}", count, word.TargetLanguageTerm);

        var prompt = BuildPrompt(word, nativeLanguage, targetLanguage, count);
        
        try
        {
            var result = await _aiService.SendPrompt<GeneratedExampleSentencesDto>(prompt);
            
            if (result == null || result.Sentences == null || !result.Sentences.Any())
            {
                _logger.LogWarning("AI returned no example sentences for word: {Word}", word.TargetLanguageTerm);
                return new List<GeneratedSentenceDto>();
            }

            _logger.LogInformation("✅ Successfully generated {Count} example sentences", result.Sentences.Count);
            return result.Sentences;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate example sentences for word: {Word}", word.TargetLanguageTerm);
            return new List<GeneratedSentenceDto>();
        }
    }

    private string BuildPrompt(VocabularyWord word, string nativeLanguage, string targetLanguage, int count)
    {
        var promptParts = new List<string>
        {
            $"Generate {count} example sentences for the {targetLanguage} vocabulary word \"{word.TargetLanguageTerm}\"",
            $"with {nativeLanguage} translations."
        };

        // Add context from existing data
        if (!string.IsNullOrWhiteSpace(word.NativeLanguageTerm))
        {
            promptParts.Add($"The word means \"{word.NativeLanguageTerm}\" in {nativeLanguage}.");
        }

        if (!string.IsNullOrWhiteSpace(word.Lemma))
        {
            promptParts.Add($"The dictionary form (lemma) is \"{word.Lemma}\".");
        }

        if (!string.IsNullOrWhiteSpace(word.Tags))
        {
            promptParts.Add($"Context tags: {word.Tags}.");
        }

        // Add generation guidelines
        promptParts.Add("\nGuidelines:");
        promptParts.Add($"- Create {count} diverse example sentences showing different usages");
        promptParts.Add("- Make sentences natural and practical for learners");
        promptParts.Add("- Mark the most common/useful sentence as IsCore=true (only one)");
        promptParts.Add("- Provide accurate translations");
        promptParts.Add($"- Keep sentences between 5-15 words in {targetLanguage}");
        promptParts.Add("- Show different contexts (formal, informal, common situations)");

        return string.Join(" ", promptParts);
    }
}
