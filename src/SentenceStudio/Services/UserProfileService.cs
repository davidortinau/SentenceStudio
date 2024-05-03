using System.Diagnostics;
using SentenceStudio.Models;
using SQLite;
using SentenceStudio.Common;

namespace SentenceStudio.Services;

public class UserProfileService
{
    private SQLiteAsyncConnection Database;

    public UserProfileService
()
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
            result = await Database.CreateTableAsync<UserProfile>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
            await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
        }
    }

    public async Task<List<UserProfile>> ListAsync()
    {
        await Init();
        return await Database.Table<UserProfile>().ToListAsync();
    }

    public async Task<UserProfile> GetAsync()
    {
        await Init();
        return await Database.Table<UserProfile>().FirstOrDefaultAsync();
    }

    public async Task<int> SaveAsync(UserProfile item)
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
    
    public async Task<int> DeleteAsync()
    {
        await Init();
        return await Database.DeleteAllAsync<UserProfile>();
    }
    
    public async Task<int> DeleteAsync(UserProfile item)
    {
        await Init();
        return await Database.DeleteAsync(item);
    }

    public async Task SaveDisplayCultureAsync(string culture)
    {
        culture = (culture == "en") ? "English" : "Korean";

        var profile = await GetAsync();
        if (profile is null)
        {
            profile = new UserProfile
            {
                DisplayLanguage = culture
            };
        }
        else
        {
            profile.DisplayLanguage = culture;
        }

        await SaveAsync(profile);
    }
}
