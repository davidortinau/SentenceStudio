

namespace SentenceStudio.Services;

public class VocabularyService
{
    private SQLiteAsyncConnection Database;
    private AiService _aiService;

    public VocabularyService(IServiceProvider service)
    {
        _aiService = service.GetRequiredService<AiService>();
    }

    async Task Init()
    {
        if (Database is not null)
            return;

        Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

        CreateTablesResult result;
        
        try
        {
            result = await Database.CreateTablesAsync<VocabularyList, VocabularyWord, VocabularyListVocabularyWord>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
        }
    }

    public async Task<List<VocabularyList>> GetListsAsync()
    {
        await Init();
        return await Database.Table<VocabularyList>().ToListAsync();
    }

    /// <summary>
    /// Retrieves all vocabulary lists with their associated words asynchronously.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="VocabularyList"/> objects.</returns>
    public async Task<List<VocabularyList>> GetAllListsWithWordsAsync()
{
    await Init();
    
    var vocabularyLists = await Database.Table<VocabularyList>().ToListAsync();
    
    foreach (var vocabularyList in vocabularyLists)
    {
        
        var wordIds = await Database.QueryAsync<int>(@"
            SELECT VocabularyWordId
            FROM VocabularyListVocabularyWord
            WHERE VocabularyListId = ?", vocabularyList.ID);

        vocabularyList.Words = await Database.Table<VocabularyWord>()
            .Where(vw => wordIds.Contains(vw.ID))
            .ToListAsync();
    }
    
    return vocabularyLists;
}

    /// <summary>
    /// Retrieves a list of vocabulary words asynchronously.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="VocabularyWord"/>.</returns>
    public async Task<List<VocabularyWord>> GetWordsAsync()
    {
        await Init();
        return await Database.Table<VocabularyWord>().ToListAsync();
    }

    public async Task<VocabularyList> GetListAsync(int id)
    {
        await Init();    
        var vocabularyList = await Database.Table<VocabularyList>().Where(i => i.ID == id).FirstOrDefaultAsync();
        if (vocabularyList != null)
        {
            var wordIds = await Database.QueryAsync<int>(@"
            SELECT VocabularyWordId
            FROM VocabularyListVocabularyWord
            WHERE VocabularyListId = ?", vocabularyList.ID);

            vocabularyList.Words = await Database.Table<VocabularyWord>()
                .Where(vw => wordIds.Contains(vw.ID))
                .ToListAsync();            
        }
        
        return vocabularyList;
    }

    public async Task<VocabularyWord> GetWordAsync(int id)
    {
        await Init();
        return await Database.Table<VocabularyWord>().Where(i => i.ID == id).FirstOrDefaultAsync();
    }

    public async Task<int> SaveListAsync(VocabularyList list)
    {
        await Init();
        int result = -1;
        if (list.ID != 0)
        {
            try
            {
                result = await Database.UpdateAsync(list);
                result = await Database.UpdateAllAsync(list.Words);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{ex.Message}");
            }
        }
        else
        {
            try
            {
                result = await Database.InsertAsync(list);

                // list.Words = new List<Event> { event1 };
                
            
                if (list.Words != null)
                {
                    foreach (var term in list.Words)
                    {
                        // term.VocabularyListId = list.ID;
                        await SaveWordAsync(term);
                        await SaveWordToList(term, list.ID);
                    }
                }
            }
            catch (Exception ex)
            {
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }

        return list.ID;
    }

    private async Task SaveWordToList(VocabularyWord term, int listID)
    {
        await Init();
        VocabularyListVocabularyWord listWord = new VocabularyListVocabularyWord();
        listWord.VocabularyListId = listID;
        listWord.VocabularyWordId = term.ID;
        await Database.InsertAsync(listWord);
    }

    public async Task<bool> DeleteListAsync(VocabularyList list)
    {
        await Init();
        try{
            await Database.DeleteAsync(list);
            await Database.ExecuteAsync("DELETE FROM VocabularyListVocabularyWord WHERE VocabularyListId = ?", list.ID);
            await Database.ExecuteAsync("DELETE FROM VocabularyWord WHERE ID NOT IN (SELECT VocabularyWordId FROM VocabularyListVocabularyWord)");
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
        await Init();
        int result = -1;
        if (word.ID != 0)
        {
            try
            {
                result = await Database.UpdateAsync(word);
            }catch(Exception ex)
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

    public async Task<int> DeleteWordAsync(VocabularyWord word)
    {
        await Init();
        return await Database.DeleteAsync(word);
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
                var template = Template.Parse(reader.ReadToEnd());
                prompt = await template.RenderAsync(new { native_language = nativeLanguage, target_language = targetLanguage});

                Debug.WriteLine(prompt);
            }
            
            try
            {
                string response = await _aiService.SendPrompt(prompt, false);

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
