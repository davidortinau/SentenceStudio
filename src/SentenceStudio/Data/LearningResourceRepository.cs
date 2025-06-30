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
            _vocabularyService = serviceProvider.GetService<VocabularyService>();
    }

    // --- Added for VocabularyService replacement ---
    public async Task<VocabularyWord> GetWordByNativeTermAsync(string nativeTerm)
    {
        await Init();
        return await Database.Table<VocabularyWord>()
            .Where(w => w.NativeLanguageTerm == nativeTerm)
            .FirstOrDefaultAsync();
    }

    public async Task<VocabularyWord> GetWordByTargetTermAsync(string targetTerm)
    {
        await Init();
        return await Database.Table<VocabularyWord>()
            .Where(w => w.TargetLanguageTerm == targetTerm)
            .FirstOrDefaultAsync();
    }

    public async Task<int> SaveWordAsync(VocabularyWord word)
    {
        await Init();
        int result = -1;
        if (word.ID != 0)
        {
            try
            {
                result = await Database.UpdateAsync(word);
            }
            catch (Exception ex)
            {
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }
        else
        {
            try
            {
                result = await Database.InsertAsync(word);
            }
            catch (Exception ex)
            {
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }
        return result;
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
        
        // First save the resource to get an ID
        if (resource.ID != 0)
        {
            result = await Database.UpdateAsync(resource);
        }
        else
        {
            result = await Database.InsertAsync(resource);
        }

        // Save the vocabulary words first to ensure they have IDs
        if (resource.Vocabulary != null && resource.Vocabulary.Count > 0 && _vocabularyService != null)
        {
            List<VocabularyWord> updatedWords = new List<VocabularyWord>();

            // Save all vocabulary words to ensure they have IDs
            foreach (var word in resource.Vocabulary)
            {
                if (word.ID == 0)
                {
                    await _vocabularyService.SaveWordAsync(word);
                }
                updatedWords.Add(word);
            }

            resource.Vocabulary = updatedWords;

            // Now handle the mappings in a transaction
            await Database.RunInTransactionAsync(connection =>
            {
                // First delete all existing mappings
                connection.Table<ResourceVocabularyMapping>()
                    .Delete(m => m.ResourceID == resource.ID);

                // Create new mappings with the saved vocabulary words
                foreach (var word in resource.Vocabulary)
                {
                    // Create a mapping
                    var mapping = new ResourceVocabularyMapping
                    {
                        ResourceID = resource.ID,
                        VocabularyWordID = word.ID
                    };

                    connection.Insert(mapping);
                }
            });
        }

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