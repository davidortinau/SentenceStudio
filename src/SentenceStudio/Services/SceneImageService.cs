using System.Diagnostics;
using SentenceStudio.Shared.Models;
using SQLite;
using SentenceStudio.Common;

namespace SentenceStudio.Services;

public class SceneImageService
{
    private SQLiteAsyncConnection Database;

    public SceneImageService()
    {
        
    }

    async Task Init()
    {
        if (Database is not null)
            return;

        Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

        CreateTableResult result;
        
        try
        {
            result = await Database.CreateTableAsync<SceneImage>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
        }
    }

    public async Task<List<SceneImage>> ListAsync()
    {
        await Init();
        return await Database.Table<SceneImage>().ToListAsync();
    }

    public async Task<SceneImage> GetAsync(string url)
    {
        await Init();
        return await Database.Table<SceneImage>().Where(i => i.Url == url).FirstOrDefaultAsync();
    }

    public async Task<int> SaveAsync(SceneImage item)
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
            }
            catch (Exception ex)
            {
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }

        return result;
    }
    

    public async Task<int> DeleteAsync(SceneImage item)
    {
        await Init();
        return await Database.DeleteAsync(item);
    }

    public async Task<SceneImage> GetRandomAsync()
    {
        await Init();
        var images = await Database.Table<SceneImage>().ToListAsync();
        if (images.Count > 0)
        {
            var random = new Random();
            var index = random.Next(0, images.Count);
            var image = images[index];
            return image;
        }else{
            return null;
        }
    }
}
