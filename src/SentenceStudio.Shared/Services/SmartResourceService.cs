using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Service for managing smart/dynamic learning resources that automatically
/// provide vocabulary based on SRS (Spaced Repetition System) rules.
/// </summary>
public class SmartResourceService
{
    private readonly LearningResourceRepository _resourceRepo;
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly ILogger<SmartResourceService> _logger;

    // Smart resource type constants
    public const string SmartResourceType_DailyReview = "DailyReview";
    public const string SmartResourceType_NewWords = "NewWords";
    public const string SmartResourceType_Struggling = "Struggling";

    // Threshold for "struggling" words
    private const int STRUGGLING_MIN_ATTEMPTS = 5;
    private const float STRUGGLING_MAX_MASTERY = 0.5f;

    // Mastery threshold (words below this are not graduated)
    private const float MASTERY_THRESHOLD = 0.85f;

    public SmartResourceService(
        LearningResourceRepository resourceRepo,
        VocabularyProgressRepository progressRepo,
        ILogger<SmartResourceService> logger)
    {
        _resourceRepo = resourceRepo;
        _progressRepo = progressRepo;
        _logger = logger;
    }

    /// <summary>
    /// Initialize smart resources on first app launch.
    /// Creates three default smart resources if they don't exist.
    /// </summary>
    public async Task InitializeSmartResourcesAsync(string targetLanguage = "Korean", int userId = 0)
    {
        _logger.LogInformation("🎯 Initializing smart resources for language: {Language}", targetLanguage);

        try
        {
            // Check if smart resources already exist
            var existingSmartResources = await _resourceRepo.GetSmartResourcesAsync();
            if (existingSmartResources.Any())
            {
                _logger.LogInformation("✅ Smart resources already exist, skipping initialization");
                return;
            }

            // Create Daily Review resource
            var dailyReview = new LearningResource
            {
                Title = "Daily Review",
                Description = "Practice vocabulary due for review today based on spaced repetition",
                MediaType = "Smart Vocabulary List",
                Language = targetLanguage,
                Tags = "system-generated,dynamic,srs",
                IsSmartResource = true,
                SmartResourceType = SmartResourceType_DailyReview,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            // Create New Words resource
            var newWords = new LearningResource
            {
                Title = "New Words Practice",
                Description = "Focus on vocabulary you haven't practiced yet",
                MediaType = "Smart Vocabulary List",
                Language = targetLanguage,
                Tags = "system-generated,dynamic,new",
                IsSmartResource = true,
                SmartResourceType = SmartResourceType_NewWords,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            // Create Struggling Words resource
            var strugglingWords = new LearningResource
            {
                Title = "Struggling Words",
                Description = "Target vocabulary that needs extra attention",
                MediaType = "Smart Vocabulary List",
                Language = targetLanguage,
                Tags = "system-generated,dynamic,review",
                IsSmartResource = true,
                SmartResourceType = SmartResourceType_Struggling,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            // Save resources
            await _resourceRepo.SaveResourceAsync(dailyReview);
            await _resourceRepo.SaveResourceAsync(newWords);
            await _resourceRepo.SaveResourceAsync(strugglingWords);

            _logger.LogInformation("✅ Created 3 smart resources: Daily Review, New Words, Struggling Words");

            // Perform initial refresh to populate vocabulary
            await RefreshSmartResourceAsync(dailyReview.Id, userId);
            await RefreshSmartResourceAsync(newWords.Id, userId);
            await RefreshSmartResourceAsync(strugglingWords.Id, userId);

            _logger.LogInformation("✅ Smart resources initialized and refreshed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error initializing smart resources");
            throw;
        }
    }

    /// <summary>
    /// Refresh a smart resource by clearing existing vocabulary and re-populating
    /// based on its type and current SRS state.
    /// </summary>
    public async Task RefreshSmartResourceAsync(int resourceId, int userId = 0)
    {
        try
        {
            // Get the smart resource
            var resource = await _resourceRepo.GetResourceAsync(resourceId);
            if (resource == null || !resource.IsSmartResource)
            {
                _logger.LogWarning("⚠️ Resource {ResourceId} is not a smart resource, skipping refresh", resourceId);
                return;
            }

            _logger.LogDebug("🔄 Refreshing smart resource: {Title} (Type: {Type})",
                resource.Title, resource.SmartResourceType);

            // Get vocabulary IDs based on smart resource type
            var vocabularyWordIds = await GetSmartResourceVocabularyIdsAsync(resource.SmartResourceType!, userId);

            _logger.LogInformation("📚 Smart resource '{Title}' found {Count} words",
                resource.Title, vocabularyWordIds.Count);

            // Clear existing associations
            var existingWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(resourceId);
            if (existingWords.Any())
            {
                var existingWordIds = existingWords.Select(w => w.Id).ToList();
                await _resourceRepo.BulkRemoveWordsFromResourceAsync(resourceId, existingWordIds);
                _logger.LogDebug("🗑️ Removed {Count} existing word associations", existingWordIds.Count);
            }

            // Associate new words
            if (vocabularyWordIds.Any())
            {
                await _resourceRepo.BulkAssociateWordsWithResourceAsync(resourceId, vocabularyWordIds);
                _logger.LogDebug("✅ Associated {Count} new words", vocabularyWordIds.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error refreshing smart resource {ResourceId}", resourceId);
            throw;
        }
    }

    /// <summary>
    /// Refresh all smart resources at once (e.g., on app launch).
    /// </summary>
    public async Task RefreshAllSmartResourcesAsync(int userId = 0)
    {
        _logger.LogInformation("🔄 Refreshing all smart resources");

        try
        {
            var smartResources = await _resourceRepo.GetSmartResourcesAsync();

            foreach (var resource in smartResources)
            {
                await RefreshSmartResourceAsync(resource.Id, userId);
            }

            _logger.LogInformation("✅ Refreshed {Count} smart resources", smartResources.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error refreshing all smart resources");
            throw;
        }
    }

    /// <summary>
    /// Get vocabulary word IDs for a specific smart resource type.
    /// </summary>
    private async Task<List<int>> GetSmartResourceVocabularyIdsAsync(string smartResourceType, int userId = 0)
    {
        return smartResourceType switch
        {
            SmartResourceType_DailyReview => await GetDailyReviewVocabularyIdsAsync(userId),
            SmartResourceType_NewWords => await GetNewWordsVocabularyIdsAsync(userId),
            SmartResourceType_Struggling => await GetStrugglingWordsVocabularyIdsAsync(userId),
            _ => new List<int>()
        };
    }

    /// <summary>
    /// Get vocabulary IDs for Daily Review: words due for SRS review today.
    /// Selection: NextReviewDate <= Today AND MasteryScore < 0.85
    /// </summary>
    private async Task<List<int>> GetDailyReviewVocabularyIdsAsync(int userId = 0)
    {
        var dueWords = await _progressRepo.GetDueVocabularyAsync(DateTime.Today, userId);

        var wordIds = dueWords
            .Where(vp => vp.MasteryScore < MASTERY_THRESHOLD) // Not graduated
            .Select(vp => vp.VocabularyWordId)
            .ToList();

        _logger.LogDebug("📅 Daily Review found {Count} due words", wordIds.Count);
        return wordIds;
    }

    /// <summary>
    /// Get vocabulary IDs for New Words: words never practiced.
    /// Selection: No progress record OR TotalAttempts = 0
    /// </summary>
    private async Task<List<int>> GetNewWordsVocabularyIdsAsync(int userId = 0)
    {
        // Get all vocabulary words
        var allWords = await _resourceRepo.GetAllVocabularyWordsAsync();

        // Get all progress records
        var allProgress = await _progressRepo.ListAsync();
        var userProgress = allProgress.Where(vp => vp.UserId == userId).ToList();
        var progressDict = userProgress.ToDictionary(vp => vp.VocabularyWordId);

        // Find words with no progress or zero attempts
        var newWordIds = allWords
            .Where(w => !progressDict.ContainsKey(w.Id) || progressDict[w.Id].TotalAttempts == 0)
            .Select(w => w.Id)
            .ToList();

        _logger.LogDebug("✨ New Words found {Count} unpracticed words", newWordIds.Count);
        return newWordIds;
    }

    /// <summary>
    /// Get vocabulary IDs for Struggling Words: low mastery despite multiple attempts.
    /// Selection: TotalAttempts >= 5 AND MasteryScore < 0.5
    /// </summary>
    private async Task<List<int>> GetStrugglingWordsVocabularyIdsAsync(int userId = 0)
    {
        var allProgress = await _progressRepo.ListAsync();
        var userProgress = allProgress.Where(vp => vp.UserId == userId).ToList();

        var strugglingWordIds = userProgress
            .Where(vp => vp.TotalAttempts >= STRUGGLING_MIN_ATTEMPTS &&
                         vp.MasteryScore < STRUGGLING_MAX_MASTERY)
            .Select(vp => vp.VocabularyWordId)
            .ToList();

        _logger.LogDebug("💪 Struggling Words found {Count} words needing attention", strugglingWordIds.Count);
        return strugglingWordIds;
    }
}
