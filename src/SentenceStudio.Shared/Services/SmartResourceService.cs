using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceProvider _serviceProvider;

    // Smart resource type constants
    public const string SmartResourceType_DailyReview = "DailyReview";
    public const string SmartResourceType_NewWords = "NewWords";
    public const string SmartResourceType_Struggling = "Struggling";
    public const string SmartResourceType_Phrases = "Phrases";
    public const string SmartResourceType_Sentences = "Sentences";

    // Threshold for "struggling" words
    private const int STRUGGLING_MIN_ATTEMPTS = 5;
    private const float STRUGGLING_MAX_MASTERY = 0.5f;

    // Mastery threshold (words below this are not graduated)
    private const float MASTERY_THRESHOLD = 0.85f;

    public SmartResourceService(
        LearningResourceRepository resourceRepo,
        VocabularyProgressRepository progressRepo,
        ILogger<SmartResourceService> logger,
        IServiceProvider serviceProvider)
    {
        _resourceRepo = resourceRepo;
        _progressRepo = progressRepo;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Initialize smart resources on first app launch.
    /// Creates three default smart resources if they don't exist.
    /// </summary>
    public async Task InitializeSmartResourcesAsync(string targetLanguage = "Korean", string userId = "")
    {
        _logger.LogInformation("🎯 Initializing smart resources for language: {Language}", targetLanguage);

        try
        {
            // Per-type idempotency: check each smart resource type independently so
            // upgraded users who pre-date a newly-added type (e.g. Phrases) still
            // get the missing entry seeded without duplicating existing ones.
            var existingSmartResources = await _resourceRepo.GetSmartResourcesAsync(userId);
            var existingTypes = new HashSet<string>(
                existingSmartResources
                    .Where(r => !string.IsNullOrEmpty(r.SmartResourceType))
                    .Select(r => r.SmartResourceType!),
                StringComparer.Ordinal);

            // Seed definitions for each smart resource type. Order preserved.
            var definitions = new[]
            {
                new LearningResource
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
                },
                new LearningResource
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
                },
                new LearningResource
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
                },
                new LearningResource
                {
                    Title = "Phrases",
                    Description = "Practice all your phrase vocabulary",
                    MediaType = "Smart Vocabulary List",
                    Language = targetLanguage,
                    Tags = "system-generated,dynamic,phrases",
                    IsSmartResource = true,
                    SmartResourceType = SmartResourceType_Phrases,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                },
                new LearningResource
                {
                    Title = "Sentences",
                    Description = "Practice all your sentence vocabulary",
                    MediaType = "Smart Vocabulary List",
                    Language = targetLanguage,
                    Tags = "system-generated,dynamic,sentences",
                    IsSmartResource = true,
                    SmartResourceType = SmartResourceType_Sentences,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            };

            var createdResources = new List<LearningResource>();
            foreach (var def in definitions)
            {
                if (existingTypes.Contains(def.SmartResourceType!))
                {
                    _logger.LogDebug("↪️ Smart resource type '{Type}' already exists, skipping create", def.SmartResourceType);
                    continue;
                }

                await _resourceRepo.SaveResourceAsync(def, userId);
                createdResources.Add(def);
                _logger.LogInformation("✅ Created smart resource: {Title} ({Type})", def.Title, def.SmartResourceType);
            }

            if (createdResources.Count == 0)
            {
                _logger.LogInformation("✅ All smart resources already exist, no seeding required");
                return;
            }

            // Perform initial refresh to populate vocabulary for newly created resources only.
            foreach (var created in createdResources)
            {
                await RefreshSmartResourceAsync(created.Id, userId);
            }

            _logger.LogInformation("✅ Smart resources initialized and refreshed ({Count} new)", createdResources.Count);
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
    public async Task RefreshSmartResourceAsync(string resourceId, string userId = "")
    {
        try
        {
            // Get the smart resource
            var resource = await _resourceRepo.GetResourceAsync(resourceId, userId);
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
            var existingWords = await _resourceRepo.GetVocabularyWordsByResourceAsync(resourceId, userId);
            if (existingWords.Any())
            {
                var existingWordIds = existingWords.Select(w => w.Id).ToList();
                await _resourceRepo.BulkRemoveWordsFromResourceAsync(resourceId, existingWordIds, userId);
                _logger.LogDebug("🗑️ Removed {Count} existing word associations", existingWordIds.Count);
            }

            // Associate new words
            if (vocabularyWordIds.Any())
            {
                await _resourceRepo.BulkAssociateWordsWithResourceAsync(resourceId, vocabularyWordIds, userId);
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
    public async Task RefreshAllSmartResourcesAsync(string userId = "")
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
    private async Task<List<string>> GetSmartResourceVocabularyIdsAsync(string smartResourceType, string userId = "")
    {
        return smartResourceType switch
        {
            SmartResourceType_DailyReview => await GetDailyReviewVocabularyIdsAsync(userId),
            SmartResourceType_NewWords => await GetNewWordsVocabularyIdsAsync(userId),
            SmartResourceType_Struggling => await GetStrugglingWordsVocabularyIdsAsync(userId),
            SmartResourceType_Phrases => await GetPhrasesVocabularyIdsAsync(userId),
            SmartResourceType_Sentences => await GetSentencesVocabularyIdsAsync(userId),
            _ => new List<string>()
        };
    }

    /// <summary>
    /// Get vocabulary IDs for Daily Review: words due for SRS review today.
    /// Selection: NextReviewDate <= Today AND MasteryScore < 0.85
    /// </summary>
    private async Task<List<string>> GetDailyReviewVocabularyIdsAsync(string userId = "")
    {
        // Resolve "today" via IPlanDateContext (user-local date) so this cutoff
        // matches DeterministicPlanBuilder.BuildPlanAsync, ProgressService.
        // ResolveTodayKey, and PlanService.ToDateKey — all of which stamp the
        // user-local date as a UTC-kinded byte sequence (NOT a true UTC
        // instant) so on-disk parity with DailyPlan.Date / DailyPlanCompletion
        // .Date / VocabularyProgress.NextReviewDate is preserved. Calling
        // ToUtcMidnight here would do a real TZ conversion and drift from
        // those values by the user's UTC offset.
        //
        // SmartResourceService is a singleton, so IPlanDateContext (transient
        // on mobile, scoped on server) must be resolved per-call via the
        // service provider.
        using var scope = _serviceProvider.CreateScope();
        var dateContext = scope.ServiceProvider.GetRequiredService<Plans.IPlanDateContext>();
        var today = dateContext.UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var dueWords = await _progressRepo.GetDueVocabularyAsync(today, userId);

        var wordIds = dueWords
            .Where(vp => vp.MasteryScore < MASTERY_THRESHOLD) // Not graduated
            .Select(vp => vp.VocabularyWordId)
            .ToList();

        _logger.LogDebug("📅 Daily Review found {Count} due words (cutoff {Cutoff:yyyy-MM-dd} user-local)",
            wordIds.Count, dateContext.UserLocalDate);
        return wordIds;
    }

    /// <summary>
    /// Get vocabulary IDs for New Words: words never practiced.
    /// Selection: No progress record OR TotalAttempts = 0
    /// </summary>
    private async Task<List<string>> GetNewWordsVocabularyIdsAsync(string userId = "")
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
    private async Task<List<string>> GetStrugglingWordsVocabularyIdsAsync(string userId = "")
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

    /// <summary>
    /// Get vocabulary IDs for Phrases: all phrase vocabulary only.
    /// Selection: LexicalUnitType = Phrase, scoped by user via VocabularyProgress.
    /// </summary>
    private async Task<List<string>> GetPhrasesVocabularyIdsAsync(string userId = "")
    {
        var allProgress = await _progressRepo.ListAsync();
        var userWordIds = allProgress
            .Where(vp => vp.UserId == userId)
            .Select(vp => vp.VocabularyWordId)
            .Distinct()
            .ToList();

        if (userWordIds.Count == 0)
        {
            _logger.LogDebug("📝 Phrases found 0 phrase entries (no user progress)");
            return new List<string>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var phraseWordIds = await db.VocabularyWords
            .Where(w => userWordIds.Contains(w.Id))
            .Where(w => w.LexicalUnitType == LexicalUnitType.Phrase)
            .Select(w => w.Id)
            .ToListAsync();

        _logger.LogDebug("📝 Phrases found {Count} phrase entries", phraseWordIds.Count);
        return phraseWordIds;
    }

    /// <summary>
    /// Get vocabulary IDs for Sentences: all sentence vocabulary only.
    /// Selection: LexicalUnitType = Sentence, scoped by user via VocabularyProgress.
    /// </summary>
    private async Task<List<string>> GetSentencesVocabularyIdsAsync(string userId = "")
    {
        var allProgress = await _progressRepo.ListAsync();
        var userWordIds = allProgress
            .Where(vp => vp.UserId == userId)
            .Select(vp => vp.VocabularyWordId)
            .Distinct()
            .ToList();

        if (userWordIds.Count == 0)
        {
            _logger.LogDebug("📖 Sentences found 0 sentence entries (no user progress)");
            return new List<string>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var sentenceWordIds = await db.VocabularyWords
            .Where(w => userWordIds.Contains(w.Id))
            .Where(w => w.LexicalUnitType == LexicalUnitType.Sentence)
            .Select(w => w.Id)
            .ToListAsync();

        _logger.LogDebug("📖 Sentences found {Count} sentence entries", sentenceWordIds.Count);
        return sentenceWordIds;
    }
}
