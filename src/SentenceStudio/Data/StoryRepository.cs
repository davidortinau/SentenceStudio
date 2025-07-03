using System.Diagnostics;
using SentenceStudio.Shared.Models;
using SQLite;
using SentenceStudio.Common;

namespace SentenceStudio.Data;

public class StoryRepository
{
    private SQLiteAsyncConnection Database;

    public StoryRepository()
    {
        
    }

    async Task Init()
    {
        if (Database is not null)
            return;

        Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

        CreateTablesResult result;
        
        try
        {
            result = await Database.CreateTablesAsync<Story,Question>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
        }
    }

    public async Task<List<Story>> ListAsync()
    {
        await Init();
        return await Database.Table<Story>().ToListAsync();
    }

    public async Task<Story> GetStory(int storyID)
    {
        await Init();
        Story s = await Database.Table<Story>().Where(i => i.ID == storyID).FirstOrDefaultAsync();
        if (s != null)
        {
            s.Questions = await Database.Table<Question>().Where(i => i.StoryID == storyID).ToListAsync();
        }
        return s;
    }

    public async Task<int> SaveAsync(Story item)
    {
        await Init();
        int result = -1;
        if (item.ID != 0)
        {
            try
            {
                result = await Database.UpdateAsync(item);
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

                if (item.Questions != null)
                {
                    foreach (var question in item.Questions)
                    {
                        try
                        {
                            question.StoryID = item.ID;
                            await Database.InsertAsync(question);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"{ex.Message}");
                            await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }       

        return result;
    }    

    public async Task<int> DeleteAsync(Story item)
    {
        await Init();
        return await Database.DeleteAsync(item);
    }    
}
