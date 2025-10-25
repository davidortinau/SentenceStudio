using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

public class UserActivityRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly SentenceStudio.Services.Progress.ProgressCacheService? _cacheService;

    public UserActivityRepository(IServiceProvider serviceProvider, ISyncService? syncService = null, SentenceStudio.Services.Progress.ProgressCacheService? cacheService = null)
    {
        _serviceProvider = serviceProvider;
        _syncService = syncService;
        _cacheService = cacheService;
    }

    public async Task<List<UserActivity>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.UserActivities.ToListAsync();
    }

    public async Task<List<UserActivity>> GetAsync(SentenceStudio.Shared.Models.Activity activity)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.UserActivities.Where(i => i.Activity == activity.ToString()).ToListAsync();
    }

    public async Task<List<UserActivity>> GetByDateRangeAsync(DateTime fromUtc, DateTime toUtc)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.UserActivities
            .Where(a => a.CreatedAt >= fromUtc && a.CreatedAt <= toUtc)
            .ToListAsync();
    }

    public async Task<int> SaveAsync(UserActivity item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            if (item.Id != 0)
            {
                db.UserActivities.Update(item);
            }
            else
            {
                db.UserActivities.Add(item);
            }

            int result = await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            // PHASE 2 OPTIMIZATION: Invalidate progress cache when new activities are recorded
            _cacheService?.InvalidateAll();

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

    public async Task<int> DeleteAsync(UserActivity item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            db.UserActivities.Remove(item);
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
