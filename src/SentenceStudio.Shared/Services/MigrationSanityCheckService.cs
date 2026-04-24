using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.Services;

/// <summary>
/// Post-migration schema sanity check to catch silent migration failures in DEBUG builds.
/// Validates that critical columns and tables exist after MigrateAsync + PatchMissingColumnsAsync.
/// In Debug: throws on failure to surface schema drift immediately during development.
/// In Release: logs Critical but continues (don't brick user apps, but visibility for diagnostics).
/// </summary>
public class MigrationSanityCheckService
{
    private readonly ILogger<MigrationSanityCheckService> _logger;

    public MigrationSanityCheckService(ILogger<MigrationSanityCheckService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates that critical schema pieces exist in the database.
    /// Call this AFTER MigrateAsync() + PatchMissingColumnsAsync() in mobile builds.
    /// </summary>
    public async Task ValidateSchemaAsync(ApplicationDbContext dbContext)
    {
        var conn = dbContext.Database.GetDbConnection();
        var missingItems = new List<string>();

        // Critical columns that must exist after migrations
        var requiredColumns = new (string Table, string Column)[]
        {
            ("VocabularyWord", "LexicalUnitType"),
            ("VocabularyWord", "Language"),
            ("VocabularyWord", "Lemma"),
            ("VocabularyWord", "Tags"),
            ("VocabularyWord", "MnemonicText"),
            ("VocabularyWord", "AudioPronunciationUri"),
            ("VocabularyProgress", "ExposureCount"),
            ("VocabularyProgress", "LastExposedAt"),
            ("VocabularyProgress", "CurrentStreak"), // Should be REAL after CurrentStreakToFloat migration
            ("DailyPlanCompletion", "NarrativeJson"),
        };

        // Critical tables that must exist. NOTE: table names match ApplicationDbContext.OnModelCreating
        // ToTable() calls — singular (e.g. "DailyPlanCompletion", not "DailyPlans"). There is no
        // standalone "DailyPlan" table — daily plans are represented solely via DailyPlanCompletion.
        var requiredTables = new[]
        {
            "VocabularyWord",
            "VocabularyProgress",
            "PhraseConstituent",
            "DailyPlanCompletion",
            "UserProfile",
            "SkillProfile",
            "LearningResource",
        };

        try
        {
            await conn.OpenAsync();

            // Validate tables exist
            foreach (var tableName in requiredTables)
            {
                using var tableCmd = conn.CreateCommand();
                tableCmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
                var exists = Convert.ToInt64(await tableCmd.ExecuteScalarAsync()) > 0;
                
                if (!exists)
                {
                    missingItems.Add($"Table: {tableName}");
                }
            }

            // Validate columns exist
            foreach (var (table, column) in requiredColumns)
            {
                // Skip column check if table doesn't exist (already logged above)
                using var tableCmd = conn.CreateCommand();
                tableCmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
                if (Convert.ToInt64(await tableCmd.ExecuteScalarAsync()) == 0)
                    continue;

                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
                var exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync()) > 0;
                
                if (!exists)
                {
                    missingItems.Add($"{table}.{column}");
                }
            }

            if (missingItems.Any())
            {
                var errorMessage = $"Mobile schema sanity check FAILED — {missingItems.Count} missing items after migration: " +
                                 string.Join(", ", missingItems);
                
                _logger.LogCritical(errorMessage);

#if DEBUG
                // In Debug builds: fail fast so devs see the issue immediately
                throw new InvalidOperationException(errorMessage + 
                    " — Migration or PatchMissingColumnsAsync did not complete successfully. " +
                    "Check MigrateAsync logs for errors. Database may need to be deleted and recreated.");
#else
                // In Release builds: log Critical but continue (don't brick user apps)
                // User data preservation takes priority; diagnostics team will see the Critical log
                _logger.LogCritical("App will continue with degraded schema — some features may not work correctly.");
#endif
            }
            else
            {
                _logger.LogInformation(
                    "Mobile schema sanity check PASSED — {TableCount} tables, {ColumnCount} columns verified",
                    requiredTables.Length,
                    requiredColumns.Length);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
