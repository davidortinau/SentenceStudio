using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

public class SkillProfileRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncService? _syncService;
    private readonly ILogger<SkillProfileRepository> _logger;

    public SkillProfileRepository(IServiceProvider serviceProvider, ILogger<SkillProfileRepository> logger, ISyncService? syncService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = syncService;
    }

    public async Task<List<SkillProfile>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SkillProfiles.ToListAsync();
    }

    public async Task<List<SkillProfile>> GetSkillsByLanguageAsync(string language)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SkillProfiles.Where(s => s.Language == language).ToListAsync();
    }

    public async Task<int> SaveAsync(SkillProfile item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            // Set timestamps
            if (item.CreatedAt == default)
                item.CreatedAt = DateTime.UtcNow;

            item.UpdatedAt = DateTime.UtcNow;

            if (item.Id != 0)
            {
                db.SkillProfiles.Update(item);
            }
            else
            {
                db.SkillProfiles.Add(item);
            }

            int result = await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return item.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SaveAsync");
            return -1;
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

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in DeleteAsync");
            return -1;
        }
    }

    public async Task<SkillProfile?> GetSkillProfileAsync(int skillID)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SkillProfiles.Where(s => s.Id == skillID).FirstOrDefaultAsync();
    }

    public async Task<SkillProfile?> GetAsync(int skillId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SkillProfiles.FirstOrDefaultAsync(s => s.Id == skillId);
    }
}
