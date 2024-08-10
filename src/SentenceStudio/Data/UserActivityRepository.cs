using System.Diagnostics;
using SentenceStudio.Models;
using SQLite;
using SentenceStudio.Common;

namespace SentenceStudio.Data;

public class UserActivityRepository
{
    private SQLiteAsyncConnection Database;

    public UserActivityRepository()
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
            result = await Database.CreateTableAsync<UserActivity>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await Shell.Current.DisplayAlert("Error", ex.Message, "Fix it");
        }
    }

    public async Task<List<UserActivity>> ListAsync()
    {
        await Init();
        return await Database.Table<UserActivity>().ToListAsync();
    }

    public async Task<List<UserActivity>> GetAsync(Models.Activity activity)
    {
        await Init();
        return await Database.Table<UserActivity>().Where(i => i.Activity == activity.ToString()).ToListAsync();
    }

    public async Task<int> SaveAsync(UserActivity item)
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
                await Shell.Current.DisplayAlert("Error", ex.Message, "Fix it");
            }
        }

        return result;
    }
    

    public async Task<int> DeleteAsync(UserActivity item)
    {
        await Init();
        return await Database.DeleteAsync(item);
    }
}
