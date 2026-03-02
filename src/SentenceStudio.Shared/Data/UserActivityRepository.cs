using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

public class UserActivityRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly SentenceStudio.Services.Progress.ProgressCacheService? _cacheService;
    private readonly ILogger<UserActivityRepository> _logger;
    private readonly SentenceStudio.Abstractions.IPreferencesService? _preferences;

    public UserActivityRepository(IServiceProvider serviceProvider, ILogger<UserActivityRepository> logger, ISyncService? syncService = null, SentenceStudio.Services.Progress.ProgressCacheService? cacheService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
        _cacheService = cacheService;
        _preferences = serviceProvider.GetService<SentenceStudio.Abstractions.IPreferencesService>();
    }

    private int ActiveUserId => _preferences?.Get("active_profile_id", 0) ?? 0;

    public async Task<List<UserActivity>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        if (userId > 0)
            return await db.UserActivities.Where(a => a.UserProfileId == userId).ToListAsync();
        return await db.UserActivities.ToListAsync();
    }

    public async Task<List<UserActivity>> GetAsync(SentenceStudio.Shared.Models.Activity activity)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        var query = db.UserActivities.Where(i => i.Activity == activity.ToString());
        if (userId > 0)
            query = query.Where(a => a.UserProfileId == userId);
        return await query.ToListAsync();
    }

    public async Task<List<UserActivity>> GetByDateRangeAsync(DateTime fromUtc, DateTime toUtc)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        var query = db.UserActivities.Where(a => a.CreatedAt >= fromUtc && a.CreatedAt <= toUtc);
        if (userId > 0)
            query = query.Where(a => a.UserProfileId == userId);
        return await query.ToListAsync();
    }

    public async Task<int> SaveAsync(UserActivity item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            // Ensure UserProfileId is set for new items
            if ((item.UserProfileId == null || item.UserProfileId == 0) && ActiveUserId > 0)
                item.UserProfileId = ActiveUserId;

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
                // UXDivers popup removed - error already logged above
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
