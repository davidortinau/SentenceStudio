using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

namespace SentenceStudio.Data;

public class StoryRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<StoryRepository> _logger;

    public StoryRepository(IServiceProvider serviceProvider, ILogger<StoryRepository> logger, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
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
            _logger.LogError(ex, "Error occurred in SaveAsync");
            if (item.Id == 0)
            {
                await IPopupService.Current.PushAsync(new SimpleActionPopup
                {
                    Title = "Error",
                    Text = ex.Message,
                    ActionButtonText = "Fix it",
                    ShowSecondaryActionButton = false
                });
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
            _logger.LogError(ex, "Error occurred in DeleteAsync");
            return -1;
        }
    }
}
