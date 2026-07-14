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

    /// <summary>
    /// Returns the <see cref="UserProfile"/> with the given id, or <c>null</c> when no
    /// row exists. Uses an indexed primary-key lookup — never load all profiles to
    /// filter in memory.
    /// </summary>
    public async Task<UserProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id)) return null;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
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

    // Tracks which user profiles have had smart resources seeded this session.
    // SmartResourceService.InitializeSmartResourcesAsync is per-type idempotent
    // at the DB level, so this set is purely an in-process optimization to
    // avoid re-running the HashSet check on every GetAsync call.
    private static readonly HashSet<string> _smartResourcesEnsured = new(StringComparer.Ordinal);
    private static readonly object _smartResourcesLock = new();

    /// <summary>
    /// Ensures smart resources (Daily Review, New Words, Struggling Words, Phrases)
    /// are seeded for the given profile. Safe to call repeatedly — the underlying
    /// service performs per-type idempotency against existing DB rows, so upgraded
    /// users receive newly-added smart resource types without duplicating existing
    /// ones. Failures are swallowed with a warning so profile load is never blocked.
    /// </summary>
    public async Task EnsureSmartResourcesAsync(UserProfile profile)
    {
        if (profile == null || string.IsNullOrEmpty(profile.Id)) return;

        lock (_smartResourcesLock)
        {
            if (_smartResourcesEnsured.Contains(profile.Id)) return;
        }

        try
        {
            var smartResources = _serviceProvider.GetService<SentenceStudio.Services.SmartResourceService>();
            if (smartResources == null)
            {
                _logger.LogDebug("SmartResourceService not registered; skipping smart-resource ensure for profile {ProfileId}", profile.Id);
            }
            else
            {
                var targetLanguage = string.IsNullOrWhiteSpace(profile.TargetLanguage) ? "Korean" : profile.TargetLanguage;
                await smartResources.InitializeSmartResourcesAsync(targetLanguage, profile.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Smart resource ensure failed for profile {ProfileId} (non-fatal)", profile.Id);
        }
        finally
        {
            lock (_smartResourcesLock)
            {
                _smartResourcesEnsured.Add(profile.Id);
            }
        }
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

        // Load the active profile if one was selected during login
        UserProfile profile = null;
        var activeId = _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;
        if (!string.IsNullOrEmpty(activeId))
        {
            profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == activeId);
            if (profile is null)
                _logger.LogWarning("GetAsync: active_profile_id '{ActiveId}' not found in UserProfiles — returning null", activeId);
        }
        else
        {
            _logger.LogWarning("GetAsync: no active_profile_id set — returning null");
        }

        // Ensure DisplayLanguage is never null or empty for existing profiles
        if (profile != null && string.IsNullOrEmpty(profile.DisplayLanguage))
        {
            profile.DisplayLanguage = "English";
        }

        // Ensure smart resources exist for this user (per-type idempotent,
        // sibling to EnsureMultiUserBackfillAsync — seeds Phrases etc. for upgraded users).
        if (profile != null)
        {
            await EnsureSmartResourcesAsync(profile);
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

    /// <summary>
    /// Persists only the <see cref="UserProfile.VocabQuizShowTextWithPhoto"/> property for
    /// the given user. Unlike <see cref="SaveAsync"/>, this performs a tracked single-property
    /// update and cannot overwrite unrelated concurrent changes.
    /// </summary>
    /// <returns><c>true</c> when the requested value is persisted for a matching user
    /// (including the no-op case where the value was already correct); <c>false</c> otherwise.</returns>
    public async Task<bool> SaveVocabQuizShowTextWithPhotoAsync(string userId, bool showTextWithPhoto)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning("SaveVocabQuizShowTextWithPhotoAsync called with no userId — returning false");
            return false;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == userId);
            if (profile is null)
            {
                _logger.LogWarning("SaveVocabQuizShowTextWithPhotoAsync: no UserProfile found for userId '{UserId}'", userId);
                return false;
            }

            if (profile.VocabQuizShowTextWithPhoto == showTextWithPhoto)
                return true;

            profile.VocabQuizShowTextWithPhoto = showTextWithPhoto;
            int rows = await db.SaveChangesAsync();

            _syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return rows > 0;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "SaveVocabQuizShowTextWithPhotoAsync: persistence failure for userId '{UserId}'", userId);
            return false;
        }
        catch (System.Data.Common.DbException ex)
        {
            _logger.LogError(ex, "SaveVocabQuizShowTextWithPhotoAsync: database error for userId '{UserId}'", userId);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "SaveVocabQuizShowTextWithPhotoAsync: invalid operation for userId '{UserId}'", userId);
            return false;
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
