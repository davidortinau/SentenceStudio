using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

public class LearningResourceRepository
{
    private readonly IServiceProvider _serviceProvider;
    private VocabularyService _vocabularyService;
    private ISyncService _syncService;

    public LearningResourceRepository(IServiceProvider serviceProvider = null)
    {
        _serviceProvider = serviceProvider;
        if (serviceProvider != null)
        {
            _vocabularyService = serviceProvider.GetService<VocabularyService>();
            _syncService = serviceProvider.GetService<ISyncService>();
        }
    }

    // --- Added for VocabularyService replacement ---
    public async Task<VocabularyWord> GetWordByNativeTermAsync(string nativeTerm)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyWords
            .Where(w => w.NativeLanguageTerm == nativeTerm)
            .FirstOrDefaultAsync();
    }

    public async Task<VocabularyWord> GetWordByTargetTermAsync(string targetTerm)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyWords
            .Where(w => w.TargetLanguageTerm == targetTerm)
            .FirstOrDefaultAsync();
    }

    public async Task<int> SaveWordAsync(VocabularyWord word)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            if (word.Id != 0)
            {
                db.VocabularyWords.Update(word);
            }
            else
            {
                db.VocabularyWords.Add(word);
            }
            
            int result = await db.SaveChangesAsync();
            return result;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return -1;
        }
    }

    public async Task<List<LearningResource>> GetAllResourcesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.LearningResources.ToListAsync();
    }

    public async Task<LearningResource> GetResourceAsync(int resourceId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var resource = await db.LearningResources
            .Include(r => r.Vocabulary) // This uses the skip navigation to load vocabulary
            .Where(r => r.Id == resourceId)
            .FirstOrDefaultAsync();
        
        return resource;
    }

    public async Task<int> SaveResourceAsync(LearningResource resource)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Set timestamps
        if (resource.CreatedAt == default)
            resource.CreatedAt = DateTime.UtcNow;
            
        resource.UpdatedAt = DateTime.UtcNow;
        
        try
        {
            if (resource.Id != 0)
            {
                db.LearningResources.Update(resource);
            }
            else
            {
                db.LearningResources.Add(resource);
            }

            // EF Core will handle the many-to-many relationship automatically
            // when we add/update vocabulary words to the resource.Vocabulary collection
            int result = await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return result;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return -1;
        }
    }

    public async Task<int> DeleteResourceAsync(LearningResource resource)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            db.LearningResources.Remove(resource);
            int result = await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return result;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return -1;
        }
    }
    
    public async Task<List<LearningResource>> SearchResourcesAsync(string query)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.LearningResources
            .Where(r => r.Title.Contains(query) || r.Description.Contains(query) || 
                   r.Tags.Contains(query) || r.Language.Contains(query))
            .ToListAsync();
    }
    
    // Get resources of a specific type
    public async Task<List<LearningResource>> GetResourcesByTypeAsync(string mediaType)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.LearningResources
            .Where(r => r.MediaType == mediaType)
            .ToListAsync();
    }
    
    // Get all vocabulary lists
    public Task<List<LearningResource>> GetVocabularyListsAsync()
    {
        return GetResourcesByTypeAsync("Vocabulary List");
    }
    
    // Get resources by language
    public async Task<List<LearningResource>> GetResourcesByLanguageAsync(string language)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.LearningResources
            .Where(r => r.Language == language)
            .ToListAsync();
    }

    // Add vocabulary word to a learning resource
    public async Task<bool> AddVocabularyToResourceAsync(int resourceId, int vocabularyWordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var resource = await db.LearningResources
                .Include(r => r.Vocabulary)
                .FirstOrDefaultAsync(r => r.Id == resourceId);
                
            var vocabularyWord = await db.VocabularyWords
                .FirstOrDefaultAsync(v => v.Id == vocabularyWordId);
                
            if (resource != null && vocabularyWord != null && !resource.Vocabulary.Contains(vocabularyWord))
            {
                resource.Vocabulary.Add(vocabularyWord);
                await db.SaveChangesAsync();
                
                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
                
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return false;
        }
    }

    // Remove vocabulary word from a learning resource
    public async Task<bool> RemoveVocabularyFromResourceAsync(int resourceId, int vocabularyWordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var resource = await db.LearningResources
                .Include(r => r.Vocabulary)
                .FirstOrDefaultAsync(r => r.Id == resourceId);
                
            if (resource != null)
            {
                var vocabularyToRemove = resource.Vocabulary.FirstOrDefault(v => v.Id == vocabularyWordId);
                if (vocabularyToRemove != null)
                {
                    resource.Vocabulary.Remove(vocabularyToRemove);
                    await db.SaveChangesAsync();
                    
                    _syncService?.TriggerSyncAsync().ConfigureAwait(false);
                    
                    return true;
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return false;
        }
    }
}