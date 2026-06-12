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
            item.UpdatedAt = DateTime.Now;
            
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
                item.CreatedAt = DateTime.Now;
                db.VocabularyLearningContexts.Add(item);
            }
            
            await db.SaveChangesAsync();
            
            TriggerSync();
            
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
            
            TriggerSync();
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in DeleteAsync");
            return -1;
        }
    }
    private void TriggerSync()
    {
        if (_syncService is null) return;
        _ = Task.Run(async () =>
        {
            try { await _syncService.TriggerSyncAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Background sync trigger failed"); }
        });
    }

}
