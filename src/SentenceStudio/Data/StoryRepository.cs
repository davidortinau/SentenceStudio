using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

public class StoryRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;

    public StoryRepository(IServiceProvider serviceProvider, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _syncService = syncService;
    }

    public async Task<List<Story>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Stories.Include(s => s.Questions).ToListAsync();
    }

    public async Task<Story> GetStory(int storyID)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Stories
            .Include(s => s.Questions)
            .Where(s => s.Id == storyID)
            .FirstOrDefaultAsync();
    }

    public async Task<int> SaveAsync(Story item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            if (item.Id != 0)
            {
                db.Stories.Update(item);
            }
            else
            {
                db.Stories.Add(item);
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
                await App.Current.Windows[0].Page.DisplayAlert("Error", ex.Message, "Fix it");
            }
            return -1;
        }
    }

    public async Task<int> DeleteAsync(Story item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            db.Stories.Remove(item);
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
}
