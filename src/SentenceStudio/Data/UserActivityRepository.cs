using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

namespace SentenceStudio.Data;

public class UserActivityRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly SentenceStudio.Services.Progress.ProgressCacheService? _cacheService;
    private readonly ILogger<UserActivityRepository> _logger;

    public UserActivityRepository(IServiceProvider serviceProvider, ILogger<UserActivityRepository> logger, ISyncService? syncService = null, SentenceStudio.Services.Progress.ProgressCacheService? cacheService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
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

            // PHASE 2 OPTIMIZATION: Invalidate relevant caches (but NOT TodaysPlan!)
            // User activities affect vocab summary and practice heat, but not the plan structure
            _cacheService?.InvalidateVocabSummary();
            _cacheService?.InvalidatePracticeHeat();

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
            _logger.LogError(ex, "Error occurred in DeleteAsync");
            return -1;
        }
    }
}
