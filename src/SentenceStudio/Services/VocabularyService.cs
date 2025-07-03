using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;

namespace SentenceStudio.Services;

public class VocabularyService
{
    private readonly IServiceProvider _serviceProvider;
    private AiService _aiService;
    private ISyncService _syncService;

    public VocabularyService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _aiService = serviceProvider.GetRequiredService<AiService>();
        _syncService = serviceProvider.GetService<ISyncService>();
    }

    public async Task<List<VocabularyList>> GetListsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyLists.ToListAsync();
    }

    /// <summary>
    /// Retrieves all vocabulary lists with their associated words asynchronously.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="VocabularyList"/> objects.</returns>
    public async Task<List<VocabularyList>> GetAllListsWithWordsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var vocabularyLists = await db.VocabularyLists
            .Include(vl => vl.Words)
            .ToListAsync();
        
        foreach (var vocabularyList in vocabularyLists)
        {
            Debug.WriteLine($"List {vocabularyList.Name} has {vocabularyList.Words.Count} words");
        }
        
        return vocabularyLists;
    }

    /// <summary>
    /// Retrieves a list of vocabulary words asynchronously.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="VocabularyWord"/>.</returns>
    public async Task<List<VocabularyWord>> GetWordsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyWords.ToListAsync();
    }

    public async Task<VocabularyList> GetListAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var vocabularyList = await db.VocabularyLists
            .Include(vl => vl.Words)
            .Where(i => i.Id == id)
            .FirstOrDefaultAsync();
        
        return vocabularyList;
    }

    public async Task<VocabularyWord> GetWordAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyWords.Where(i => i.Id == id).FirstOrDefaultAsync();
    }

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

    public async Task<int> SaveListAsync(VocabularyList list)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Set timestamps
        if (list.CreatedAt == default)
            list.CreatedAt = DateTime.UtcNow;
            
        list.UpdatedAt = DateTime.UtcNow;
        
        try
        {
            if (list.Id != 0)
            {
                // Update existing list
                db.VocabularyLists.Update(list);
                
                // Handle vocabulary words - EF Core will manage the many-to-many relationship
                if (list.Words != null)
                {
                    foreach (var word in list.Words)
                    {
                        if (word.CreatedAt == default)
                            word.CreatedAt = DateTime.UtcNow;
                        word.UpdatedAt = DateTime.UtcNow;
                        
                        if (word.Id == 0)
                        {
                            db.VocabularyWords.Add(word);
                        }
                        else
                        {
                            db.VocabularyWords.Update(word);
                        }
                    }
                }
            }
            else
            {
                // Create new list
                db.VocabularyLists.Add(list);
                
                if (list.Words != null)
                {
                    foreach (var word in list.Words)
                    {
                        if (word.CreatedAt == default)
                            word.CreatedAt = DateTime.UtcNow;
                        word.UpdatedAt = DateTime.UtcNow;
                        
                        if (word.Id == 0)
                        {
                            db.VocabularyWords.Add(word);
                        }
                    }
                }
            }
            
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
        }

        return list.Id;
    }

    public async Task<bool> DeleteListAsync(VocabularyList list)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            // EF Core will handle the cascade delete of the many-to-many relationships
            db.VocabularyLists.Remove(list);
            await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return false;
        }
        return true;
    }

    public async Task<int> SaveWordAsync(VocabularyWord word)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Set timestamps
        if (word.CreatedAt == default)
            word.CreatedAt = DateTime.UtcNow;
            
        word.UpdatedAt = DateTime.UtcNow;
        
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
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return result;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return -1;
        }
    }

    public async Task<int> DeleteWordAsync(VocabularyWord word)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            db.VocabularyWords.Remove(word);
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

    public async Task<int> DeleteWordFromListAsync(VocabularyWord word, int listId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var list = await db.VocabularyLists
                .Include(vl => vl.Words)
                .Where(vl => vl.Id == listId)
                .FirstOrDefaultAsync();
                
            if (list != null && list.Words.Contains(word))
            {
                list.Words.Remove(word);
                int result = await db.SaveChangesAsync();
                
                _syncService?.TriggerSyncAsync().ConfigureAwait(false);
                
                return result;
            }
            
            return 0;
        }
        catch (Exception ex)
        {
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            return -1;
        }
    }


    // public async Task SaveWordsAsync(int listId, List<VocabularyWord> listWords)
    // {
    //     await Init();
    //     foreach (var term in listWords)
    //     {
    //         // term.VocabularyListId = listId;
    //         await SaveWordAsync(term);
    //     }
    // }

    public async Task GetStarterVocabulary(string nativeLanguage, string targetLanguage)
        {       
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GetStarterVocabulary.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync(new { native_language = nativeLanguage, target_language = targetLanguage});

                // //Debug.WriteLine(prompt);
            }
            
            try
            {
                var response = await _aiService.SendPrompt<string>(prompt);

                VocabularyList list = new();
                list.Name = "Sentence Studio Starter Vocabulary";
                list.Words = VocabularyWord.ParseVocabularyWords(response);
                var listId = await SaveListAsync(list);
                await AppShell.DisplayToastAsync("Starter vocabulary list created");
                
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during the process
                Debug.WriteLine($"An error occurred GetStarterVocabulary: {ex.Message}");
                
            }
        }
}
