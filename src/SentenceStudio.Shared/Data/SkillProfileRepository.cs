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

    private string ResolveUserId(string? userProfileId) =>
        !string.IsNullOrEmpty(userProfileId) ? userProfileId : ActiveUserId;

    /// <summary>
    /// Lists skill profiles. When <paramref name="userProfileId"/> is provided
    /// it scopes the results to that profile (required on multi-user hosts
    /// like the API where <c>IPreferencesService</c> isn't registered).
    /// </summary>
    public async Task<List<SkillProfile>> ListAsync(string? userProfileId = null)
    {
        var userId = !string.IsNullOrEmpty(userProfileId) ? userProfileId : ActiveUserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("SkillProfileRepository.ListAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<SkillProfile>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SkillProfiles.Where(s => s.UserProfileId == userId).ToListAsync();
    }

    public async Task<List<SkillProfile>> GetSkillsByLanguageAsync(string language)
    {
        var userId = ActiveUserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("SkillProfileRepository.GetSkillsByLanguageAsync called without an active user — returning empty result to prevent cross-tenant data leak.");
            return new List<SkillProfile>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SkillProfiles
            .Where(s => s.Language == language)
            .Where(s => s.UserProfileId == userId)
            .ToListAsync();
    }

    public async Task<string> SaveAsync(SkillProfile item, string? userProfileId = null)
    {
        var userId = ResolveUserId(userProfileId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("SkillProfileRepository.SaveAsync called without an active user — refusing write to prevent cross-tenant data changes.");
            return string.Empty;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            // Set timestamps
            if (item.CreatedAt == default)
                item.CreatedAt = DateTime.UtcNow;

            item.UpdatedAt = DateTime.UtcNow;

            var existing = await db.SkillProfiles
                .FirstOrDefaultAsync(p => p.Id == item.Id && p.UserProfileId == userId);
            if (existing is null
                && await db.SkillProfiles.AnyAsync(p => p.Id == item.Id))
            {
                _logger.LogWarning("SkillProfileRepository.SaveAsync refused an unowned skill profile.");
                return string.Empty;
            }

            item.UserProfileId = userId;
            if (existing is not null)
            {
                db.Entry(existing).CurrentValues.SetValues(item);
                existing.UserProfileId = userId;
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
            return string.Empty;
        }
    }

    public async Task<int> DeleteAsync(SkillProfile item, string? userProfileId = null)
    {
        var userId = ResolveUserId(userProfileId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("SkillProfileRepository.DeleteAsync called without an active user — refusing delete to prevent cross-tenant data changes.");
            return 0;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var owned = await db.SkillProfiles
                .FirstOrDefaultAsync(s => s.Id == item.Id && s.UserProfileId == userId);
            if (owned is null)
            {
                _logger.LogWarning("SkillProfileRepository.DeleteAsync refused a missing or unowned skill profile.");
                return 0;
            }

            db.SkillProfiles.Remove(owned);
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

    public async Task<SkillProfile?> GetSkillProfileAsync(string skillID, string? userProfileId = null)
    {
        var userId = ResolveUserId(userProfileId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("SkillProfileRepository.GetSkillProfileAsync called without an active user — returning null to prevent cross-tenant data leak.");
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var skill = await db.SkillProfiles
            .FirstOrDefaultAsync(s => s.Id == skillID && s.UserProfileId == userId);
        if (skill is null)
        {
            _logger.LogWarning("SkillProfileRepository.GetSkillProfileAsync refused a missing or unowned skill profile.");
        }

        return skill;
    }

    public async Task<SkillProfile?> GetAsync(string skillId, string? userProfileId = null)
    {
        var userId = ResolveUserId(userProfileId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("SkillProfileRepository.GetAsync called without an active user — returning null to prevent cross-tenant data leak.");
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var skill = await db.SkillProfiles
            .FirstOrDefaultAsync(s => s.Id == skillId && s.UserProfileId == userId);
        if (skill is null)
        {
            _logger.LogWarning("SkillProfileRepository.GetAsync refused a missing or unowned skill profile.");
        }

        return skill;
    }
}
