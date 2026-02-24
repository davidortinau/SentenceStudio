using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Agents;

/// <summary>
/// AI Tool that provides vocabulary lookup capabilities to conversation agents.
/// Searches across all vocabulary words in the user's learning resources.
/// </summary>
public class VocabularyLookupTool
{
    private readonly LearningResourceRepository _repository;
    private readonly ILogger<VocabularyLookupTool> _logger;

    public VocabularyLookupTool(LearningResourceRepository repository, ILogger<VocabularyLookupTool> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Creates the AIFunction that can be registered with agents.
    /// </summary>
    public AIFunction CreateFunction()
    {
        return AIFunctionFactory.Create(LookupVocabularyAsync, nameof(LookupVocabularyAsync));
    }

    [Description("Look up vocabulary words from the user's learning resources. Use this to find Korean words, their English meanings, and example sentences.")]
    public async Task<VocabularyLookupResult> LookupVocabularyAsync(
        [Description("The Korean or English term to search for. Can be partial match.")] string searchTerm,
        [Description("Maximum number of results to return. Default is 5.")] int limit = 5)
    {
        _logger.LogDebug("VocabularyLookupTool: Searching for '{SearchTerm}' with limit {Limit}", searchTerm, limit);

        var result = new VocabularyLookupResult
        {
            SearchTerm = searchTerm
        };

        try
        {
            // Search in both Korean (target) and English (native) terms
            var allWords = await _repository.GetAllVocabularyWordsAsync();
            
            var matches = allWords
                .Where(w => 
                    (w.TargetLanguageTerm?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (w.NativeLanguageTerm?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (w.Lemma?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(limit)
                .Select(w => new VocabularyMatch
                {
                    TargetTerm = w.TargetLanguageTerm ?? string.Empty,
                    NativeTerm = w.NativeLanguageTerm ?? string.Empty,
                    Mnemonic = w.MnemonicText,
                    Examples = w.ExampleSentences?
                        .Take(2)
                        .Select(e => e.TargetSentence ?? string.Empty)
                        .ToList() ?? new List<string>()
                })
                .ToList();

            result.Matches = matches;
            result.TotalCount = matches.Count;

            _logger.LogDebug("VocabularyLookupTool: Found {Count} matches for '{SearchTerm}'", matches.Count, searchTerm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VocabularyLookupTool: Error searching for '{SearchTerm}'", searchTerm);
        }

        return result;
    }

    /// <summary>
    /// Search for words by tag/category.
    /// </summary>
    [Description("Search vocabulary words by tag or category (e.g., 'food', 'travel', 'business')")]
    public async Task<VocabularyLookupResult> SearchByTagAsync(
        [Description("The tag or category to search for")] string tag,
        [Description("Maximum number of results to return")] int limit = 10)
    {
        _logger.LogDebug("VocabularyLookupTool: Searching by tag '{Tag}'", tag);

        var result = new VocabularyLookupResult
        {
            SearchTerm = $"tag:{tag}"
        };

        try
        {
            var allWords = await _repository.GetAllVocabularyWordsAsync();
            
            var matches = allWords
                .Where(w => w.Tags?.Contains(tag, StringComparison.OrdinalIgnoreCase) ?? false)
                .Take(limit)
                .Select(w => new VocabularyMatch
                {
                    TargetTerm = w.TargetLanguageTerm ?? string.Empty,
                    NativeTerm = w.NativeLanguageTerm ?? string.Empty,
                    Mnemonic = w.MnemonicText,
                    Examples = w.ExampleSentences?
                        .Take(2)
                        .Select(e => e.TargetSentence ?? string.Empty)
                        .ToList() ?? new List<string>()
                })
                .ToList();

            result.Matches = matches;
            result.TotalCount = matches.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VocabularyLookupTool: Error searching by tag '{Tag}'", tag);
        }

        return result;
    }
}
