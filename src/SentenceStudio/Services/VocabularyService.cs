using System.Diagnostics;
using SentenceStudio.Models;
using SQLite;
using SentenceStudio.Common;
using Scriban;

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
            result = await Database.CreateTablesAsync<VocabularyList, Term>();
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

    public async Task<List<VocabularyList>> GetAllListsWithTermsAsync()
    {
        await Init();
        
        var vocabularyLists = await Database.Table<VocabularyList>().ToListAsync();
        
        foreach (var vocabularyList in vocabularyLists)
        {
            vocabularyList.Terms = await Database.Table<Term>().Where(i => i.VocabularyListId == vocabularyList.ID).ToListAsync();
        }
        
        return vocabularyLists;
    }

    public async Task<List<Term>> GetTermsAsync()
    {
        await Init();
        return await Database.Table<Term>().ToListAsync();
    }

    public async Task<VocabularyList> GetListAsync(int id)
    {
        await Init();    
        var vocabularyList = await Database.Table<VocabularyList>().Where(i => i.ID == id).FirstOrDefaultAsync();
        if (vocabularyList != null)
        {
            vocabularyList.Terms = await Database.Table<Term>().Where(i => i.VocabularyListId == vocabularyList.ID).ToListAsync();
        }
        
        return vocabularyList;
    }

    public async Task<Term> GetTermAsync(int id)
    {
        await Init();
        return await Database.Table<Term>().Where(i => i.ID == id).FirstOrDefaultAsync();
    }

    public async Task<int> SaveListAsync(VocabularyList item)
    {
        await Init();
        int result = -1;
        if (item.ID != 0)
        {
            try
            {
                result = await Database.UpdateAsync(item);
                result = await Database.UpdateAllAsync(item.Terms);
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
                result = await Database.InsertAsync(item);
            

                if (item.Terms != null)
                {
                    foreach (var term in item.Terms)
                    {
                        term.VocabularyListId = item.ID;
                        await SaveTermAsync(term);
                    }
                }
            }
            catch (Exception ex)
            {
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }

        return item.ID;
    }

    public async Task<int> DeleteListAsync(VocabularyList item)
    {
        await Init();
        return await Database.DeleteAsync(item);
    }

    public async Task<int> SaveTermAsync(Term item)
    {
        await Init();
        int result = -1;
        if (item.ID != 0)
        {
            try
            {
                result = await Database.UpdateAsync(item);
            }catch(Exception ex)
            {
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }
        else
        {
            try
            {
                result = await Database.InsertAsync(item);
            }
            catch (Exception ex)
            {
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }

        return result;
    }

    public async Task<int> DeleteTermAsync(Term item)
    {
        await Init();
        return await Database.DeleteAsync(item);
    }


    public async Task SaveTermsAsync(int listId, List<Term> listTerms)
    {
        await Init();
        foreach (var term in listTerms)
        {
            term.VocabularyListId = listId;
            await SaveTermAsync(term);
        }
    }

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
                list.Terms = Term.ParseTerms(response);
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
