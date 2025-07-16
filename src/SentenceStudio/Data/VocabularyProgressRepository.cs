using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace SentenceStudio.Data;

public class VocabularyProgressRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;

    public VocabularyProgressRepository(IServiceProvider serviceProvider, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
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
            Debug.WriteLine($"An error occurred SaveAsync: {ex.Message}");
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
            Debug.WriteLine($"An error occurred DeleteAsync: {ex.Message}");
            return -1;
        }
    }
}
