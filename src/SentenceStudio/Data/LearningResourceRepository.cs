using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

public class LearningResourceRepository
{
    private readonly IServiceProvider _serviceProvider;
    private ISyncService _syncService;
    private AiService _aiService;

    public LearningResourceRepository(IServiceProvider serviceProvider = null)
    {
        _serviceProvider = serviceProvider;
        if (serviceProvider != null)
        {
            _syncService = serviceProvider.GetService<ISyncService>();
            _aiService = serviceProvider.GetService<AiService>();
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

    public async Task<List<VocabularyWord>> GetAllVocabularyWordsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyWords.ToListAsync();
    }

    public async Task<int> SaveWordAsync(VocabularyWord word)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            if (word.Id != 0)
            {
                // For updates, detach any tracked navigation properties to avoid conflicts
                if (word.LearningResources?.Any() == true)
                {
                    foreach (var resource in word.LearningResources)
                    {
                        db.Entry(resource).State = EntityState.Detached;
                    }
                    word.LearningResources.Clear();
                }
                
                if (word.ResourceMappings?.Any() == true)
                {
                    foreach (var mapping in word.ResourceMappings)
                    {
                        db.Entry(mapping).State = EntityState.Detached;
                    }
                    word.ResourceMappings.Clear();
                }
                
                db.VocabularyWords.Update(word);
            }
            else
            {
                db.VocabularyWords.Add(word);
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

    public async Task<List<LearningResource>> GetAllResourcesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.LearningResources
            .Include(r => r.Vocabulary) // Include vocabulary words like GetResourceAsync does
            .ToListAsync();
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

    public async Task GetStarterVocabulary(string nativeLanguage, string targetLanguage)
    {       
        var prompt = string.Empty;     
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetStarterVocabulary.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(await reader.ReadToEndAsync());
            prompt = await template.RenderAsync(new { native_language = nativeLanguage, target_language = targetLanguage});
        }
        
        try
        {
            var response = await _aiService.SendPrompt<string>(prompt);

            // Create a LearningResource instead of VocabularyList
            var resource = new LearningResource
            {
                Title = "Sentence Studio Starter Vocabulary",
                Description = $"Starter vocabulary for learning {targetLanguage}",
                MediaType = "Vocabulary List",
                Language = targetLanguage,
                Tags = "starter,vocabulary",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Parse vocabulary words and add them to the resource
            var vocabularyWords = VocabularyWord.ParseVocabularyWords(response);
            
            // Save the resource first
            await SaveResourceAsync(resource);
            
            // Now save the vocabulary words and associate them with the resource
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            foreach (var word in vocabularyWords)
            {
                if (word.CreatedAt == default)
                    word.CreatedAt = DateTime.UtcNow;
                word.UpdatedAt = DateTime.UtcNow;
                
                // Add the word to the database
                db.VocabularyWords.Add(word);
                await db.SaveChangesAsync();
                
                // Associate the word with the resource
                await AddVocabularyToResourceAsync(resource.Id, word.Id);
            }
            
            await AppShell.DisplayToastAsync("Starter vocabulary resource created");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred GetStarterVocabulary: {ex.Message}");
        }
    }

    // --- Enhanced Vocabulary Management Methods ---
    
    /// <summary>
    /// Get all vocabulary words with their associated learning resources
    /// </summary>
    public async Task<List<VocabularyWord>> GetAllVocabularyWordsWithResourcesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.VocabularyWords
            .Include(vw => vw.LearningResources)
            .ToListAsync();
    }

    /// <summary>
    /// Search vocabulary words by target or native language terms
    /// </summary>
    public async Task<List<VocabularyWord>> SearchVocabularyWordsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetAllVocabularyWordsWithResourcesAsync();
            
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var searchTerm = query.ToLower().Trim();
        
        return await db.VocabularyWords
            .Include(vw => vw.LearningResources)
            .Where(vw => 
                (vw.TargetLanguageTerm != null && vw.TargetLanguageTerm.ToLower().Contains(searchTerm)) ||
                (vw.NativeLanguageTerm != null && vw.NativeLanguageTerm.ToLower().Contains(searchTerm)))
            .ToListAsync();
    }

    /// <summary>
    /// Get vocabulary words that are not associated with any learning resources (orphaned)
    /// </summary>
    public async Task<List<VocabularyWord>> GetOrphanedVocabularyWordsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.VocabularyWords
            .Where(vw => !db.ResourceVocabularyMappings.Any(rvm => rvm.VocabularyWordId == vw.Id))
            .ToListAsync();
    }

    /// <summary>
    /// Get vocabulary words that are associated with learning resources
    /// </summary>
    public async Task<List<VocabularyWord>> GetAssociatedVocabularyWordsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.VocabularyWords
            .Include(vw => vw.LearningResources)
            .Where(vw => db.ResourceVocabularyMappings.Any(rvm => rvm.VocabularyWordId == vw.Id))
            .ToListAsync();
    }

    /// <summary>
    /// Get a specific vocabulary word with all its associated resources
    /// </summary>
    public async Task<VocabularyWord?> GetVocabularyWordWithResourcesAsync(int wordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.VocabularyWords
            .Include(vw => vw.LearningResources)
            .FirstOrDefaultAsync(vw => vw.Id == wordId);
    }

    /// <summary>
    /// Get a specific vocabulary word by ID (without resources)
    /// </summary>
    public async Task<VocabularyWord?> GetVocabularyWordByIdAsync(int wordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.VocabularyWords
            .FirstOrDefaultAsync(vw => vw.Id == wordId);
    }

    /// <summary>
    /// Get all learning resources associated with a specific vocabulary word
    /// </summary>
    public async Task<List<LearningResource>> GetResourcesForVocabularyWordAsync(int wordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.VocabularyWords
            .Where(vw => vw.Id == wordId)
            .SelectMany(vw => vw.LearningResources)
            .ToListAsync();
    }

    /// <summary>
    /// Get vocabulary statistics
    /// </summary>
    public async Task<(int TotalWords, int AssociatedWords, int OrphanedWords)> GetVocabularyStatsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var totalWords = await db.VocabularyWords.CountAsync();
        var associatedWords = await db.VocabularyWords
            .Where(vw => db.ResourceVocabularyMappings.Any(rvm => rvm.VocabularyWordId == vw.Id))
            .CountAsync();
        var orphanedWords = totalWords - associatedWords;
        
        return (totalWords, associatedWords, orphanedWords);
    }

    /// <summary>
    /// Safely delete a vocabulary word and all its associations
    /// </summary>
    public async Task<bool> DeleteVocabularyWordAsync(int wordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            // First remove all resource associations
            var mappings = await db.ResourceVocabularyMappings
                .Where(rvm => rvm.VocabularyWordId == wordId)
                .ToListAsync();
                
            db.ResourceVocabularyMappings.RemoveRange(mappings);
            
            // Then remove the vocabulary word itself
            var word = await db.VocabularyWords.FindAsync(wordId);
            if (word != null)
            {
                db.VocabularyWords.Remove(word);
            }
            
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return true;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return false;
        }
    }

    /// <summary>
    /// Update an existing vocabulary word
    /// </summary>
    public async Task<bool> UpdateVocabularyWordAsync(VocabularyWord word)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            word.UpdatedAt = DateTime.UtcNow;
            db.VocabularyWords.Update(word);
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return true;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return false;
        }
    }

    /// <summary>
    /// Bulk associate multiple vocabulary words with a learning resource
    /// </summary>
    public async Task<bool> BulkAssociateWordsWithResourceAsync(int resourceId, List<int> vocabularyWordIds)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var resource = await db.LearningResources
                .Include(r => r.Vocabulary)
                .FirstOrDefaultAsync(r => r.Id == resourceId);
                
            if (resource == null) return false;
            
            var vocabularyWords = await db.VocabularyWords
                .Where(vw => vocabularyWordIds.Contains(vw.Id))
                .ToListAsync();
            
            foreach (var word in vocabularyWords)
            {
                if (!resource.Vocabulary.Contains(word))
                {
                    resource.Vocabulary.Add(word);
                }
            }
            
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return true;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return false;
        }
    }

    /// <summary>
    /// Bulk remove vocabulary words from a learning resource
    /// </summary>
    public async Task<bool> BulkRemoveWordsFromResourceAsync(int resourceId, List<int> vocabularyWordIds)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var mappingsToRemove = await db.ResourceVocabularyMappings
                .Where(rvm => rvm.ResourceId == resourceId && vocabularyWordIds.Contains(rvm.VocabularyWordId))
                .ToListAsync();
                
            db.ResourceVocabularyMappings.RemoveRange(mappingsToRemove);
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return true;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return false;
        }
    }

    /// <summary>
    /// Bulk delete multiple vocabulary words
    /// </summary>
    public async Task<bool> BulkDeleteVocabularyWordsAsync(List<int> vocabularyWordIds)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            // Remove all resource associations first
            var mappings = await db.ResourceVocabularyMappings
                .Where(rvm => vocabularyWordIds.Contains(rvm.VocabularyWordId))
                .ToListAsync();
                
            db.ResourceVocabularyMappings.RemoveRange(mappings);
            
            // Then remove the vocabulary words
            var words = await db.VocabularyWords
                .Where(vw => vocabularyWordIds.Contains(vw.Id))
                .ToListAsync();
                
            db.VocabularyWords.RemoveRange(words);
            
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return true;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return false;
        }
    }

    /// <summary>
    /// Get vocabulary words associated with a specific learning resource
    /// </summary>
    public async Task<List<VocabularyWord>> GetVocabularyWordsByResourceAsync(int resourceId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.VocabularyWords
            .Where(vw => db.ResourceVocabularyMappings
                .Any(rvm => rvm.ResourceId == resourceId && rvm.VocabularyWordId == vw.Id))
            .ToListAsync();
    }

    /// <summary>
    /// Check if a vocabulary word exists with the same terms
    /// </summary>
    public async Task<VocabularyWord?> FindDuplicateVocabularyWordAsync(string targetTerm, string nativeTerm)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.VocabularyWords
            .FirstOrDefaultAsync(vw => 
                vw.TargetLanguageTerm != null && vw.TargetLanguageTerm.Trim().ToLower() == targetTerm.Trim().ToLower() &&
                vw.NativeLanguageTerm != null && vw.NativeLanguageTerm.Trim().ToLower() == nativeTerm.Trim().ToLower());
    }

    /// <summary>
    /// Get learning resources that contain a specific vocabulary word
    /// </summary>
    public async Task<List<LearningResource>> GetResourcesContainingWordAsync(int vocabularyWordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        return await db.LearningResources
            .Where(lr => db.ResourceVocabularyMappings
                .Any(rvm => rvm.VocabularyWordId == vocabularyWordId && rvm.ResourceId == lr.Id))
            .ToListAsync();
    }
}