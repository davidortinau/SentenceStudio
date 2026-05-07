using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

public class SkillProfileRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<SkillProfileRepository> _logger;
    private readonly SentenceStudio.Abstractions.IPreferencesService? _preferences;

    public SkillProfileRepository(IServiceProvider serviceProvider, ILogger<SkillProfileRepository> logger, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
        _preferences = serviceProvider.GetService<SentenceStudio.Abstractions.IPreferencesService>();
    }

    private string ActiveUserId => _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;

    public async Task<List<SkillProfile>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        if (!string.IsNullOrEmpty(userId))
            return await db.SkillProfiles.Where(s => s.UserProfileId == userId).ToListAsync();
        return await db.SkillProfiles.ToListAsync();
    }

    public async Task<List<SkillProfile>> GetSkillsByLanguageAsync(string language)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = ActiveUserId;
        var query = db.SkillProfiles.Where(s => s.Language == language);
        if (!string.IsNullOrEmpty(userId))
            query = query.Where(s => s.UserProfileId == userId);
        return await query.ToListAsync();
    }

    public async Task<string> SaveAsync(SkillProfile item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            // Set timestamps
            if (item.CreatedAt == default)
                item.CreatedAt = DateTime.UtcNow;

            item.UpdatedAt = DateTime.UtcNow;

            // Ensure UserProfileId is set for new items
            if (string.IsNullOrEmpty(item.UserProfileId))
                item.UserProfileId = !string.IsNullOrEmpty(ActiveUserId) ? ActiveUserId : null;

            // With GUID PKs, Id is always pre-set by the model constructor.
            // Check DB existence to determine Add vs Update.
            var exists = !string.IsNullOrEmpty(item.Id)
                && await db.SkillProfiles.AnyAsync(p => p.Id == item.Id);

            if (exists)
            {
                db.SkillProfiles.Update(item);
            }
            else
            {
                db.SkillProfiles.Add(item);
            }

            int result = await db.SaveChangesAsync();

            TriggerSync();

            return item.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SaveAsync");
            return string.Empty;
        }
    }

    public async Task<int> DeleteAsync(SkillProfile item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            db.SkillProfiles.Remove(item);
            int result = await db.SaveChangesAsync();

            TriggerSync();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in DeleteAsync");
            return -1;
        }
    }

    public async Task<SkillProfile?> GetSkillProfileAsync(string skillID)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SkillProfiles.Where(s => s.Id == skillID).FirstOrDefaultAsync();
    }

    public async Task<SkillProfile?> GetAsync(string skillId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SkillProfiles.FirstOrDefaultAsync(s => s.Id == skillId);
    }
    private void TriggerSync()
    {
        if (_syncService is null) return;
        _ = Task.Run(async () =>
        {
            try { await _syncService.TriggerSyncAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Background sync trigger failed"); }
        });
    }

}
