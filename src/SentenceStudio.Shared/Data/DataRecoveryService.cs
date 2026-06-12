using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Data;

/// <summary>
/// Recovers orphaned local data after a Postgres wipe + re-registration scenario.
/// When the server creates a new UserProfileId for a returning user, local SQLite
/// records still carry the old ID and become invisible to repository queries.
/// This service re-tags all user-scoped rows to the new ID before sync runs,
/// so CoreSync can push the recovered data back to the server.
///
/// SAFEGUARDS (all must pass before any UPDATE/DELETE runs):
///   1. Email mismatch abort — orphan UserProfile.Email != new user email → different human, not server wipe.
///   2. Temporal sanity abort — orphan records pre-date the new account's CreatedAt by more than 1 day.
///   3. First-run gate — per-user preference "_data_recovery_complete_{userId}"="true" makes the service one-shot idempotent per account.
///
/// See also: .squad/decisions/inbox/captain-rca-datarecoveryservice-cross-tenant-corruption.md
/// </summary>
public class DataRecoveryService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DataRecoveryService> _logger;
    private readonly IPreferencesService _preferences;

    private const string RecoveryCompleteKeyPrefix = "_data_recovery_complete_";

    // Per-user first-run key so different users on the same device each get one recovery chance.
    private static string RecoveryCompleteKey(string userId) => $"{RecoveryCompleteKeyPrefix}{userId}";

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

    public DataRecoveryService(
        IServiceProvider serviceProvider,
        ILogger<DataRecoveryService> logger,
        IPreferencesService preferences)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _preferences = preferences;
    }

    /// <summary>
    /// Scans local SQLite for records that belong to a different user ID than
    /// <paramref name="newUserProfileId"/> and re-tags them, subject to safeguards
    /// that prevent cross-tenant data corruption on shared or test devices.
    /// </summary>
    /// <param name="newUserProfileId">The UserProfileId of the user who just signed in.</param>
    /// <param name="newUserEmail">Email of the user who just signed in (from JWT). Used for email mismatch safeguard.</param>
    public async Task RecoverOrphanedDataAsync(string newUserProfileId, string? newUserEmail = null)
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

        // SAFEGUARD 3: First-run gate — per-user key so a different user on the same device
        // gets their own chance at recovery. Once set, recovery is one-shot for that userId.
        if (_preferences.Get(RecoveryCompleteKey(newUserProfileId), "") == "true")
        {
            _logger.LogDebug("[OrphanRecovery] First-run gate: {Key}=true — skipping", RecoveryCompleteKey(newUserProfileId));
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
            // Mark as done so future logins skip the scan entirely.
            _preferences.Set(RecoveryCompleteKey(newUserProfileId), "true");
            return;
        }

        // SAFEGUARD 1: Email mismatch — if any orphan UserProfile has a non-empty Email that differs
        // from the new user's email, this is a shared/test device with data belonging to a different
        // human, not a server-wipe recovery scenario.
        if (!string.IsNullOrEmpty(newUserEmail))
        {
            var orphanEmails = await GetOrphanProfileEmailsAsync(db, newUserProfileId);
            var conflictingEmails = orphanEmails
                .Where(e => !string.IsNullOrEmpty(e)
                    && !e.Equals(newUserEmail, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (conflictingEmails.Count > 0)
            {
                _logger.LogWarning(
                    "[OrphanRecovery] ABORTED — Email mismatch: orphan profile(s) belong to a different user. " +
                    "NewUserId={NewUserId}, OrphanUserIds={OrphanCount}, OrphanEmails={OrphanEmails}",
                    newUserProfileId, orphanIds.Count,
                    string.Join(", ", conflictingEmails.Select(MaskEmail)));
                return;
            }
        }

        // SAFEGUARD 2: Temporal sanity — compare the new account's creation date against the
        // earliest orphan record timestamps. Two cases:
        //   a) New user profile exists locally: abort if orphan data predates it by >1 day.
        //   b) New user profile NOT yet synced locally (the normal StoreTokens ordering — sync
        //      runs AFTER recovery): if there are any orphan timestamps we cannot verify safety,
        //      so abort. A server-wipe recovery will not have orphan timestamps until after sync.
        var newUserCreatedAt = await GetNewUserCreatedAtAsync(db, newUserProfileId);
        var earliestOrphan   = await GetEarliestOrphanCreatedAtAsync(db, newUserProfileId);

        if (!newUserCreatedAt.HasValue)
        {
            // Profile not yet present locally. If orphan timestamps exist we cannot distinguish
            // a legitimate server-wipe recovery from a cross-tenant scenario — abort.
            if (earliestOrphan.HasValue)
            {
                _logger.LogWarning(
                    "[OrphanRecovery] ABORTED — Cannot verify temporal sanity: new user {NewUserId} has no local " +
                    "profile yet but {OrphanCount} orphan record(s) with timestamps are present. " +
                    "Treating as potential cross-tenant scenario.",
                    newUserProfileId, orphanIds.Count);
                return;
            }
            // No orphan timestamps — temporal check inconclusive but unblocking; continue to retag.
        }
        else
        {
            var cutoff = newUserCreatedAt.Value.AddDays(-1);
            if (earliestOrphan.HasValue && earliestOrphan.Value < cutoff)
            {
                _logger.LogWarning(
                    "[OrphanRecovery] ABORTED — Temporal mismatch: orphan data ({Earliest:O}) predates new account ({NewCreated:O}). " +
                    "NewUserId={NewUserId}, OrphanUserIds={OrphanCount}",
                    earliestOrphan.Value, newUserCreatedAt.Value, newUserProfileId, orphanIds.Count);
                return;
            }
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

        // Mark done so subsequent logins skip the recovery scan.
        _preferences.Set(RecoveryCompleteKey(newUserProfileId), "true");
    }

    /// <summary>
    /// Returns the email addresses of all UserProfile rows whose Id differs from <paramref name="newUserProfileId"/>.
    /// Used by the email mismatch safeguard.
    /// </summary>
    private async Task<List<string>> GetOrphanProfileEmailsAsync(ApplicationDbContext db, string newUserProfileId)
    {
        try
        {
            return await db.Database.SqlQueryRaw<string>(
                "SELECT \"Email\" FROM \"UserProfile\" " +
                "WHERE \"Id\" != {0} AND \"Email\" IS NOT NULL AND \"Email\" != ''",
                newUserProfileId).ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OrphanRecovery] Could not query orphan UserProfile emails");
            return new List<string>();
        }
    }

    /// <summary>
    /// Returns the CreatedAt of the new user's local UserProfile row, if it exists.
    /// Used by the temporal sanity safeguard.
    /// </summary>
    private async Task<DateTime?> GetNewUserCreatedAtAsync(ApplicationDbContext db, string newUserProfileId)
    {
        try
        {
            return await db.UserProfiles
                .Where(p => p.Id == newUserProfileId)
                .Select(p => (DateTime?)p.CreatedAt)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OrphanRecovery] Could not query new user CreatedAt");
            return null;
        }
    }

    /// <summary>
    /// Returns the earliest CreatedAt across orphan rows in the two highest-volume tables
    /// (LearningResource and UserActivity). Used by the temporal sanity safeguard.
    /// IMPORTANT: do not narrow these queries — any active user has UserActivity rows, so
    /// inspecting LR+UA gives sufficient temporal signal. Adding/removing tables here
    /// changes the safeguard's coverage; see captain-rca-datarecoveryservice-cross-tenant-corruption.md.
    /// </summary>
    private async Task<DateTime?> GetEarliestOrphanCreatedAtAsync(ApplicationDbContext db, string newUserProfileId)
    {
        DateTime? earliest = null;

        try
        {
            var earliestResource = await db.LearningResources
                .Where(r => r.UserProfileId != null && r.UserProfileId != newUserProfileId)
                .Select(r => (DateTime?)r.CreatedAt)
                .MinAsync();

            if (earliestResource.HasValue && (earliest is null || earliestResource.Value < earliest.Value))
                earliest = earliestResource;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OrphanRecovery] Could not query LearningResource orphan timestamps");
        }

        try
        {
            var earliestActivity = await db.UserActivities
                .Where(a => a.UserProfileId != newUserProfileId)
                .Select(a => (DateTime?)a.CreatedAt)
                .MinAsync();

            if (earliestActivity.HasValue && (earliest is null || earliestActivity.Value < earliest.Value))
                earliest = earliestActivity;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OrphanRecovery] Could not query UserActivity orphan timestamps");
        }

        return earliest;
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

    /// <summary>
    /// Returns a partially-redacted email for safe use in warning logs.
    /// "dave@ortinau.com" → "dav***@ortinau.com"
    /// Keeps first 3 chars of local-part and full domain for incident tracing without
    /// storing a complete PII record in telemetry.
    /// </summary>
    private static string MaskEmail(string email)
    {
        if (string.IsNullOrEmpty(email)) return "(empty)";
        var atIndex = email.IndexOf('@');
        if (atIndex <= 0) return "***";
        var prefix = email[..Math.Min(3, atIndex)];
        return $"{prefix}***{email[atIndex..]}";
    }
}
