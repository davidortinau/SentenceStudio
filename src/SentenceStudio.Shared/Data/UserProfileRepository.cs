using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

public class UserProfileRepository
{
    private readonly IServiceProvider _serviceProvider;
    private ISyncService _syncService;
    private readonly ILogger<UserProfileRepository> _logger;
    private readonly SentenceStudio.Abstractions.IPreferencesService _preferences;

    /// <summary>Preference key storing the active profile's ID (set during login).</summary>
    public const string ActiveProfileIdKey = "active_profile_id";

    public UserProfileRepository(IServiceProvider serviceProvider, ILogger<UserProfileRepository> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = serviceProvider.GetService<ISyncService>();
        _preferences = serviceProvider.GetService<SentenceStudio.Abstractions.IPreferencesService>();
    }

    public async Task<List<UserProfile>> ListAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.UserProfiles.ToListAsync();
    }

    private static bool _schemaEnsured;

    /// <summary>
    /// Ensures multi-user schema columns exist. Called once per app session.
    /// </summary>
    public async Task EnsureMultiUserSchemaAsync()
    {
        if (_schemaEnsured) return;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await EnsureUserProfileIdColumnsAsync(db);
        _schemaEnsured = true;
    }

    public async Task<UserProfile> GetAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync(); // Apply any pending migrations

        // Ensure performance indexes exist (CREATE IF NOT EXISTS is idempotent)
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_VocabularyWord_TargetLanguageTerm ON VocabularyWord(TargetLanguageTerm)");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_VocabularyWord_NativeLanguageTerm ON VocabularyWord(NativeLanguageTerm)");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ResourceVocabularyMapping_VocabularyWordId ON ResourceVocabularyMapping(VocabularyWordId)");

        // Add UserProfileId columns for multi-user data isolation (idempotent)
        await EnsureMultiUserSchemaAsync();

        // Load the active profile if one was selected during login
        UserProfile profile = null;
        var activeId = _preferences?.Get("active_profile_id", 0) ?? 0;
        if (activeId > 0)
        {
            profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == activeId);
        }

        // Fall back to first profile if active profile not found
        profile ??= await db.UserProfiles.FirstOrDefaultAsync();

        // Ensure DisplayLanguage is never null or empty for existing profiles
        if (profile != null && string.IsNullOrEmpty(profile.DisplayLanguage))
        {
            profile.DisplayLanguage = "English";
        }

        return profile; // Return null if no profile exists
    }

    private static async Task EnsureUserProfileIdColumnsAsync(ApplicationDbContext db)
    {
        // SQLite: add nullable UserProfileId columns if they don't exist
        // Table names are SINGULAR (as configured in ApplicationDbContext.OnModelCreating)
        var tables = new[] { "SkillProfile", "LearningResource", "UserActivity" };
        foreach (var table in tables)
        {
            try
            {
                // PRAGMA table_info returns column details; check if UserProfileId exists
                var cols = await db.Database.SqlQueryRaw<string>(
                    $"SELECT name FROM pragma_table_info('{table}') WHERE name = 'UserProfileId'").ToListAsync();
                if (cols.Count == 0)
                {
                    await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {table} ADD COLUMN UserProfileId INTEGER");
                }
            }
            catch { /* column already exists or table doesn't exist yet */ }
        }

        // Backfill: assign unowned SkillProfiles by matching Language → UserProfile.TargetLanguage
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE SkillProfile SET UserProfileId = (
                SELECT UP.Id FROM UserProfile UP WHERE UP.TargetLanguage = SkillProfile.Language LIMIT 1
            ) WHERE UserProfileId IS NULL");

        // Backfill: assign unowned LearningResources by matching Language → UserProfile.TargetLanguage
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE LearningResource SET UserProfileId = (
                SELECT UP.Id FROM UserProfile UP WHERE UP.TargetLanguage = LearningResource.Language LIMIT 1
            ) WHERE UserProfileId IS NULL");

        // Backfill: assign unowned UserActivities to first profile
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE UserActivity SET UserProfileId = (
                SELECT Id FROM UserProfile ORDER BY Id LIMIT 1
            ) WHERE UserProfileId IS NULL");
    }

    public async Task<UserProfile> GetOrCreateDefaultAsync()
    {
        var profile = await GetAsync();

        // Provide defaults for a new or invalid profile
        if (profile == null)
        {
            profile = new UserProfile
            {
                Name = string.Empty,
                Email = string.Empty,
                NativeLanguage = "English",
                TargetLanguage = "Korean",
                DisplayLanguage = "English",
                OpenAI_APIKey = string.Empty
            };
        }

        return profile;
    }

    public async Task<int> SaveAsync(UserProfile item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Set timestamps
        if (item.CreatedAt == default)
            item.CreatedAt = DateTime.UtcNow;

        try
        {
            if (item.Id > 0)
            {
                db.UserProfiles.Update(item);
            }
            else
            {
                db.UserProfiles.Add(item);
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
                // UXDivers popup removed - error already logged above
            }
            return -1;
        }
    }

    public async Task<int> DeleteAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var profiles = await db.UserProfiles.ToListAsync();
            db.UserProfiles.RemoveRange(profiles);
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

    public async Task<int> DeleteAsync(UserProfile item)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            db.UserProfiles.Remove(item);
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

    public async Task SaveDisplayCultureAsync(string culture)
    {
        // Map culture code to display language
        string displayLanguage = culture.StartsWith("en") ? "English" : "Korean";

        var profile = await GetAsync();
        if (profile is null)
        {
            profile = new UserProfile
            {
                DisplayLanguage = displayLanguage
            };
        }
        else
        {
            profile.DisplayLanguage = displayLanguage;
        }

        await SaveAsync(profile);

        // Also update the LocalizationManager to reflect changes immediately
        LocalizationManager.Instance.SetCulture(new CultureInfo(culture));
    }
}
