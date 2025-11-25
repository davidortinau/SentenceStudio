using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

public class VocabularyProgressRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<VocabularyProgressRepository> _logger;

    public VocabularyProgressRepository(IServiceProvider serviceProvider, ILogger<VocabularyProgressRepository> logger, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
    }

    public async Task<List<VocabularyProgress>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyProgresses
            .Include(vp => vp.VocabularyWord)
            .Include(vp => vp.LearningContexts)
            .ToListAsync();
    }

    public async Task<VocabularyProgress?> GetByWordIdAndUserIdAsync(int vocabularyWordId, int userId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyProgresses
            .Include(vp => vp.VocabularyWord)
            .Include(vp => vp.LearningContexts)
                .ThenInclude(lc => lc.LearningResource)
            .FirstOrDefaultAsync(vp => vp.VocabularyWordId == vocabularyWordId && vp.UserId == userId);
    }

    public async Task<VocabularyProgress?> GetByWordIdAsync(int vocabularyWordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyProgresses
            .Include(vp => vp.VocabularyWord)
            .Include(vp => vp.LearningContexts)
                .ThenInclude(lc => lc.LearningResource)
            .FirstOrDefaultAsync(vp => vp.VocabularyWordId == vocabularyWordId);
    }

    public async Task<List<VocabularyProgress>> GetByWordIdsAsync(List<int> vocabularyWordIds)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyProgresses
            .Include(vp => vp.VocabularyWord)
            .Include(vp => vp.LearningContexts)
                .ThenInclude(lc => lc.LearningResource)
            .Where(vp => vocabularyWordIds.Contains(vp.VocabularyWordId))
            .ToListAsync();
    }

    public async Task<VocabularyProgress> GetOrCreateAsync(int vocabularyWordId)
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

            if (item.Id != 0)
            {
                // For updates, detach any tracked navigation properties to avoid conflicts
                if (item.VocabularyWord != null)
                {
                    db.Entry(item.VocabularyWord).State = EntityState.Detached;
                    item.VocabularyWord = null; // Clear navigation property to avoid tracking
                }

                // Detach any learning contexts to avoid conflicts
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
            if (item.Id == 0)
            {
                await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
            }
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
    public async Task<(int New, int Learning, int Review, int Known)> GetVocabSummaryCountsAsync(int userId = 1)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // PHASE 1: Get total vocabulary word count (all words in database)
        var totalVocabWords = await db.VocabularyWords.CountAsync();

        // PHASE 2: Load progress records for words that have been practiced
        var allProgress = await db.VocabularyProgresses
            .Where(vp => vp.UserId == userId)
            .Select(vp => new
            {
                vp.TotalAttempts,
                vp.MasteryScore,
                vp.NextReviewDate
            })
            .ToListAsync();

        var now = DateTime.Now;

        // Words with progress records
        var learning = allProgress.Count(p => p.MasteryScore < 0.8f && p.TotalAttempts > 0 && (p.NextReviewDate == null || p.NextReviewDate > now));
        var review = allProgress.Count(p => p.MasteryScore < 0.8f && p.NextReviewDate != null && p.NextReviewDate <= now);
        var known = allProgress.Count(p => p.MasteryScore >= 0.8f);

        // "New" = Total words that have never been practiced (no progress record OR progress with 0 attempts)
        var wordsWithProgress = allProgress.Count;
        var newFromProgress = allProgress.Count(p => p.TotalAttempts == 0);
        var wordsNeverSeen = totalVocabWords - wordsWithProgress;
        var newCount = wordsNeverSeen + newFromProgress;

        return (newCount, learning, review, known);
    }

    /// <summary>
    /// Get 7-day success rate using efficient SQL aggregation
    /// </summary>
    public async Task<double> GetSuccessRate7dAsync(int userId = 1)
    {
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
    /// Get count of vocabulary words due for review based on SRS schedule
    /// </summary>
    public async Task<int> GetDueVocabCountAsync(DateTime asOfDate, int userId = 1)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dueCount = await db.VocabularyProgresses
            .Where(vp => vp.UserId == userId && vp.NextReviewDate <= asOfDate)
            .CountAsync();

        return dueCount;
    }

    /// <summary>
    /// Get vocabulary words due for review with word and resource details for planning
    /// </summary>
    public async Task<List<VocabularyProgress>> GetDueVocabularyAsync(DateTime asOfDate, int userId = 1)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dueWords = await db.VocabularyProgresses
            .Include(vp => vp.VocabularyWord)
            .Include(vp => vp.LearningContexts)
                .ThenInclude(lc => lc.LearningResource)
            .Where(vp => vp.UserId == userId && vp.NextReviewDate <= asOfDate)
            .ToListAsync();

        return dueWords;
    }

    /// <summary>
    /// Get aggregated progress for vocabulary words in a specific resource using SQL joins
    /// </summary>
    public async Task<ResourceProgressAggregation?> GetResourceProgressAggregationAsync(int resourceId, int userId = 1)
    {
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
    public async Task<Dictionary<int, ResourceProgressAggregation>> GetMultipleResourceProgressAggregationsAsync(List<int> resourceIds, int userId = 1)
    {
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
    public async Task<ResourceProgressAggregation?> GetOverallProgressAggregationAsync(int userId = 1)
    {
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
