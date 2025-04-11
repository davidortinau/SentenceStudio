using System.Diagnostics;
using SentenceStudio.Common;
using SentenceStudio.Models;
using SQLite;
using SentenceStudio.Services;

namespace SentenceStudio.Data;

public class LearningResourceRepository
{
    private SQLiteAsyncConnection Database;
    private VocabularyService _vocabularyService;

    public LearningResourceRepository(IServiceProvider serviceProvider = null)
    {
        if (serviceProvider != null)
        {
            _vocabularyService = serviceProvider.GetService<VocabularyService>();
        }
    }

    async Task Init()
    {
        if (Database is not null)
            return;

        Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

        try
        {
            await Database.CreateTablesAsync<LearningResource, ResourceVocabularyMapping>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
        }
    }

    public async Task<List<LearningResource>> GetAllResourcesAsync()
    {
        await Init();
        return await Database.Table<LearningResource>().ToListAsync();
    }

    public async Task<LearningResource> GetResourceAsync(int resourceId)
    {
        await Init();
        var resource = await Database.Table<LearningResource>().Where(r => r.ID == resourceId).FirstOrDefaultAsync();
        
        // Load associated vocabulary
        if (resource != null)
        {
            // Get all vocabulary mappings for this resource
            var mappings = await Database.Table<ResourceVocabularyMapping>()
                .Where(m => m.ResourceID == resourceId)
                .ToListAsync();
                
            // Get the vocabulary words
            if (mappings.Any() && _vocabularyService != null)
            {
                List<VocabularyWord> vocabularyWords = new List<VocabularyWord>();
                foreach(var mapping in mappings)
                {
                    var word = await _vocabularyService.GetWordAsync(mapping.VocabularyWordID);
                    if (word != null)
                    {
                        vocabularyWords.Add(word);
                    }
                }
                resource.Vocabulary = vocabularyWords;
            }
        }
        
        return resource;
    }

    public async Task<int> SaveResourceAsync(LearningResource resource)
    {
        await Init();
        int result = -1;
        
        // Set timestamps
        if (resource.CreatedAt == default)
            resource.CreatedAt = DateTime.UtcNow;
            
        resource.UpdatedAt = DateTime.UtcNow;
        
        // Begin a transaction to ensure both the resource and its vocabulary mappings are saved
        await Database.RunInTransactionAsync(connection => {
            if (resource.ID != 0)
            {
                result = connection.Update(resource);
            }
            else
            {
                result = connection.Insert(resource);
            }
            
            // Save the vocabulary mappings if this has vocabulary
            if (resource.Vocabulary != null && resource.Vocabulary.Count > 0 && _vocabularyService != null)
            {
                // First delete all existing mappings
                connection.Table<ResourceVocabularyMapping>()
                    .Delete(m => m.ResourceID == resource.ID);
                    
                // Then save all vocabulary words and create new mappings
                foreach (var word in resource.Vocabulary)
                {
                    // Save the vocabulary word if it doesn't exist
                    if (word.ID == 0)
                    {
                        // Do not use await inside a synchronous delegate - save first
                        _vocabularyService.SaveWordAsync(word);
                    }
                    
                    // Create a mapping
                    var mapping = new ResourceVocabularyMapping
                    {
                        ResourceID = resource.ID,
                        VocabularyWordID = word.ID
                    };
                    
                    connection.Insert(mapping);
                }
            }
        });

        return result;
    }    

    public async Task<int> DeleteResourceAsync(LearningResource resource)
    {
        await Init();
        
        // First delete all vocabulary mappings
        await Database.Table<ResourceVocabularyMapping>()
            .Where(m => m.ResourceID == resource.ID)
            .DeleteAsync();
            
        // Then delete the resource
        return await Database.DeleteAsync(resource);
    }
    
    public async Task<List<LearningResource>> SearchResourcesAsync(string query)
    {
        await Init();
        return await Database.Table<LearningResource>()
            .Where(r => r.Title.Contains(query) || r.Description.Contains(query) || 
                   r.Tags.Contains(query) || r.Language.Contains(query))
            .ToListAsync();
    }
    
    // Get resources of a specific type
    public async Task<List<LearningResource>> GetResourcesByTypeAsync(string mediaType)
    {
        await Init();
        return await Database.Table<LearningResource>()
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
        await Init();
        return await Database.Table<LearningResource>()
            .Where(r => r.Language == language)
            .ToListAsync();
    }
}