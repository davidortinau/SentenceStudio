using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

public class UserProfileRepository
{
    private readonly IServiceProvider _serviceProvider;
    private ISyncService _syncService;

    public UserProfileRepository(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _syncService = serviceProvider.GetService<ISyncService>();
    }

    public async Task<List<UserProfile>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.UserProfiles.ToListAsync();
    }

    public async Task<UserProfile> GetAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync(); // TODO find a more reliable place to call this, if needed. It is one of the first data calls in every app run right now
        var profile = await db.UserProfiles.FirstOrDefaultAsync();
        
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
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Set timestamps
        if (item.CreatedAt == default)
            item.CreatedAt = DateTime.UtcNow;
        
        try
        {
            if (item.Id > 0)
            {
                db.UserProfiles.Update(item);
            }
            else
            {
                db.UserProfiles.Add(item);
            }
            
            int result = await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred SaveAsync: {ex.Message}");
            if (item.Id == 0)
            {
                await App.Current.MainPage.DisplayAlert("Error", ex.Message, "Fix it");
            }
            return -1;
        }
    }
    
    public async Task<int> DeleteAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            var profiles = await db.UserProfiles.ToListAsync();
            db.UserProfiles.RemoveRange(profiles);
            int result = await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred DeleteAsync: {ex.Message}");
            return -1;
        }
    }
    
    public async Task<int> DeleteAsync(UserProfile item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            db.UserProfiles.Remove(item);
            int result = await db.SaveChangesAsync();
            
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred DeleteAsync: {ex.Message}");
            return -1;
        }
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
