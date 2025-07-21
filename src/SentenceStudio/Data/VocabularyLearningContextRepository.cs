using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace SentenceStudio.Data;

public class VocabularyLearningContextRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;

    public VocabularyLearningContextRepository(IServiceProvider serviceProvider, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
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

    public async Task<List<VocabularyLearningContext>> GetByProgressIdAsync(int vocabularyProgressId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyLearningContexts
            .Include(vlc => vlc.LearningResource)
            .Where(vlc => vlc.VocabularyProgressId == vocabularyProgressId)
            .OrderByDescending(vlc => vlc.LearnedAt)
            .ToListAsync();
    }

    public async Task<List<VocabularyLearningContext>> GetRecentAttemptsAsync(int vocabularyProgressId, int count)
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
            
            if (item.Id != 0)
            {
                // For updates, detach any tracked navigation properties to avoid conflicts
                if (item.VocabularyProgress != null)
                {
                    db.Entry(item.VocabularyProgress).State = EntityState.Detached;
                    item.VocabularyProgress = null; // Clear navigation property to avoid tracking
                }
                
                if (item.LearningResource != null)
                {
                    db.Entry(item.LearningResource).State = EntityState.Detached;
                    item.LearningResource = null; // Clear navigation property to avoid tracking
                }
                
                db.VocabularyLearningContexts.Update(item);
            }
            else
            {
                item.CreatedAt = DateTime.Now;
                db.VocabularyLearningContexts.Add(item);
            }
            
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return item;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred SaveAsync: {ex.Message}");
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
            Debug.WriteLine($"An error occurred DeleteAsync: {ex.Message}");
            return -1;
        }
    }
}
