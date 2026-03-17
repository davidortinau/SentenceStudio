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

    private static bool _backfillDone;

    /// <summary>
    /// Backfills UserProfileId on existing rows that predate multi-user support.
    /// Runs once per app session. Column creation is handled by EF migration.
    /// </summary>
    public async Task EnsureMultiUserBackfillAsync()
    {
        if (_backfillDone) return;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await BackfillUserProfileIdsAsync(db);
        _backfillDone = true;
    }

    public async Task<UserProfile> GetAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Ensure performance indexes exist (CREATE IF NOT EXISTS is idempotent)
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_VocabularyWord_TargetLanguageTerm ON VocabularyWord(TargetLanguageTerm)");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_VocabularyWord_NativeLanguageTerm ON VocabularyWord(NativeLanguageTerm)");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ResourceVocabularyMapping_VocabularyWordId ON ResourceVocabularyMapping(VocabularyWordId)");

        // Backfill UserProfileId for existing data (idempotent, runs once per session)
        await EnsureMultiUserBackfillAsync();

        // Load the active profile if one was selected during login
        UserProfile profile = null;
        var activeId = _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;
        if (!string.IsNullOrEmpty(activeId))
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

    private static async Task BackfillUserProfileIdsAsync(ApplicationDbContext db)
    {
        // Check if UserProfileId column exists before attempting backfill.
        // On fresh installs, the column may not exist until MigrateAsync() runs.
        var cols = await db.Database.SqlQueryRaw<string>(
            "SELECT name FROM pragma_table_info('SkillProfile') WHERE name = 'UserProfileId'").ToListAsync();
        if (cols.Count == 0)
            return; // Column doesn't exist yet — skip backfill

        // Assign unowned SkillProfiles by matching Language → UserProfile.TargetLanguage
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE SkillProfile SET UserProfileId = (
                SELECT UP.Id FROM UserProfile UP WHERE UP.TargetLanguage = SkillProfile.Language LIMIT 1
            ) WHERE UserProfileId IS NULL");

        // Assign unowned LearningResources by matching Language → UserProfile.TargetLanguage
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE LearningResource SET UserProfileId = (
                SELECT UP.Id FROM UserProfile UP WHERE UP.TargetLanguage = LearningResource.Language LIMIT 1
            ) WHERE UserProfileId IS NULL");

        // Fall back: assign any remaining unowned LearningResources to first profile
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE LearningResource SET UserProfileId = (
                SELECT Id FROM UserProfile ORDER BY Id LIMIT 1
            ) WHERE UserProfileId IS NULL");

        // Assign unowned UserActivities to first profile
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE UserActivity SET UserProfileId = (
                SELECT Id FROM UserProfile ORDER BY Id LIMIT 1
            ) WHERE UserProfileId IS NULL");

        // Backfill VocabularyWord.Language from associated LearningResources
        var langCol = await db.Database.SqlQueryRaw<string>(
            "SELECT name FROM pragma_table_info('VocabularyWord') WHERE name = 'Language'").ToListAsync();
        if (langCol.Count > 0)
        {
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE VocabularyWord SET Language = (
                    SELECT LR.Language FROM ResourceVocabularyMapping RVM
                    JOIN LearningResource LR ON LR.Id = RVM.ResourceId
                    WHERE RVM.VocabularyWordId = VocabularyWord.Id
                    AND LR.Language IS NOT NULL AND LR.Language != ''
                    LIMIT 1
                ) WHERE Language IS NULL OR Language = ''");
        }
    }

    /// <summary>
    /// One-time compat fix: if UserProfileId columns were added via raw SQL before the EF migration
    /// existed, mark the migration as applied so MigrateAsync() doesn't try to add them again.
    /// </summary>
    private static bool _migrationChecked;
    private static async Task MarkMigrationIfColumnsExistAsync(ApplicationDbContext db)
    {
        if (_migrationChecked) return;
        _migrationChecked = true;

        const string migrationId = "20260302040632_AddUserProfileIdToEntities";
        try
        {
            // Check if the column already exists but the migration hasn't been recorded
            var cols = await db.Database.SqlQueryRaw<string>(
                "SELECT name FROM pragma_table_info('SkillProfile') WHERE name = 'UserProfileId'").ToListAsync();
            if (cols.Count > 0)
            {
                // Column exists — check if migration is already recorded
                var applied = await db.Database.SqlQueryRaw<string>(
                    $"SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId = '{migrationId}'").ToListAsync();
                if (applied.Count == 0)
                {
                    // Column exists but migration not recorded — mark it as applied
                    await db.Database.ExecuteSqlRawAsync(
                        $"INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('{migrationId}', '10.0.2')");
                }
            }
        }
        catch { /* __EFMigrationsHistory may not exist yet on fresh databases */ }
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
            // With GUID PKs, Id is always pre-set by the model constructor.
            // Check DB existence to determine Add vs Update.
            var exists = !string.IsNullOrEmpty(item.Id)
                && await db.UserProfiles.AnyAsync(p => p.Id == item.Id);

            if (exists)
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
            if (string.IsNullOrEmpty(item.Id))
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
