using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Data;

public class LearningResourceRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LearningResourceRepository> _logger;
    private ISyncService _syncService;
    private AiService _aiService;
    private readonly IFileSystemService _fileSystem;
    private readonly SentenceStudio.Abstractions.IPreferencesService? _preferences;

    public LearningResourceRepository(
        IServiceProvider serviceProvider,
        ILogger<LearningResourceRepository> logger,
        IFileSystemService fileSystem)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _fileSystem = fileSystem;
        if (serviceProvider != null)
        {
            _syncService = serviceProvider.GetService<ISyncService>();
            _aiService = serviceProvider.GetService<AiService>();
            _preferences = serviceProvider.GetService<SentenceStudio.Abstractions.IPreferencesService>();
        }
    }

    private int ActiveUserId => _preferences?.Get("active_profile_id", 0) ?? 0;

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
        var userId = ActiveUserId;
        if (userId > 0)
        {
            var userWordIds = db.ResourceVocabularyMappings
                .Join(db.LearningResources.Where(r => r.UserProfileId == userId),
                    m => m.ResourceId, r => r.Id, (m, r) => m.VocabularyWordId);
            return await db.VocabularyWords.Where(w => userWordIds.Contains(w.Id)).ToListAsync();
        }
        return await db.VocabularyWords.ToListAsync();
    }

    /// <summary>
    /// Get just the target language terms for a specific language (optimized for AI prompts)
    /// </summary>
    public async Task<List<string>> GetVocabularyTargetTermsByLanguageAsync(string language, int? limit = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var query = db.VocabularyWords
            .Join(
                db.ResourceVocabularyMappings,
                word => word.Id,
                mapping => mapping.VocabularyWordId,
                (word, mapping) => new { Word = word, Mapping = mapping }
            )
            .Join(
                db.LearningResources,
                wm => wm.Mapping.ResourceId,
                resource => resource.Id,
                (wm, resource) => new { wm.Word, Resource = resource }
            )
            .Where(wr => wr.Resource.Language == language);

        var userId = ActiveUserId;
        if (userId > 0)
            query = query.Where(wr => wr.Resource.UserProfileId == userId);

        var termsQuery = query
            .Select(wr => wr.Word.TargetLanguageTerm)
            .Distinct();

        if (limit.HasValue)
        {
            termsQuery = termsQuery.Take(limit.Value);
        }

        return await termsQuery.ToListAsync();
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
            // UXDivers popup removed - error already logged above
            return -1;
        }
    }

    public async Task<List<LearningResource>> GetAllResourcesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var query = db.LearningResources
            .Include(r => r.Vocabulary);
        var userId = ActiveUserId;
        if (userId > 0)
            return await query.Where(r => r.UserProfileId == userId).ToListAsync();
        return await query.ToListAsync();
    }

    /// <summary>
    /// Lightweight query: resources without vocabulary navigation properties.
    /// Supports optional type/language filters pushed to SQL.
    /// </summary>
    public async Task<List<LearningResource>> GetAllResourcesLightweightAsync(
        string filterType = null, List<string> filterLanguages = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IQueryable<LearningResource> query = db.LearningResources.AsNoTracking();

        var userId = ActiveUserId;
        if (userId > 0)
            query = query.Where(r => r.UserProfileId == userId);

        if (!string.IsNullOrEmpty(filterType) && filterType != "All")
            query = query.Where(r => r.MediaType == filterType);

        if (filterLanguages != null && filterLanguages.Count > 0)
            query = query.Where(r => filterLanguages.Contains(r.Language));

        return await query
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.CreatedAt)
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
            // Capture vocabulary words before they get detached
            var vocabularyWords = resource.Vocabulary?.ToList() ?? new List<VocabularyWord>();
            var vocabularyWordIds = vocabularyWords.Select(v => v.Id).ToList();

            // Set user ownership for new resources
            if (resource.Id == 0)
                resource.UserProfileId ??= ActiveUserId > 0 ? ActiveUserId : null;

            if (resource.Id != 0)
            {
                var existingResource = await db.LearningResources
                    .Include(r => r.Vocabulary)
                    .FirstOrDefaultAsync(r => r.Id == resource.Id);

                if (existingResource != null)
                {
                    // Update resource properties
                    db.Entry(existingResource).CurrentValues.SetValues(resource);

                    // Handle vocabulary associations
                    // Get the actual vocabulary words from the database in this context
                    var dbVocabularyWords = await db.VocabularyWords
                        .Where(v => vocabularyWordIds.Contains(v.Id))
                        .ToListAsync();

                    // Clear existing and add new associations
                    existingResource.Vocabulary.Clear();
                    foreach (var word in dbVocabularyWords)
                    {
                        existingResource.Vocabulary.Add(word);
                    }
                }
            }
            else
            {
                // For new resources
                db.LearningResources.Add(resource);
                await db.SaveChangesAsync(); // Save to get the resource ID

                // Now associate vocabulary words
                if (vocabularyWordIds.Any())
                {
                    var dbVocabularyWords = await db.VocabularyWords
                        .Where(v => vocabularyWordIds.Contains(v.Id))
                        .ToListAsync();

                    resource.Vocabulary.Clear();
                    foreach (var word in dbVocabularyWords)
                    {
                        resource.Vocabulary.Add(word);
                    }
                }
            }

            await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return resource.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ SaveResourceAsync error");
            // UXDivers popup removed - error already logged above
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
            // UXDivers popup removed - error already logged above
            return -1;
        }
    }

    public async Task<List<LearningResource>> SearchResourcesAsync(string query)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = db.LearningResources
            .AsNoTracking()
            .Where(r => r.Title.Contains(query) || r.Description.Contains(query) ||
                   r.Tags.Contains(query) || r.Language.Contains(query));
        var userId = ActiveUserId;
        if (userId > 0)
            results = results.Where(r => r.UserProfileId == userId);
        return await results.ToListAsync();
    }

    // Get resources of a specific type
    public async Task<List<LearningResource>> GetResourcesByTypeAsync(string mediaType)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = db.LearningResources
            .Where(r => r.MediaType == mediaType);
        var userId = ActiveUserId;
        if (userId > 0)
            results = results.Where(r => r.UserProfileId == userId);
        return await results.ToListAsync();
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

        var results = db.LearningResources
            .Where(r => r.Language == language);
        var userId = ActiveUserId;
        if (userId > 0)
            results = results.Where(r => r.UserProfileId == userId);
        return await results.ToListAsync();
    }

    // Get all smart resources
    public async Task<List<LearningResource>> GetSmartResourcesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = db.LearningResources
            .Where(r => r.IsSmartResource);
        var userId = ActiveUserId;
        if (userId > 0)
            results = results.Where(r => r.UserProfileId == userId);
        return await results.ToListAsync();
    }

    // Get smart resource by type
    public async Task<LearningResource?> GetSmartResourceByTypeAsync(string smartResourceType)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = db.LearningResources
            .Where(r => r.IsSmartResource && r.SmartResourceType == smartResourceType);
        var userId = ActiveUserId;
        if (userId > 0)
            results = results.Where(r => r.UserProfileId == userId);
        return await results.FirstOrDefaultAsync();
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
            // UXDivers popup removed - error already logged above
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
            // UXDivers popup removed - error already logged above
            return false;
        }
    }

    public async Task GetStarterVocabulary(string nativeLanguage, string targetLanguage)
    {
        var prompt = string.Empty;
        using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GetStarterVocabulary.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(await reader.ReadToEndAsync());
            prompt = await template.RenderAsync(new { native_language = nativeLanguage, target_language = targetLanguage });
        }

        try
        {
            var response = await _aiService.SendPrompt<string>(prompt);

            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("AI returned empty response for starter vocabulary");
                return;
            }

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

            _logger.LogInformation("Starter vocabulary resource created");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in GetStarterVocabulary");
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

        var userId = ActiveUserId;
        if (userId > 0)
        {
            var userWordIds = db.ResourceVocabularyMappings
                .Join(db.LearningResources.Where(r => r.UserProfileId == userId),
                    m => m.ResourceId, r => r.Id, (m, r) => m.VocabularyWordId);
            return await db.VocabularyWords
                .AsNoTracking()
                .Include(vw => vw.LearningResources)
                .Where(w => userWordIds.Contains(w.Id))
                .ToListAsync();
        }
        return await db.VocabularyWords
            .AsNoTracking()
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

        var userId = ActiveUserId;
        if (userId > 0)
        {
            var userWordIds = db.ResourceVocabularyMappings
                .Join(db.LearningResources.Where(r => r.UserProfileId == userId),
                    m => m.ResourceId, r => r.Id, (m, r) => m.VocabularyWordId);
            return await db.VocabularyWords
                .Include(vw => vw.LearningResources)
                .Where(vw => userWordIds.Contains(vw.Id))
                .Where(vw =>
                    (vw.TargetLanguageTerm != null && vw.TargetLanguageTerm.ToLower().Contains(searchTerm)) ||
                    (vw.NativeLanguageTerm != null && vw.NativeLanguageTerm.ToLower().Contains(searchTerm)))
                .ToListAsync();
        }
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

        var userId = ActiveUserId;
        int totalWords;
        int associatedWords;
        if (userId > 0)
        {
            var userWordIds = db.ResourceVocabularyMappings
                .Join(db.LearningResources.Where(r => r.UserProfileId == userId),
                    m => m.ResourceId, r => r.Id, (m, r) => m.VocabularyWordId);
            totalWords = await db.VocabularyWords.Where(w => userWordIds.Contains(w.Id)).CountAsync();
            associatedWords = await userWordIds.Distinct().CountAsync();
        }
        else
        {
            totalWords = await db.VocabularyWords.CountAsync();
            associatedWords = await db.ResourceVocabularyMappings
                .Select(rvm => rvm.VocabularyWordId)
                .Distinct()
                .CountAsync();
        }
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
            // UXDivers popup removed - error already logged above
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
            // UXDivers popup removed - error already logged above
            return false;
        }
    }

    /// <summary>
    /// Updates vocabulary word terms by ID, avoiding context tracking issues
    /// </summary>
    public async Task<bool> UpdateVocabularyWordTermsAsync(int wordId, string targetTerm, string nativeTerm)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var word = await db.VocabularyWords.FindAsync(wordId);
            if (word == null)
                return false;

            word.TargetLanguageTerm = targetTerm;
            word.NativeLanguageTerm = nativeTerm;
            word.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating vocabulary word terms");
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
            // UXDivers popup removed - error already logged above
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
            // UXDivers popup removed - error already logged above
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
            // UXDivers popup removed - error already logged above
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

    /// <summary>
    /// Check if a learning resource exists with the same MediaUrl (YouTube URL)
    /// </summary>
    public async Task<LearningResource?> FindDuplicateByMediaUrlAsync(string mediaUrl)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl))
            return null;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var normalizedUrl = mediaUrl.Trim().ToLower();

        return await db.LearningResources
            .FirstOrDefaultAsync(lr =>
                lr.MediaUrl != null &&
                lr.MediaUrl.Trim().ToLower() == normalizedUrl);
    }

    /// <summary>
    /// Check if a learning resource exists with a similar title
    /// </summary>
    public async Task<LearningResource?> FindDuplicateByTitleAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var normalizedTitle = title.Trim().ToLower();

        return await db.LearningResources
            .FirstOrDefaultAsync(lr =>
                lr.Title != null &&
                lr.Title.Trim().ToLower() == normalizedTitle);
    }
}
