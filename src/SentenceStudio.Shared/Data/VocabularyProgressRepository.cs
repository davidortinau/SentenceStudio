using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

public class VocabularyProgressRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<VocabularyProgressRepository> _logger;
    private readonly SentenceStudio.Abstractions.IPreferencesService? _preferences;

    public VocabularyProgressRepository(IServiceProvider serviceProvider, ILogger<VocabularyProgressRepository> logger, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
        _preferences = serviceProvider.GetService<SentenceStudio.Abstractions.IPreferencesService>();
    }

    private string ActiveUserId => _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;

    public async Task<List<VocabularyProgress>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        var query = db.VocabularyProgresses
            .Include(vp => vp.VocabularyWord)
            .Include(vp => vp.LearningContexts)
            .AsQueryable();
        if (!string.IsNullOrEmpty(userId))
            query = query.Where(vp => vp.UserId == userId);
        return await query.ToListAsync();
    }

    public async Task<VocabularyProgress?> GetByWordIdAndUserIdAsync(string vocabularyWordId, string userId)
    {
        if (string.IsNullOrEmpty(userId))
            userId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : string.Empty;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyProgresses
            .Include(vp => vp.VocabularyWord)
            .Include(vp => vp.LearningContexts)
                .ThenInclude(lc => lc.LearningResource)
            .FirstOrDefaultAsync(vp => vp.VocabularyWordId == vocabularyWordId && vp.UserId == userId);
    }

    public async Task<VocabularyProgress?> GetByWordIdAsync(string vocabularyWordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        var query = db.VocabularyProgresses
            .Include(vp => vp.VocabularyWord)
            .Include(vp => vp.LearningContexts)
                .ThenInclude(lc => lc.LearningResource)
            .Where(vp => vp.VocabularyWordId == vocabularyWordId);
        if (!string.IsNullOrEmpty(userId))
            query = query.Where(vp => vp.UserId == userId);
        return await query.FirstOrDefaultAsync();
    }

    /// <summary>
    /// Get progress for specific vocabulary word IDs with batching
    /// OPTIMIZATION: Batches queries to avoid SQLite's 999 parameter limit
    /// </summary>
    public async Task<List<VocabularyProgress>> GetByWordIdsAsync(List<string> vocabularyWordIds)
    {
        if (!vocabularyWordIds.Any())
            return new List<VocabularyProgress>();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // OPTIMIZATION: SQLite has a limit of 999 parameters per query
        // Batch queries in chunks of 500 to stay well below the limit
        const int BATCH_SIZE = 500;
        var results = new List<VocabularyProgress>();

        for (int i = 0; i < vocabularyWordIds.Count; i += BATCH_SIZE)
        {
            var batch = vocabularyWordIds.Skip(i).Take(BATCH_SIZE).ToList();
            var batchResults = await db.VocabularyProgresses
                .Include(vp => vp.VocabularyWord)
                .Include(vp => vp.LearningContexts)
                    .ThenInclude(lc => lc.LearningResource)
                .Where(vp => batch.Contains(vp.VocabularyWordId))
                .ToListAsync();

            results.AddRange(batchResults);
        }

        return results;
    }

    /// <summary>
    /// Get ALL progress records for a user efficiently
    /// OPTIMIZATION: Use this instead of GetByWordIdsAsync when loading all vocabulary
    /// Avoids massive WHERE IN clauses by loading everything in one query
    /// </summary>
    public async Task<List<VocabularyProgress>> GetAllForUserAsync(string userId = "")
    {
        if (string.IsNullOrEmpty(userId)) userId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : string.Empty;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.VocabularyProgresses
            .AsNoTracking()
            .Where(vp => vp.UserId == userId)
            .ToListAsync();
    }

    public async Task<VocabularyProgress> GetOrCreateAsync(string vocabularyWordId)
    {
        var existing = await GetByWordIdAsync(vocabularyWordId);
        if (existing != null)
        {
            return existing;
        }

        var newProgress = new VocabularyProgress
        {
            VocabularyWordId = vocabularyWordId,
            FirstSeenAt = DateTime.Now,
            LastPracticedAt = DateTime.Now,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        return await SaveAsync(newProgress);
    }

    public async Task<VocabularyProgress> SaveAsync(VocabularyProgress item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            item.UpdatedAt = DateTime.Now;

            // Auto-set UserId for new items if not already set
            if (string.IsNullOrEmpty(item.UserId) && !string.IsNullOrEmpty(ActiveUserId))
                item.UserId = ActiveUserId;

            var existsInDb = await db.VocabularyProgresses.AnyAsync(x => x.Id == item.Id);

            if (existsInDb)
            {
                // For updates, detach any tracked navigation properties to avoid conflicts
                if (item.VocabularyWord != null)
                {
                    db.Entry(item.VocabularyWord).State = EntityState.Detached;
                    item.VocabularyWord = null;
                }

                if (item.LearningContexts?.Any() == true)
                {
                    foreach (var context in item.LearningContexts)
                    {
                        db.Entry(context).State = EntityState.Detached;
                    }
                    item.LearningContexts.Clear();
                }

                db.VocabularyProgresses.Update(item);
            }
            else
            {
                db.VocabularyProgresses.Add(item);
            }

            await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return item;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SaveAsync");
            throw;
        }
    }

    public async Task<int> DeleteAsync(VocabularyProgress item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            db.VocabularyProgresses.Remove(item);
            int result = await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in DeleteAsync");
            return -1;
        }
    }

    // ===== PHASE 1 OPTIMIZATION: SQL-LEVEL AGGREGATION QUERIES =====

    /// <summary>
    /// Get vocabulary summary counts using efficient SQL aggregation
    /// </summary>
    public async Task<(int New, int Learning, int Familiar, int Review, int Known)> GetVocabSummaryCountsAsync(string userId = "")
    {
        if (string.IsNullOrEmpty(userId)) userId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : string.Empty;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Count vocabulary words belonging to this user's resources (via ResourceVocabularyMapping)
        var totalVocabWords = await db.Set<ResourceVocabularyMapping>()
            .Where(rvm => db.Set<LearningResource>()
                .Where(lr => lr.UserProfileId == userId)
                .Select(lr => lr.Id)
                .Contains(rvm.ResourceId))
            .Select(rvm => rvm.VocabularyWordId)
            .Distinct()
            .CountAsync();

        // PHASE 2: Load progress records for words that have been practiced
        var allProgress = await db.VocabularyProgresses
            .Where(vp => vp.UserId == userId)
            .Select(vp => new
            {
                vp.TotalAttempts,
                vp.MasteryScore,
                vp.NextReviewDate,
                vp.IsUserDeclared,
                vp.VerificationState,
                vp.IsKnown
            })
            .ToListAsync();

        var now = DateTime.Now;

        // Familiar = user-declared but not yet verified through practice
        var familiar = allProgress.Count(p => p.IsUserDeclared && p.VerificationState == VerificationStatus.Pending);

        // Words with progress records (excluding Familiar, which is its own category)
        var learning = allProgress.Count(p =>
            !(p.IsUserDeclared && p.VerificationState == VerificationStatus.Pending)
            && !p.IsKnown && p.TotalAttempts > 0
            && (p.NextReviewDate == null || p.NextReviewDate > now));
        var review = allProgress.Count(p =>
            !(p.IsUserDeclared && p.VerificationState == VerificationStatus.Pending)
            && !p.IsKnown && p.NextReviewDate != null && p.NextReviewDate <= now);
        var known = allProgress.Count(p => p.IsKnown);

        // "New" = Total words that have never been practiced (no progress record OR progress with 0 attempts)
        var wordsWithProgress = allProgress.Count;
        var newFromProgress = allProgress.Count(p => p.TotalAttempts == 0
            && !(p.IsUserDeclared && p.VerificationState == VerificationStatus.Pending));
        var wordsNeverSeen = totalVocabWords - wordsWithProgress;
        var newCount = wordsNeverSeen + newFromProgress;

        return (newCount, learning, familiar, review, known);
    }

    /// <summary>
    /// Get 7-day success rate using efficient SQL aggregation
    /// </summary>
    public async Task<double> GetSuccessRate7dAsync(string userId = "")
    {
        if (string.IsNullOrEmpty(userId)) userId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : string.Empty;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var sevenDaysAgo = DateTime.Now.AddDays(-7);
        var recent = await db.VocabularyProgresses
            .Where(vp => vp.UserId == userId && vp.LastPracticedAt >= sevenDaysAgo)
            .GroupBy(vp => 1)
            .Select(g => new
            {
                TotalCorrect = g.Sum(p => p.CorrectAttempts),
                TotalAttempts = g.Sum(p => p.TotalAttempts)
            })
            .FirstOrDefaultAsync();

        if (recent == null || recent.TotalAttempts == 0)
            return 0;

        return (double)recent.TotalCorrect / recent.TotalAttempts;
    }

    /// <summary>
    /// Get count of vocabulary words due for review based on SRS schedule.
    /// Excludes words that are already Known (MasteryScore >= 0.85 AND ProductionInStreak >= 2).
    /// </summary>
    public async Task<int> GetDueVocabCountAsync(DateTime asOfDate, string userId = "")
    {
        if (string.IsNullOrEmpty(userId)) userId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : string.Empty;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dueCount = await db.VocabularyProgresses
            .Where(vp => vp.UserId == userId
                && vp.NextReviewDate <= asOfDate
                && !(vp.MasteryScore >= 0.85f && vp.ProductionInStreak >= 2))
            .CountAsync();

        return dueCount;
    }

    /// <summary>
    /// Get vocabulary words due for review with word and resource details for planning.
    /// Excludes words that are already Known (MasteryScore >= 0.85 AND ProductionInStreak >= 2)
    /// so that the Today Plan focuses on words that still need active learning.
    /// </summary>
    public async Task<List<VocabularyProgress>> GetDueVocabularyAsync(DateTime asOfDate, string userId = "")
    {
        if (string.IsNullOrEmpty(userId)) userId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : string.Empty;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dueWords = await db.VocabularyProgresses
            .Include(vp => vp.VocabularyWord)
            .Include(vp => vp.LearningContexts)
                .ThenInclude(lc => lc.LearningResource)
            .Where(vp => vp.UserId == userId
                && vp.NextReviewDate <= asOfDate
                && !(vp.MasteryScore >= 0.85f && vp.ProductionInStreak >= 2))
            .ToListAsync();

        return dueWords;
    }

    /// <summary>
    /// Get aggregated progress for vocabulary words in a specific resource using SQL joins
    /// </summary>
    public async Task<ResourceProgressAggregation?> GetResourceProgressAggregationAsync(string resourceId, string userId = "")
    {
        if (string.IsNullOrEmpty(userId)) userId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : string.Empty;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var result = await db.ResourceVocabularyMappings
            .Where(rvm => rvm.ResourceId == resourceId)
            .Join(
                db.VocabularyProgresses.Where(vp => vp.UserId == userId),
                rvm => rvm.VocabularyWordId,
                vp => vp.VocabularyWordId,
                (rvm, vp) => vp
            )
            .GroupBy(vp => 1)
            .Select(g => new ResourceProgressAggregation
            {
                AverageMasteryScore = g.Average(p => p.MasteryScore),
                TotalAttempts = g.Sum(p => p.TotalAttempts),
                TotalCorrectAttempts = g.Sum(p => p.CorrectAttempts),
                KnownCount = g.Count(p => p.IsKnown)
            })
            .FirstOrDefaultAsync();

        return result;
    }

    /// <summary>
    /// Get aggregated progress for multiple resources in one query
    /// </summary>
    public async Task<Dictionary<string, ResourceProgressAggregation>> GetMultipleResourceProgressAggregationsAsync(List<string> resourceIds, string userId = "")
    {
        if (string.IsNullOrEmpty(userId)) userId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : string.Empty;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = await db.ResourceVocabularyMappings
            .Where(rvm => resourceIds.Contains(rvm.ResourceId))
            .Join(
                db.VocabularyProgresses.Where(vp => vp.UserId == userId),
                rvm => rvm.VocabularyWordId,
                vp => vp.VocabularyWordId,
                (rvm, vp) => new { rvm.ResourceId, Progress = vp }
            )
            .GroupBy(x => x.ResourceId)
            .Select(g => new
            {
                ResourceId = g.Key,
                Aggregation = new ResourceProgressAggregation
                {
                    AverageMasteryScore = g.Average(x => x.Progress.MasteryScore),
                    TotalAttempts = g.Sum(x => x.Progress.TotalAttempts),
                    TotalCorrectAttempts = g.Sum(x => x.Progress.CorrectAttempts),
                    KnownCount = g.Count(x => x.Progress.MasteryScore >= 0.8f)
                }
            })
            .ToDictionaryAsync(x => x.ResourceId, x => x.Aggregation);

        return results;
    }

    /// <summary>
    /// Get overall vocabulary progress aggregation (for skill progress calculation)
    /// </summary>
    public async Task<ResourceProgressAggregation?> GetOverallProgressAggregationAsync(string userId = "")
    {
        if (string.IsNullOrEmpty(userId)) userId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : string.Empty;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var result = await db.VocabularyProgresses
            .Where(vp => vp.UserId == userId)
            .GroupBy(vp => 1)
            .Select(g => new ResourceProgressAggregation
            {
                AverageMasteryScore = g.Average(p => p.MasteryScore),
                TotalAttempts = g.Sum(p => p.TotalAttempts),
                TotalCorrectAttempts = g.Sum(p => p.CorrectAttempts),
                KnownCount = g.Count(p => p.MasteryScore >= 0.8f)
            })
            .FirstOrDefaultAsync();

        return result;
    }
}

/// <summary>
/// DTO for aggregated resource progress data
/// </summary>
public class ResourceProgressAggregation
{
    public double AverageMasteryScore { get; set; }
    public int TotalAttempts { get; set; }
    public int TotalCorrectAttempts { get; set; }
    public int KnownCount { get; set; }

    public double CorrectRate => TotalAttempts > 0 ? (double)TotalCorrectAttempts / TotalAttempts : 0;
}
