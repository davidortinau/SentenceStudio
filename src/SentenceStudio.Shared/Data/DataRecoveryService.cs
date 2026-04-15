using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

/// <summary>
/// Recovers orphaned local data after a Postgres wipe + re-registration scenario.
/// When the server creates a new UserProfileId for a returning user, local SQLite
/// records still carry the old ID and become invisible to repository queries.
/// This service re-tags all user-scoped rows to the new ID before sync runs,
/// so CoreSync can push the recovered data back to the server.
/// </summary>
public class DataRecoveryService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DataRecoveryService> _logger;

    // Tables that use "UserId" as the user-scoping column
    private static readonly (string Table, string Column)[] UserIdTables =
    {
        ("VocabularyProgress", "UserId"),
        ("MinimalPair", "UserId"),
        ("MinimalPairSession", "UserId"),
        ("MinimalPairAttempt", "UserId"),
    };

    // Tables that use "UserProfileId" as the user-scoping column
    private static readonly (string Table, string Column)[] UserProfileIdTables =
    {
        ("SkillProfile", "UserProfileId"),
        ("DailyPlanCompletion", "UserProfileId"),
        ("UserActivity", "UserProfileId"),
        ("LearningResource", "UserProfileId"),
        ("WordAssociationScore", "UserProfileId"),
        ("MonitoredChannel", "UserProfileId"),
        ("VideoImport", "UserProfileId"),
    };

    public DataRecoveryService(IServiceProvider serviceProvider, ILogger<DataRecoveryService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Scans local SQLite for records that belong to a different user ID than
    /// <paramref name="newUserProfileId"/> and re-tags them. Safe to call multiple
    /// times — idempotent when no orphans remain.
    /// </summary>
    public async Task RecoverOrphanedDataAsync(string newUserProfileId)
    {
        if (string.IsNullOrEmpty(newUserProfileId))
        {
            _logger.LogWarning("[OrphanRecovery] Called with empty newUserProfileId — skipping");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Only run on SQLite (local device). Never on Postgres (server).
        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) != true)
        {
            _logger.LogDebug("[OrphanRecovery] Not running on SQLite — skipping");
            return;
        }

        // Discover orphan user IDs across all user-scoped tables
        var orphanIds = new HashSet<string>();
        foreach (var (table, column) in UserIdTables)
            await CollectOrphanIdsAsync(db, table, column, newUserProfileId, orphanIds);
        foreach (var (table, column) in UserProfileIdTables)
            await CollectOrphanIdsAsync(db, table, column, newUserProfileId, orphanIds);
        await CollectOrphanIdsAsync(db, "UserProfile", "Id", newUserProfileId, orphanIds);

        if (orphanIds.Count == 0)
        {
            _logger.LogInformation("[OrphanRecovery] No orphaned records found — data is clean");
            return;
        }

        if (orphanIds.Count == 1)
        {
            _logger.LogInformation("[OrphanRecovery] Found 1 old user ID — re-tagging all data to {NewId}",
                newUserProfileId);
        }
        else
        {
            _logger.LogWarning(
                "[OrphanRecovery] Found {Count} distinct old user IDs — re-tagging all to {NewId}",
                orphanIds.Count, newUserProfileId);
        }

        int totalRetagged = 0;

        // Re-tag all user-scoped tables
        foreach (var (table, column) in UserIdTables)
            totalRetagged += await RetagTableAsync(db, table, column, newUserProfileId);
        foreach (var (table, column) in UserProfileIdTables)
            totalRetagged += await RetagTableAsync(db, table, column, newUserProfileId);

        // Handle UserProfile PK (special case — Id is the PK itself)
        totalRetagged += await RetagUserProfileAsync(db, newUserProfileId, orphanIds);

        _logger.LogInformation(
            "[OrphanRecovery] Complete — {Total} records re-tagged to {NewId}",
            totalRetagged, newUserProfileId);
    }

    /// <summary>
    /// Finds distinct values in <paramref name="column"/> that differ from <paramref name="newId"/>
    /// and adds them to <paramref name="orphanIds"/>.
    /// </summary>
    private async Task CollectOrphanIdsAsync(
        ApplicationDbContext db, string table, string column,
        string newId, HashSet<string> orphanIds)
    {
        try
        {
            var ids = await db.Database.SqlQueryRaw<string>(
                $"SELECT DISTINCT \"{column}\" FROM \"{table}\" " +
                $"WHERE \"{column}\" IS NOT NULL AND \"{column}\" != '' AND \"{column}\" != {{0}}",
                newId).ToListAsync();

            foreach (var id in ids)
                orphanIds.Add(id);
        }
        catch (Exception ex)
        {
            // Table might not exist yet (fresh install). Not fatal.
            _logger.LogDebug(ex, "[OrphanRecovery] Could not query {Table}.{Column}", table, column);
        }
    }

    /// <summary>
    /// Updates all rows in <paramref name="table"/> where <paramref name="column"/>
    /// differs from <paramref name="newId"/>.
    /// </summary>
    private async Task<int> RetagTableAsync(
        ApplicationDbContext db, string table, string column, string newId)
    {
        try
        {
            int count = await db.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{table}\" SET \"{column}\" = {{0}} " +
                $"WHERE \"{column}\" IS NOT NULL AND \"{column}\" != '' AND \"{column}\" != {{0}}",
                newId);

            if (count > 0)
                _logger.LogInformation("[OrphanRecovery] Re-tagged {Count} rows in {Table}", count, table);

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OrphanRecovery] Failed to re-tag {Table}.{Column}", table, column);
            return 0;
        }
    }

    /// <summary>
    /// Handles the UserProfile table where Id is the primary key.
    /// If a profile with the new ID already exists, removes old profiles.
    /// Otherwise renames the first old profile to carry the new ID (preserving settings).
    /// </summary>
    private async Task<int> RetagUserProfileAsync(
        ApplicationDbContext db, string newId, HashSet<string> orphanIds)
    {
        try
        {
            var newProfileExists = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT \"Id\" FROM \"UserProfile\" WHERE \"Id\" = {0}", newId)
                .AnyAsync();

            if (newProfileExists)
            {
                // New profile exists (created by sync or onboarding).
                // Remove old orphan profiles — their data has already been re-tagged.
                int deleted = await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"UserProfile\" WHERE \"Id\" != {0}", newId);

                if (deleted > 0)
                    _logger.LogInformation("[OrphanRecovery] Removed {Count} orphan UserProfile row(s)", deleted);

                return deleted;
            }

            // No profile with the new ID yet. Promote the first old profile
            // by renaming its PK so the user keeps their settings.
            var firstOldId = orphanIds.FirstOrDefault();
            if (firstOldId == null)
                return 0;

            int updated = await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"UserProfile\" SET \"Id\" = {0} WHERE \"Id\" = {1}",
                newId, firstOldId);

            // Clean up any extra old profiles
            int extras = await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"UserProfile\" WHERE \"Id\" != {0}", newId);

            if (updated > 0)
                _logger.LogInformation(
                    "[OrphanRecovery] Promoted UserProfile {OldId} -> {NewId}", firstOldId, newId);
            if (extras > 0)
                _logger.LogInformation(
                    "[OrphanRecovery] Removed {Count} extra UserProfile row(s)", extras);

            return updated + extras;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OrphanRecovery] Failed to update UserProfile");
            return 0;
        }
    }
}
