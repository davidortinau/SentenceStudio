using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Services;

public class SceneImageService
{
    private readonly IServiceProvider _serviceProvider;
    private ISyncService _syncService;

    public SceneImageService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _syncService = serviceProvider.GetService<ISyncService>();
    }

    public async Task<List<SceneImage>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SceneImages.ToListAsync();
    }

    public async Task<SceneImage> GetAsync(string url)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SceneImages.Where(i => i.Url == url).FirstOrDefaultAsync();
    }

    public async Task<int> SaveAsync(SceneImage item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            if (item.Id != 0)
            {
                db.SceneImages.Update(item);
            }
            else
            {
                db.SceneImages.Add(item);
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
    
    public async Task<int> DeleteAsync(SceneImage item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        try
        {
            db.SceneImages.Remove(item);
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

    public async Task<SceneImage> GetRandomAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var images = await db.SceneImages.ToListAsync();
        if (images.Count > 0)
        {
            var random = new Random();
            var index = random.Next(0, images.Count);
            var image = images[index];
            return image;
        }
        else
        {
            return null;
        }
    }
}
