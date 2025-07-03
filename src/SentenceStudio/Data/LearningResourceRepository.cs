using System.Diagnostics;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using SentenceStudio.Services;
using SentenceStudio.Data;
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
            .Where(r => r.Id == resourceId)
            .FirstOrDefaultAsync();
        
        // Load associated vocabulary - since we removed many-to-many mapping,
        // we'll need to implement this differently or remove it for now
        if (resource != null)
        {
            // For now, we'll just return the resource without vocabulary
            // This can be re-implemented when the vocabulary relationship is properly defined
        }
        
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

            // Save the vocabulary words first to ensure they have IDs
            if (resource.Vocabulary != null && resource.Vocabulary.Count > 0)
            {
                foreach (var word in resource.Vocabulary)
                {
                    if (word.Id == 0)
                    {
                        await SaveWordAsync(word);
                    }
                }
            }

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
}