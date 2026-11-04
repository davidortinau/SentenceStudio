using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

public class VocabularyLearningContextRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<VocabularyLearningContextRepository> _logger;

    public VocabularyLearningContextRepository(IServiceProvider serviceProvider, ILogger<VocabularyLearningContextRepository> logger, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
    }

    public async Task<List<VocabularyLearningContext>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyLearningContexts
            .Include(vlc => vlc.VocabularyProgress)
                .ThenInclude(vp => vp.VocabularyWord)
            .Include(vlc => vlc.LearningResource)
            .ToListAsync();
    }

    public async Task<List<VocabularyLearningContext>> GetByProgressIdAsync(string vocabularyProgressId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyLearningContexts
            .Include(vlc => vlc.LearningResource)
            .Where(vlc => vlc.VocabularyProgressId == vocabularyProgressId)
            .OrderByDescending(vlc => vlc.LearnedAt)
            .ToListAsync();
    }

    public async Task<List<VocabularyLearningContext>> GetRecentAttemptsAsync(string vocabularyProgressId, int count)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyLearningContexts
            .Where(vlc => vlc.VocabularyProgressId == vocabularyProgressId)
            .OrderByDescending(vlc => vlc.LearnedAt)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// True when an answer on one of <paramref name="wordIds"/> was scored for
    /// <paramref name="userId"/> in <paramref name="activity"/> at or after
    /// <paramref name="sinceUtc"/>. False, without a query, when there is no learner or no word.
    /// A client completing a quiz run from a round summary it holds no proof for checks this
    /// first: the scoring client's learning records sync with the run (rule 6 of
    /// docs/activity-session-format.md; native NativeQuizAttemptCommitterAdapter).
    /// </summary>
    public async Task<bool> HasScoredAnswerSinceAsync(
        string userId, string activity, IEnumerable<string> wordIds, DateTime sinceUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("HasScoredAnswerSinceAsync called without a user; reporting no scored answer.");
            return false;
        }

        var words = wordIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (words.Length == 0)
            return false;

        var since = sinceUtc.Kind switch
        {
            DateTimeKind.Local => sinceUtc.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc),
            _ => sinceUtc
        };

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyLearningContexts.AsNoTracking()
            .Where(context => context.Activity == activity && context.LearnedAt >= since)
            .Join(db.VocabularyProgresses.AsNoTracking()
                    .Where(progress => progress.UserId == userId && words.Contains(progress.VocabularyWordId)),
                context => context.VocabularyProgressId, progress => progress.Id,
                (context, _) => context.Id)
            .AnyAsync(cancellationToken);
    }

    public async Task<List<VocabularyLearningContext>> GetByActivityAsync(string activity)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyLearningContexts
            .Include(vlc => vlc.VocabularyProgress)
                .ThenInclude(vp => vp.VocabularyWord)
            .Include(vlc => vlc.LearningResource)
            .Where(vlc => vlc.Activity == activity)
            .OrderByDescending(vlc => vlc.LearnedAt)
            .ToListAsync();
    }

    public async Task<VocabularyLearningContext> SaveAsync(VocabularyLearningContext item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            item.UpdatedAt = DateTime.UtcNow;
            
            var existsInDb = await db.VocabularyLearningContexts.AnyAsync(x => x.Id == item.Id);

            if (existsInDb)
            {
                // For updates, detach any tracked navigation properties to avoid conflicts
                if (item.VocabularyProgress != null)
                {
                    db.Entry(item.VocabularyProgress).State = EntityState.Detached;
                    item.VocabularyProgress = null;
                }
                
                if (item.LearningResource != null)
                {
                    db.Entry(item.LearningResource).State = EntityState.Detached;
                    item.LearningResource = null;
                }
                
                db.VocabularyLearningContexts.Update(item);
            }
            else
            {
                item.CreatedAt = DateTime.UtcNow;
                db.VocabularyLearningContexts.Add(item);
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

    public async Task<int> DeleteAsync(VocabularyLearningContext item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            db.VocabularyLearningContexts.Remove(item);
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
}
