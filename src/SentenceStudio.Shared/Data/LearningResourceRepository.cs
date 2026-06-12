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
    private readonly IFileSystemService _fileSystem;
    private readonly SentenceStudio.Abstractions.IPreferencesService? _preferences;

    // Lazily resolved so this repo can be registered on hosts (like the API)
    // that don't wire up the full AiService config section. Only needed by
    // EnsureStarterVocabularyAsync today; plan-generation paths never touch it.
    private AiService? _aiService;
    private AiService AiService => _aiService ??= _serviceProvider.GetService<AiService>()
        ?? throw new InvalidOperationException(
            "AiService is not registered or could not be constructed in this host.");

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
            _preferences = serviceProvider.GetService<SentenceStudio.Abstractions.IPreferencesService>();
        }
    }

    private string ActiveUserId => _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;

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
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetAllVocabularyWordsAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<VocabularyWord>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userWordIds = db.ResourceVocabularyMappings
            .Join(db.LearningResources.Where(r => r.UserProfileId == userId),
                m => m.ResourceId, r => r.Id, (m, r) => m.VocabularyWordId);
        return await db.VocabularyWords.Where(w => userWordIds.Contains(w.Id)).ToListAsync();
    }

    /// <summary>
    /// Get just the target language terms for a specific language (optimized for AI prompts)
    /// </summary>
    public async Task<List<string>> GetVocabularyTargetTermsByLanguageAsync(string language, int? limit = null)
    {
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetVocabularyTargetTermsByLanguageAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<string>();
        }

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
            .Where(wr => wr.Resource.Language == language)
            .Where(wr => wr.Resource.UserProfileId == userId);

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
            var existsInDb = await db.VocabularyWords.AnyAsync(w => w.Id == word.Id);

            if (existsInDb)
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
            _logger.LogError(ex, "Error saving vocabulary word {WordId}", word?.Id);
            return -1;
        }
    }

    public async Task<List<LearningResource>> GetAllResourcesAsync()
    {
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetAllResourcesAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<LearningResource>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var query = db.LearningResources
            .Include(r => r.Vocabulary);
        return await query.Where(r => r.UserProfileId == userId).ToListAsync();
    }

    /// <summary>
    /// Lightweight query: resources without vocabulary navigation properties.
    /// Supports optional type/language filters pushed to SQL.
    /// </summary>
    /// <param name="filterType">Optional MediaType filter (pass "All" or null to disable).</param>
    /// <param name="filterLanguages">Optional language whitelist.</param>
    /// <param name="userProfileId">
    /// When non-empty, restrict results to resources owned by this profile id.
    /// On multi-user hosts (the API) the caller MUST pass this — falling back
    /// to <c>ActiveUserId</c> from <see cref="SentenceStudio.Abstractions.IPreferencesService"/>
    /// is correct only on single-user devices (MAUI / mobile). When the
    /// preferences service isn't registered (API host) and this argument is
    /// empty, the query would return rows across all users; callers like
    /// <c>DeterministicPlanBuilder</c> must therefore thread the
    /// request-scoped <c>UserProfileId</c> through.
    /// </param>
    public async Task<List<LearningResource>> GetAllResourcesLightweightAsync(
        string filterType = null, List<string> filterLanguages = null, string? userProfileId = null)
    {
        var userId = !string.IsNullOrEmpty(userProfileId) ? userProfileId : ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetAllResourcesLightweightAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<LearningResource>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IQueryable<LearningResource> query = db.LearningResources.AsNoTracking()
            .Where(r => r.UserProfileId == userId);

        if (!string.IsNullOrEmpty(filterType) && filterType != "All")
            query = query.Where(r => r.MediaType == filterType);

        if (filterLanguages != null && filterLanguages.Count > 0)
            query = query.Where(r => filterLanguages.Contains(r.Language));

        return await query
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<LearningResource> GetResourceAsync(string resourceId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var resource = await db.LearningResources
            .Include(r => r.Vocabulary) // This uses the skip navigation to load vocabulary
            .Where(r => r.Id == resourceId)
            .FirstOrDefaultAsync();

        return resource;
    }

    public async Task<string> SaveResourceAsync(LearningResource resource)
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

            // Check if this resource already exists in the database
            var existsInDb = await db.LearningResources.AnyAsync(r => r.Id == resource.Id);

            if (existsInDb)
            {
                var existingResource = await db.LearningResources
                    .Include(r => r.Vocabulary)
                    .FirstOrDefaultAsync(r => r.Id == resource.Id);

                if (existingResource != null)
                {
                    // Update resource properties
                    db.Entry(existingResource).CurrentValues.SetValues(resource);

                    // Handle vocabulary associations
                    var dbVocabularyWords = await db.VocabularyWords
                        .Where(v => vocabularyWordIds.Contains(v.Id))
                        .ToListAsync();

                    existingResource.Vocabulary.Clear();
                    foreach (var word in dbVocabularyWords)
                    {
                        existingResource.Vocabulary.Add(word);
                    }
                }
            }
            else
            {
                // New resource — set user ownership
                resource.UserProfileId ??= !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : null;

                // Clear navigation collection before Add to prevent EF Core
                // from cascade-inserting already-saved VocabularyWord entities
                resource.Vocabulary?.Clear();

                db.LearningResources.Add(resource);
                await db.SaveChangesAsync();

                // Now associate vocabulary words that already exist in the DB
                if (vocabularyWordIds.Any())
                {
                    var dbVocabularyWords = await db.VocabularyWords
                        .Where(v => vocabularyWordIds.Contains(v.Id))
                        .ToListAsync();

                    resource.Vocabulary ??= new List<VocabularyWord>();
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
            return string.Empty;
        }
    }

    public async Task<int> DeleteResourceAsync(LearningResource resource)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            // Capture the vocab IDs reachable via this resource BEFORE removing the resource;
            // ResourceVocabularyMapping cascades to gone, so we can't read them after the fact.
            var resourceWordIds = await db.ResourceVocabularyMappings
                .Where(m => m.ResourceId == resource.Id)
                .Select(m => m.VocabularyWordId)
                .Distinct()
                .ToListAsync();

            db.LearningResources.Remove(resource);
            int affected = await db.SaveChangesAsync();

            // Cascade orphan sweep: for each vocab word this resource owned a mapping for,
            // check whether the SAME user can still reach the word via another of their
            // resources. If not, delete the user's VocabularyProgress row so it doesn't become
            // an "eternally due" orphan that pollutes plan generation. Matches the user's
            // mental model — "if I removed the resource, my progress on its words is gone too".
            // See Brot incident, 2026-06-12.
            var ownerUserId = resource.UserProfileId;
            if (resourceWordIds.Count > 0 && !string.IsNullOrEmpty(ownerUserId))
            {
                var stillReachableWordIds = await db.ResourceVocabularyMappings
                    .Where(m => resourceWordIds.Contains(m.VocabularyWordId))
                    .Join(db.LearningResources.Where(r => r.UserProfileId == ownerUserId),
                          m => m.ResourceId,
                          r => r.Id,
                          (m, r) => m.VocabularyWordId)
                    .Distinct()
                    .ToListAsync();
                var orphanedWordIds = resourceWordIds.Except(stillReachableWordIds).ToList();
                if (orphanedWordIds.Count > 0)
                {
                    var orphanProgress = await db.VocabularyProgresses
                        .Where(vp => vp.UserId == ownerUserId
                            && orphanedWordIds.Contains(vp.VocabularyWordId))
                        .ToListAsync();
                    if (orphanProgress.Count > 0)
                    {
                        db.VocabularyProgresses.RemoveRange(orphanProgress);
                        await db.SaveChangesAsync();
                        _logger.LogInformation(
                            "DeleteResourceAsync orphan sweep: removed {Count} VocabularyProgress rows for user {UserId} that lost their last reachable mapping after deleting resource {ResourceId}",
                            orphanProgress.Count, ownerUserId, resource.Id);
                    }
                }
            }

            await tx.CommitAsync();
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return affected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ DeleteResourceAsync error for resource {ResourceId}", resource.Id);
            try { await tx.RollbackAsync(); } catch { }
            return -1;
        }
    }

    public async Task<List<LearningResource>> SearchResourcesAsync(string query)
    {
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("SearchResourcesAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<LearningResource>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = db.LearningResources
            .AsNoTracking()
            .Where(r => r.UserProfileId == userId)
            .Where(r => r.Title.Contains(query) || r.Description.Contains(query) ||
                   r.Tags.Contains(query) || r.Language.Contains(query));
        return await results.ToListAsync();
    }

    // Get resources of a specific type
    public async Task<List<LearningResource>> GetResourcesByTypeAsync(string mediaType)
    {
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetResourcesByTypeAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<LearningResource>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = db.LearningResources
            .Where(r => r.UserProfileId == userId)
            .Where(r => r.MediaType == mediaType);
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
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetResourcesByLanguageAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<LearningResource>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = db.LearningResources
            .Where(r => r.UserProfileId == userId)
            .Where(r => r.Language == language);
        return await results.ToListAsync();
    }

    // Get all smart resources
    public async Task<List<LearningResource>> GetSmartResourcesAsync()
    {
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetSmartResourcesAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<LearningResource>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = db.LearningResources
            .Where(r => r.UserProfileId == userId)
            .Where(r => r.IsSmartResource);
        return await results.ToListAsync();
    }

    // Get smart resource by type
    public async Task<LearningResource?> GetSmartResourceByTypeAsync(string smartResourceType)
    {
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetSmartResourceByTypeAsync called without an active user — returning null to prevent cross-tenant data leak.");
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var results = db.LearningResources
            .Where(r => r.UserProfileId == userId)
            .Where(r => r.IsSmartResource && r.SmartResourceType == smartResourceType);
        return await results.FirstOrDefaultAsync();
    }

    // Add vocabulary word to a learning resource
    public async Task<bool> AddVocabularyToResourceAsync(string resourceId, string vocabularyWordId)
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
    public async Task<bool> RemoveVocabularyFromResourceAsync(string resourceId, string vocabularyWordId)
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

    public async Task<bool> StarterResourceExistsAsync(string? targetLanguage = null)
    {
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("StarterResourceExistsAsync called without an active user — returning false to prevent cross-tenant data leak (and to avoid blocking starter creation).");
            return false;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var query = db.LearningResources
            .Where(r => r.UserProfileId == userId)
            .Where(r => r.Tags != null && r.Tags.Contains("starter"));

        if (!string.IsNullOrEmpty(targetLanguage))
            query = query.Where(r => r.Language == targetLanguage);

        return await query.AnyAsync();
    }

    public async Task GetStarterVocabulary(string nativeLanguage, string targetLanguage)
    {
        // Guard: don't create duplicates
        if (await StarterResourceExistsAsync(targetLanguage))
        {
            _logger.LogInformation("Starter resource already exists for {Language}, skipping creation", targetLanguage);
            return;
        }

        var prompt = string.Empty;
        using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("GetStarterVocabulary.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(await reader.ReadToEndAsync());
            prompt = await template.RenderAsync(new { native_language = nativeLanguage, target_language = targetLanguage });
        }

        try
        {
            var response = await AiService.SendPrompt<string>(prompt);

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
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetAllVocabularyWordsWithResourcesAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<VocabularyWord>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var userWordIds = db.ResourceVocabularyMappings
            .Join(db.LearningResources.Where(r => r.UserProfileId == userId),
                m => m.ResourceId, r => r.Id, (m, r) => m.VocabularyWordId);
        return await db.VocabularyWords
            .AsNoTracking()
            .Include(vw => vw.LearningResources)
            .AsSplitQuery()
            .Where(w => userWordIds.Contains(w.Id))
            .ToListAsync();
    }

    /// <summary>
    /// Search vocabulary words by target or native language terms
    /// </summary>
    public async Task<List<VocabularyWord>> SearchVocabularyWordsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetAllVocabularyWordsWithResourcesAsync();

        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("SearchVocabularyWordsAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<VocabularyWord>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var searchTerm = query.ToLower().Trim();

        var userWordIds = db.ResourceVocabularyMappings
            .Join(db.LearningResources.Where(r => r.UserProfileId == userId),
                m => m.ResourceId, r => r.Id, (m, r) => m.VocabularyWordId);
        return await db.VocabularyWords
            .Include(vw => vw.LearningResources)
            .AsSplitQuery()
            .Where(vw => userWordIds.Contains(vw.Id))
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
            .AsSplitQuery()
            .Where(vw => db.ResourceVocabularyMappings.Any(rvm => rvm.VocabularyWordId == vw.Id))
            .ToListAsync();
    }

    /// <summary>
    /// Get a specific vocabulary word with all its associated resources
    /// </summary>
    public async Task<VocabularyWord?> GetVocabularyWordWithResourcesAsync(string wordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.VocabularyWords
            .Include(vw => vw.LearningResources)
            .AsSplitQuery()
            .FirstOrDefaultAsync(vw => vw.Id == wordId);
    }

    /// <summary>
    /// Get a specific vocabulary word by ID (without resources)
    /// </summary>
    public async Task<VocabularyWord?> GetVocabularyWordByIdAsync(string wordId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.VocabularyWords
            .FirstOrDefaultAsync(vw => vw.Id == wordId);
    }

    /// <summary>
    /// Get all learning resources associated with a specific vocabulary word
    /// </summary>
    public async Task<List<LearningResource>> GetResourcesForVocabularyWordAsync(string wordId)
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
        var userId = ActiveUserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetVocabularyStatsAsync called without an active user — returning zeroed stats to prevent cross-tenant data leak.");
            return (0, 0, 0);
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var userWordIds = db.ResourceVocabularyMappings
            .Join(db.LearningResources.Where(r => r.UserProfileId == userId),
                m => m.ResourceId, r => r.Id, (m, r) => m.VocabularyWordId);
        int totalWords = await db.VocabularyWords.Where(w => userWordIds.Contains(w.Id)).CountAsync();
        int associatedWords = await userWordIds.Distinct().CountAsync();
        var orphanedWords = totalWords - associatedWords;

        return (totalWords, associatedWords, orphanedWords);
    }

    /// <summary>
    /// Safely delete a vocabulary word and all its associations
    /// </summary>
    public async Task<bool> DeleteVocabularyWordAsync(string wordId)
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
    public async Task<bool> UpdateVocabularyWordTermsAsync(string wordId, string targetTerm, string nativeTerm)
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
    public async Task<bool> BulkAssociateWordsWithResourceAsync(string resourceId, List<string> vocabularyWordIds)
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
    public async Task<bool> BulkRemoveWordsFromResourceAsync(string resourceId, List<string> vocabularyWordIds)
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
    public async Task<bool> BulkDeleteVocabularyWordsAsync(List<string> vocabularyWordIds)
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
    public async Task<List<VocabularyWord>> GetVocabularyWordsByResourceAsync(string resourceId)
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
    public async Task<List<LearningResource>> GetResourcesContainingWordAsync(string vocabularyWordId)
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
    /// Bulk update lemma values for vocabulary words.
    /// </summary>
    public async Task<int> BulkUpdateLemmasAsync(Dictionary<string, string> wordIdToLemma)
    {
        if (wordIdToLemma == null || !wordIdToLemma.Any())
            return 0;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        int updated = 0;
        foreach (var (wordId, lemma) in wordIdToLemma)
        {
            var word = await db.VocabularyWords.FindAsync(wordId);
            if (word != null)
            {
                word.Lemma = lemma;
                word.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
        }

        if (updated > 0)
            await db.SaveChangesAsync();

        return updated;
    }

    /// <summary>
    /// Bulk set the Language property on multiple vocabulary words.
    /// Returns the number of words updated.
    /// </summary>
    public async Task<int> BulkSetLanguageAsync(List<string> vocabularyWordIds, string language)
    {
        if (vocabularyWordIds == null || vocabularyWordIds.Count == 0 || string.IsNullOrWhiteSpace(language))
            return 0;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var words = await db.VocabularyWords
                .Where(vw => vocabularyWordIds.Contains(vw.Id))
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var word in words)
            {
                word.Language = language;
                word.UpdatedAt = now;
            }

            if (words.Count > 0)
                await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return words.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk setting vocabulary word language");
            return 0;
        }
    }

    /// <summary>
    /// Bulk add tags (additive, deduped, case-insensitive) to multiple vocabulary words.
    /// Existing tags on each word are preserved; new tags are merged in.
    /// Returns the number of words updated.
    /// </summary>
    public async Task<int> BulkAddTagsAsync(List<string> vocabularyWordIds, IEnumerable<string> tagsToAdd)
    {
        if (vocabularyWordIds == null || vocabularyWordIds.Count == 0 || tagsToAdd == null)
            return 0;

        var normalizedNewTags = tagsToAdd
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedNewTags.Count == 0)
            return 0;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var words = await db.VocabularyWords
                .Where(vw => vocabularyWordIds.Contains(vw.Id))
                .ToListAsync();

            var now = DateTime.UtcNow;
            int updated = 0;
            foreach (var word in words)
            {
                var existing = (word.Tags ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

                bool changed = false;
                foreach (var tag in normalizedNewTags)
                {
                    if (!existing.Any(e => string.Equals(e, tag, StringComparison.OrdinalIgnoreCase)))
                    {
                        existing.Add(tag);
                        changed = true;
                    }
                }

                if (changed)
                {
                    word.Tags = string.Join(", ", existing);
                    word.UpdatedAt = now;
                    updated++;
                }
            }

            if (updated > 0)
                await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk adding tags to vocabulary words");
            return 0;
        }
    }

    /// <summary>
    /// Merge duplicate vocabulary words: reassign all resource mappings from source words
    /// to the keeper word, then delete the source words.
    /// </summary>
    public async Task<int> MergeVocabularyWordsAsync(string keeperWordId, List<string> deleteWordIds)
    {
        if (string.IsNullOrWhiteSpace(keeperWordId) || deleteWordIds == null || !deleteWordIds.Any())
            return 0;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        int deleted = 0;

        // Get existing mappings for the keeper to avoid duplicates
        var keeperResourceIds = await db.ResourceVocabularyMappings
            .Where(m => m.VocabularyWordId == keeperWordId)
            .Select(m => m.ResourceId)
            .ToListAsync();

        foreach (var deleteId in deleteWordIds)
        {
            // Reassign resource mappings from the duplicate to the keeper
            var mappingsToReassign = await db.ResourceVocabularyMappings
                .Where(m => m.VocabularyWordId == deleteId)
                .ToListAsync();

            foreach (var mapping in mappingsToReassign)
            {
                if (!keeperResourceIds.Contains(mapping.ResourceId))
                {
                    db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                    {
                        ResourceId = mapping.ResourceId,
                        VocabularyWordId = keeperWordId
                    });
                    keeperResourceIds.Add(mapping.ResourceId);
                }
                db.ResourceVocabularyMappings.Remove(mapping);
            }

            // Delete the duplicate word
            var word = await db.VocabularyWords.FindAsync(deleteId);
            if (word != null)
            {
                db.VocabularyWords.Remove(word);
                deleted++;
            }
        }

        if (deleted > 0)
        {
            await db.SaveChangesAsync();
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        }

        return deleted;
    }
}
