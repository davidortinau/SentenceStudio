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
        int count = 3,
        SpeechRegister targetRegister = SpeechRegister.Unspecified)
    {
        _logger.LogInformation("🤖 Generating {Count} example sentences for word: {Word}", count, word.TargetLanguageTerm);

        var prompt = BuildPrompt(word, nativeLanguage, targetLanguage, count, targetRegister);
        
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

    /// <summary>
    /// Generate AI example sentences as unsaved <see cref="ExampleSentence"/> suggestions.
    /// Each is tagged Source=AiGenerated, Status=Suggested (NOT quiz-eligible until the
    /// learner keeps/edits it), with register and difficulty metadata carried over.
    /// </summary>
    public async Task<List<ExampleSentence>> GenerateSuggestionsAsync(
        VocabularyWord word,
        string nativeLanguage = "English",
        string targetLanguage = "Korean",
        int count = 3,
        SpeechRegister targetRegister = SpeechRegister.Unspecified)
    {
        var dtos = await GenerateExampleSentencesAsync(word, nativeLanguage, targetLanguage, count, targetRegister);

        return dtos
            .Where(d => !string.IsNullOrWhiteSpace(d.TargetSentence))
            .Select(d => new ExampleSentence
            {
                VocabularyWordId = word.Id,
                TargetSentence = d.TargetSentence.Trim(),
                NativeSentence = string.IsNullOrWhiteSpace(d.NativeSentence) ? null : d.NativeSentence.Trim(),
                IsCore = false,
                Source = ExampleSentenceSource.AiGenerated,
                Status = ExampleSentenceStatus.Suggested,
                Register = d.Register,
                DifficultyLevel = d.DifficultyLevel is >= 1 and <= 5 ? d.DifficultyLevel : null,
            })
            .ToList();
    }

    private string BuildPrompt(VocabularyWord word, string nativeLanguage, string targetLanguage, int count, SpeechRegister targetRegister)
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

        if (targetRegister != SpeechRegister.Unspecified)
        {
            promptParts.Add($"Write all sentences in the {DescribeRegister(targetRegister)} Korean speech level, used consistently.");
        }

        // Add generation guidelines
        promptParts.Add("\nGuidelines:");
        promptParts.Add($"- Create {count} diverse example sentences showing different usages");
        promptParts.Add("- The target word should be the only unfamiliar item; keep every other word common (i+1)");
        promptParts.Add("- Prefer the most natural, high-frequency usage and show a real collocation, not a contrived frame");
        promptParts.Add("- Make sentences natural and practical for learners");
        promptParts.Add("- Mark the most common/useful sentence as IsCore=true (only one)");
        promptParts.Add("- Set Register to the actual speech level of each sentence");
        promptParts.Add("- Set DifficultyLevel from 1 (short, concrete) to 5 (long/abstract) for each sentence");
        promptParts.Add("- Provide accurate translations");
        promptParts.Add($"- Keep sentences between 5-15 words in {targetLanguage}");
        promptParts.Add("- Keep honorific marking (시) consistent within each sentence");

        return string.Join(" ", promptParts);
    }

    private static string DescribeRegister(SpeechRegister register) => register switch
    {
        SpeechRegister.FormalPolite => "formal-polite (합쇼체)",
        SpeechRegister.InformalPolite => "informal-polite (해요체)",
        SpeechRegister.Casual => "casual / 반말",
        SpeechRegister.PlainWritten => "plain written (한다체)",
        _ => "appropriate",
    };
}
