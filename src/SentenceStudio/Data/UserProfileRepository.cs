using System.Diagnostics;
using SentenceStudio.Shared.Models;
using SQLite;
using SentenceStudio.Common;
using System.Globalization;

namespace SentenceStudio.Data;

public class UserProfileRepository
{
    private SQLiteAsyncConnection Database;

    public UserProfileRepository
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
        var profile = await Database.Table<UserProfile>().FirstOrDefaultAsync();
        
        // Provide defaults for a new or invalid profile
        if (profile == null)
        {
            profile = new UserProfile 
            { 
                Name = string.Empty,
                Email = string.Empty,
                NativeLanguage = "English",
                TargetLanguage = "Korean",
                DisplayLanguage = "English",
                OpenAI_APIKey = string.Empty
            };
        }
        
        // Ensure DisplayLanguage is never null or empty
        if (string.IsNullOrEmpty(profile.DisplayLanguage))
        {
            profile.DisplayLanguage = "English";
        }
        
        return profile;
    }

    public async Task<int> SaveAsync(UserProfile item)
    {
        await Init();
        int result = 0;
        if (item.ID > 0)
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
        // Map culture code to display language
        string displayLanguage = culture.StartsWith("en") ? "English" : "Korean";

        var profile = await GetAsync();
        if (profile is null)
        {
            profile = new UserProfile
            {
                DisplayLanguage = displayLanguage
            };
        }
        else
        {
            profile.DisplayLanguage = displayLanguage;
        }

        await SaveAsync(profile);
        
        // Also update the LocalizationManager to reflect changes immediately
        LocalizationManager.Instance.SetCulture(new CultureInfo(culture));
    }
}
