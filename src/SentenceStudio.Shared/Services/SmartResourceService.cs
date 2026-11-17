using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

    // One seed at a time per learner in this process. See InitializeSmartResourcesAsync.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _seedGates = new(StringComparer.Ordinal);

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
    /// Creates the default smart resources that don't exist yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check for which types exist and the inserts are separate steps, so two seeds for one
    /// learner at the same time both found nothing and both inserted. A page load asks for the
    /// profile from several components at once, and every ask seeded, so a new learner ended up
    /// with the same list two or three times over (seen 2026-11-16: fourteen lists where there
    /// should be five).
    /// </para>
    /// <para>
    /// Seeds for one learner now run one at a time: in this process behind a gate per learner,
    /// and on PostgreSQL also behind a transaction-scoped advisory lock, so two instances of the
    /// app serving the same learner cannot both seed. The second seed waits, then finds every type
    /// present and creates nothing.
    /// </para>
    /// <para>
    /// On the server's database (PostgreSQL) the seed also folds extra copies already stored
    /// into one (<see cref="MergeExtraCopiesAsync"/>). A device leaves that to the server, which
    /// holds every synced row that can name a copy, and receives its deletes.
    /// </para>
    /// </remarks>
    public async Task InitializeSmartResourcesAsync(string targetLanguage = "Korean", string userId = "")
    {
        var learner = userId ?? string.Empty;
        var gate = _seedGates.GetOrAdd(learner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The lists created, and those kept by a fold. Collected across attempts: a retried
            // seed skips a list an earlier attempt already committed, and that list still has to
            // be filled.
            var toFill = new List<string>();
            await UnderDatabaseSeedLockAsync(learner,
                onServer => CreateMissingSmartResourcesAsync(targetLanguage, learner, toFill, onServer)).ConfigureAwait(false);

            if (toFill.Count == 0)
            {
                return;
            }

            // Filled after the lock: a second seed only needs the lists to exist to skip them.
            foreach (var resourceId in toFill.Distinct(StringComparer.Ordinal))
            {
                await RefreshSmartResourceAsync(resourceId, learner).ConfigureAwait(false);
            }

            _logger.LogInformation("Smart resources filled after seeding: {Count}", toFill.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error initializing smart resources");
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="seed"/> while holding a PostgreSQL advisory lock for this learner's
    /// smart resources. Other providers (a device's SQLite database, one process) run it as is.
    /// The seed is told whether it runs on the server's database (PostgreSQL).
    /// </summary>
    /// <remarks>
    /// The lock is taken in a transaction of its own and released when it commits. The seed's
    /// reads and writes go through the repository on their own connections; the lock only makes a
    /// second seed wait until the first one's rows are committed and visible.
    /// </remarks>
    private async Task UnderDatabaseSeedLockAsync(string userId, Func<bool, Task> seed)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetService<ApplicationDbContext>();
        if (db?.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
        {
            await seed(false).ConfigureAwait(false);
            return;
        }

        var key = SeedLockKey(userId);

        // Under the provider's retrying execution strategy a transaction has to be opened inside
        // it. A retry runs the seed again, which is safe: it creates only the types still missing.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", key).ConfigureAwait(false);
            await seed(true).ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>A stable advisory lock key for one learner's smart resources.</summary>
    private static long SeedLockKey(string userId) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("smart-resources:" + userId)), 0);

    /// <summary>
    /// Keeps one smart resource per type for the learner and folds the others into it
    /// (<see cref="LearningResourceRepository.MergeSmartResourceCopiesAsync"/>). Each kept list
    /// is added to <paramref name="toFill"/>: the deleted copies' links went with them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run only on the server's database. A device holds only what it has synced so far, so a
    /// fold there could delete the server's lists while rows it has not downloaded, or another
    /// device has not uploaded, still name them.
    /// </para>
    /// <para>
    /// The kept copy is the one with the lowest ID (ordinal), which compares the same everywhere
    /// (not the oldest: smart lists made before the UTC change hold a local wall clock). A list
    /// made before the fixed IDs (<see cref="SyncIds.SmartResource"/>) is a GUID, which sorts
    /// before every "sr:" ID, so the list the learner's devices already use is kept and a copy a
    /// new device made before its first sync is the one folded. That device then has the type
    /// and does not make it again.
    /// </para>
    /// </remarks>
    private async Task MergeExtraCopiesAsync(List<LearningResource> smartResources, string userId, List<string> toFill)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return;

        foreach (var group in smartResources
            .Where(r => !string.IsNullOrEmpty(r.SmartResourceType))
            .GroupBy(r => r.SmartResourceType!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1))
        {
            var ordered = group.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
            var keeper = ordered[0];
            var copies = ordered.Skip(1).Select(r => r.Id).ToList();

            // The kept list stays either way, so the seed creates nothing for this type.
            var removed = await _resourceRepo.MergeSmartResourceCopiesAsync(keeper.Id, copies, userId).ConfigureAwait(false);
            if (removed > 0)
            {
                toFill.Add(keeper.Id);
                _logger.LogInformation("Folded {Count} extra copies of smart resource type {Type} into {ResourceId}",
                    removed, group.Key, keeper.Id);
            }
            else if (removed < 0)
            {
                _logger.LogWarning("Could not fold the extra copies of smart resource type {Type}", group.Key);
            }
        }
    }

    private async Task CreateMissingSmartResourcesAsync(string targetLanguage, string userId, List<string> toFill, bool onServer)
    {
        _logger.LogInformation("🎯 Initializing smart resources for language: {Language}", targetLanguage);

        try
        {
            // Per-type idempotency: check each smart resource type independently so
            // upgraded users who pre-date a newly-added type (e.g. Phrases) still
            // get the missing entry seeded without duplicating existing ones.
            var existingSmartResources = await _resourceRepo.GetSmartResourcesAsync(userId);
            if (onServer)
                await MergeExtraCopiesAsync(existingSmartResources, userId, toFill).ConfigureAwait(false);
            var existingTypes = new HashSet<string>(
                existingSmartResources
                    .Where(r => !string.IsNullOrEmpty(r.SmartResourceType))
                    .Select(r => r.SmartResourceType!),
                StringComparer.Ordinal);

            // CreatedAt is a UTC instant like every other persisted timestamp. Smart
            // rows created before the UTC change hold a local wall clock (display-only
            // skew; see docs/native-auth-readiness.md). Order preserved.
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
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
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
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
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
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
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
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
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
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            };

            var createdCount = 0;
            foreach (var def in definitions)
            {
                // One ID per learner and type in every database. The MAUI app seeds its own
                // database too, and a seed there before the device's first sync made a second
                // set with new IDs, which sync uploaded beside the server's. Lists made with the
                // same ID sync as one row; beside a learner's older GUID lists such a copy is
                // folded on the server (MergeExtraCopiesAsync).
                if (!string.IsNullOrWhiteSpace(userId))
                    def.Id = SyncIds.SmartResource(userId, def.SmartResourceType!);
                if (existingTypes.Contains(def.SmartResourceType!))
                {
                    _logger.LogDebug("↪️ Smart resource type '{Type}' already exists, skipping create", def.SmartResourceType);
                    continue;
                }

                await _resourceRepo.SaveResourceAsync(def, userId);
                toFill.Add(def.Id);
                createdCount++;
                _logger.LogInformation("✅ Created smart resource: {Title} ({Type})", def.Title, def.SmartResourceType);
            }

            if (createdCount == 0)
            {
                _logger.LogInformation("✅ All smart resources already exist, no seeding required");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error creating smart resources");
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
        // Resolve "today" via IPlanDateContext (user-local date). NextReviewDate is a
        // UTC instant, so the cutoff is the instant that opens the learner's local day
        // (DueCutoffUtc), matching DeterministicPlanBuilder and ProgressService.
        //
        // SmartResourceService is a singleton, so IPlanDateContext (transient
        // on mobile, scoped on server) must be resolved per-call via the
        // service provider.
        using var scope = _serviceProvider.CreateScope();
        var dateContext = scope.ServiceProvider.GetRequiredService<Plans.IPlanDateContext>();
        // NextReviewDate is a UTC instant: "due today" means due by the instant that
        // opens the learner's local day (not the UTC-labeled day key).
        var dueCutoffUtc = Plans.PlanDateContextExtensions.DueCutoffUtc(dateContext);

        var dueWords = await _progressRepo.GetDueVocabularyAsync(dueCutoffUtc, userId);

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
        // The learner's own words (the one ownership rule)
        var allWords = string.IsNullOrWhiteSpace(userId)
            ? new List<VocabularyWord>()
            : await _resourceRepo.GetAllVocabularyWordsWithResourcesAsync(userId);

        // Get all progress records
        var allProgress = await _progressRepo.ListAsync();
        var userProgress = allProgress.Where(vp => vp.UserId == userId).ToList();
        var progressDict = userProgress.ToDictionary(vp => vp.VocabularyWordId);

        // Find words with no progress or zero attempts
        // A word marked Known with no practice is not new (the one Known rule).
        var mode = await _progressRepo.GetLearningModeAsync(userId);
        var newWordIds = allWords
            .Where(w => !progressDict.TryGetValue(w.Id, out var progress)
                || (progress.TotalAttempts == 0 && !VocabularyKnownRule.IsKnown(progress, mode)))
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

        var mode = await _progressRepo.GetLearningModeAsync(userId);
        var strugglingWordIds = userProgress
            .Where(vp => vp.TotalAttempts >= STRUGGLING_MIN_ATTEMPTS &&
                         vp.MasteryScore < STRUGGLING_MAX_MASTERY &&
                         !VocabularyKnownRule.IsKnown(vp, mode))
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

        if (userWordIds.Count == 0 || string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogDebug("📝 Phrases found 0 phrase entries (no user progress)");
            return new List<string>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var phraseWordIds = await db.VocabularyWords.OwnedBy(userId)
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

        if (userWordIds.Count == 0 || string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogDebug("📖 Sentences found 0 sentence entries (no user progress)");
            return new List<string>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var sentenceWordIds = await db.VocabularyWords.OwnedBy(userId)
            .Where(w => userWordIds.Contains(w.Id))
            .Where(w => w.LexicalUnitType == LexicalUnitType.Sentence)
            .Select(w => w.Id)
            .ToListAsync();

        _logger.LogDebug("📖 Sentences found {Count} sentence entries", sentenceWordIds.Count);
        return sentenceWordIds;
    }
}
