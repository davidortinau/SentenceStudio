using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Data;

public class UserProfileRepository
{
    private readonly IServiceProvider _serviceProvider;
    private ISyncService _syncService;
    private readonly ILogger<UserProfileRepository> _logger;
    private readonly IActiveUserProvider? _activeUserProvider;

    /// <summary>Preference key storing the active profile's ID (set during login).</summary>
    public const string ActiveProfileIdKey = "active_profile_id";

    public UserProfileRepository(IServiceProvider serviceProvider, ILogger<UserProfileRepository> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = serviceProvider.GetService<ISyncService>();
        _activeUserProvider = serviceProvider.GetService<IActiveUserProvider>();
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
        // PostgreSQL requires quoted identifiers for PascalCase table/column names
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_VocabularyWord_TargetLanguageTerm\" ON \"VocabularyWord\"(\"TargetLanguageTerm\")");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_VocabularyWord_NativeLanguageTerm\" ON \"VocabularyWord\"(\"NativeLanguageTerm\")");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_ResourceVocabularyMapping_VocabularyWordId\" ON \"ResourceVocabularyMapping\"(\"VocabularyWordId\")");

        // Backfill UserProfileId for existing data (idempotent, runs once per session)
        await EnsureMultiUserBackfillAsync();

        // Load the active profile using the host-appropriate provider
        // (MAUI: device preferences, WebApp: authenticated claims)
        UserProfile profile = null;
        var activeId = _activeUserProvider?.GetActiveProfileId() ?? string.Empty;
        if (!string.IsNullOrEmpty(activeId))
        {
            profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == activeId);
        }

        // Fall back to first profile ONLY on single-user hosts (MAUI).
        // On the server, returning null forces re-authentication instead of
        // leaking another user's data.
        if (profile is null && (_activeUserProvider?.ShouldFallbackToFirstProfile ?? true))
        {
            profile = await db.UserProfiles.FirstOrDefaultAsync();
        }

        // Ensure DisplayLanguage is never null or empty for existing profiles
        if (profile != null && string.IsNullOrEmpty(profile.DisplayLanguage))
        {
            profile.DisplayLanguage = "English";
        }

        return profile; // Return null if no profile exists
    }

    private static async Task BackfillUserProfileIdsAsync(ApplicationDbContext db)
    {
        bool isSqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        // Quote identifiers for PostgreSQL (PascalCase requires quoting); SQLite is case-insensitive
        string q(string id) => isSqlite ? id : $"\"{id}\"";

        // Check if UserProfileId column exists before attempting backfill.
        bool hasColumn;
        if (isSqlite)
        {
            var cols = await db.Database.SqlQueryRaw<string>(
                "SELECT name FROM pragma_table_info('SkillProfile') WHERE name = 'UserProfileId'").ToListAsync();
            hasColumn = cols.Count > 0;
        }
        else
        {
            var cols = await db.Database.SqlQueryRaw<string>(
                "SELECT column_name FROM information_schema.columns WHERE table_name = 'SkillProfile' AND column_name = 'UserProfileId'").ToListAsync();
            hasColumn = cols.Count > 0;
        }
        if (!hasColumn)
            return; // Column doesn't exist yet — skip backfill

        // Assign unowned SkillProfiles by matching Language → UserProfile.TargetLanguage
        await db.Database.ExecuteSqlRawAsync($@"
            UPDATE {q("SkillProfile")} SET {q("UserProfileId")} = (
                SELECT UP.{q("Id")} FROM {q("UserProfile")} UP WHERE UP.{q("TargetLanguage")} = {q("SkillProfile")}.{q("Language")} LIMIT 1
            ) WHERE {q("UserProfileId")} IS NULL");

        // Assign unowned LearningResources by matching Language → UserProfile.TargetLanguage
        await db.Database.ExecuteSqlRawAsync($@"
            UPDATE {q("LearningResource")} SET {q("UserProfileId")} = (
                SELECT UP.{q("Id")} FROM {q("UserProfile")} UP WHERE UP.{q("TargetLanguage")} = {q("LearningResource")}.{q("Language")} LIMIT 1
            ) WHERE {q("UserProfileId")} IS NULL");

        // Fall back: assign any remaining unowned LearningResources to first profile
        await db.Database.ExecuteSqlRawAsync($@"
            UPDATE {q("LearningResource")} SET {q("UserProfileId")} = (
                SELECT {q("Id")} FROM {q("UserProfile")} ORDER BY {q("Id")} LIMIT 1
            ) WHERE {q("UserProfileId")} IS NULL");

        // Assign unowned UserActivities to first profile
        await db.Database.ExecuteSqlRawAsync($@"
            UPDATE {q("UserActivity")} SET {q("UserProfileId")} = (
                SELECT {q("Id")} FROM {q("UserProfile")} ORDER BY {q("Id")} LIMIT 1
            ) WHERE {q("UserProfileId")} IS NULL");

        // Backfill VocabularyWord.Language from associated LearningResources
        bool hasLangCol;
        if (isSqlite)
        {
            var langCol = await db.Database.SqlQueryRaw<string>(
                "SELECT name FROM pragma_table_info('VocabularyWord') WHERE name = 'Language'").ToListAsync();
            hasLangCol = langCol.Count > 0;
        }
        else
        {
            var langCol = await db.Database.SqlQueryRaw<string>(
                "SELECT column_name FROM information_schema.columns WHERE table_name = 'VocabularyWord' AND column_name = 'Language'").ToListAsync();
            hasLangCol = langCol.Count > 0;
        }
        if (hasLangCol)
        {
            await db.Database.ExecuteSqlRawAsync($@"
                UPDATE {q("VocabularyWord")} SET {q("Language")} = (
                    SELECT LR.{q("Language")} FROM {q("ResourceVocabularyMapping")} RVM
                    JOIN {q("LearningResource")} LR ON LR.{q("Id")} = RVM.{q("ResourceId")}
                    WHERE RVM.{q("VocabularyWordId")} = {q("VocabularyWord")}.{q("Id")}
                    AND LR.{q("Language")} IS NOT NULL AND LR.{q("Language")} != ''
                    LIMIT 1
                ) WHERE {q("Language")} IS NULL OR {q("Language")} = ''");
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
            bool isSqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

            // Check if the column already exists but the migration hasn't been recorded
            bool hasColumn;
            if (isSqlite)
            {
                var cols = await db.Database.SqlQueryRaw<string>(
                    "SELECT name FROM pragma_table_info('SkillProfile') WHERE name = 'UserProfileId'").ToListAsync();
                hasColumn = cols.Count > 0;
            }
            else
            {
                var cols = await db.Database.SqlQueryRaw<string>(
                    "SELECT column_name FROM information_schema.columns WHERE table_name = 'SkillProfile' AND column_name = 'UserProfileId'").ToListAsync();
                hasColumn = cols.Count > 0;
            }
            if (hasColumn)
            {
                // Column exists — check if migration is already recorded
                string q(string id) => isSqlite ? id : $"\"{id}\"";
                var applied = await db.Database.SqlQueryRaw<string>(
                    $"SELECT {q("MigrationId")} FROM {q("__EFMigrationsHistory")} WHERE {q("MigrationId")} = '{migrationId}'").ToListAsync();
                if (applied.Count == 0)
                {
                    // Column exists but migration not recorded — mark it as applied
                    await db.Database.ExecuteSqlRawAsync(
                        $"INSERT INTO {q("__EFMigrationsHistory")} ({q("MigrationId")}, {q("ProductVersion")}) VALUES ('{migrationId}', '10.0.2')");
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
