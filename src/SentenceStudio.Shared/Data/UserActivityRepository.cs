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

    private string ActiveUserId => _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;

    public async Task<List<UserActivity>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        if (!string.IsNullOrEmpty(userId))
            return await db.UserActivities.Where(a => a.UserProfileId == userId).ToListAsync();
        return await db.UserActivities.ToListAsync();
    }

    public async Task<List<UserActivity>> GetAsync(SentenceStudio.Shared.Models.Activity activity)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        var query = db.UserActivities.Where(i => i.Activity == activity.ToString());
        if (!string.IsNullOrEmpty(userId))
            query = query.Where(a => a.UserProfileId == userId);
        return await query.ToListAsync();
    }

    public async Task<List<UserActivity>> GetByDateRangeAsync(DateTime fromUtc, DateTime toUtc)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        var query = db.UserActivities.Where(a => a.CreatedAt >= fromUtc && a.CreatedAt <= toUtc);
        if (!string.IsNullOrEmpty(userId))
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
            if (string.IsNullOrEmpty(item.UserProfileId) && !string.IsNullOrEmpty(ActiveUserId))
                item.UserProfileId = ActiveUserId;

            // With GUID PKs, Id is always pre-set. Check DB existence to determine Add vs Update.
            var exists = !string.IsNullOrEmpty(item.Id)
                && await db.UserActivities.AnyAsync(a => a.Id == item.Id);

            if (exists)
            {
                db.UserActivities.Update(item);
            }
            else
            {
                // Ensure new records have a GUID
                if (string.IsNullOrEmpty(item.Id))
                    item.Id = Guid.NewGuid().ToString();
                
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
