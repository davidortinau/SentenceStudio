using CoreSync;
using CoreSync.Http.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.Services;

public interface ISyncService
{
    Task InitializeDatabaseAsync();
    Task TriggerSyncAsync();
    bool IsInitialSyncInProgress { get; }
    event Action? InitialSyncCompleted;
}

public class NoOpSyncService : ISyncService
{
    public Task InitializeDatabaseAsync() => Task.CompletedTask;
    public Task TriggerSyncAsync() => Task.CompletedTask;
    public bool IsInitialSyncInProgress => false;
    public event Action? InitialSyncCompleted;
}

public class SyncService : ISyncService
{
    private readonly ISyncProvider _localSyncProvider;
    private readonly ISyncProviderHttpClient _remoteSyncProvider;
    private readonly ILogger<SyncService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly SemaphoreSlim _syncSemaphore = new(1, 1);
    private bool _isInitialized = false;
    
    public bool IsInitialSyncInProgress { get; private set; }
    public event Action? InitialSyncCompleted;

    public SyncService(
        ISyncProvider localSyncProvider,
        ISyncProviderHttpClient remoteSyncProvider,
        ILogger<SyncService> logger,
        IServiceProvider serviceProvider)
    {
        _localSyncProvider = localSyncProvider;
        _remoteSyncProvider = remoteSyncProvider;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task InitializeDatabaseAsync()
    {
        if (_isInitialized)
        {
            _logger.LogInformation("Database already initialized, skipping");
            return;
        }

        try
        {
            _logger.LogInformation("Initializing CoreSync provider...");

            // First: Ensure EF Core applies all migrations (server) or creates schema (mobile)
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

#if IOS || ANDROID || MACCATALYST
            _logger.LogDebug("Running EF Core migrations on mobile...");
            
            // Handle transition from legacy EnsureCreated() to MigrateAsync().
            // Existing databases won't have __EFMigrationsHistory, so MigrateAsync
            // would try to re-create all tables and fail. Detect this case and seed
            // the migration history so only NEW migrations run.
            var conn = dbContext.Database.GetDbConnection();
            await conn.OpenAsync();
            try
            {
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
                var historyExists = await checkCmd.ExecuteScalarAsync() != null;

                if (!historyExists)
                {
                    using var appTableCmd = conn.CreateCommand();
                    appTableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='LearningResource'";
                    var isLegacyDb = await appTableCmd.ExecuteScalarAsync() != null;

                    if (isLegacyDb)
                    {
                        _logger.LogInformation("Legacy database detected (no history table) — seeding migration history for InitialSqlite");
                        using var seedCmd = conn.CreateCommand();
                        seedCmd.CommandText = @"
                            CREATE TABLE ""__EFMigrationsHistory"" (
                                ""MigrationId"" TEXT NOT NULL PRIMARY KEY,
                                ""ProductVersion"" TEXT NOT NULL
                            );
                            INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                            VALUES ('20260321133148_InitialSqlite', '10.0.4');";
                        await seedCmd.ExecuteNonQueryAsync();
                    }
                }
                else
                {
                    // Detect databases with old integer-PK schema that can't work with
                    // the new GUID-based model and CoreSync. Check if LearningResource
                    // still uses INTEGER PK (old schema) vs TEXT PK (new schema).
                    using var pkCheckCmd = conn.CreateCommand();
                    pkCheckCmd.CommandText = @"
                        SELECT type FROM pragma_table_info('LearningResource') WHERE name='Id'";
                    var pkType = (await pkCheckCmd.ExecuteScalarAsync())?.ToString() ?? "";
                    
                    var hasOldSchema = pkType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);

                    if (hasOldSchema)
                    {
                        _logger.LogWarning("Old integer-PK schema detected — recreating database with GUID schema. Server sync will restore all data.");
                        IsInitialSyncInProgress = true;

                        // Drop all app tables so InitialSqlite can recreate them cleanly.
                        // User data is safe on the server — CoreSync will pull it back.
                        using var dropCmd = conn.CreateCommand();
                        dropCmd.CommandText = @"
                            PRAGMA foreign_keys = OFF;
                            DROP TABLE IF EXISTS ""__CORE_SYNC_CT"";
                            DROP TABLE IF EXISTS ""__CORE_SYNC_LOCAL_ID"";
                            DROP TABLE IF EXISTS ""__CORE_SYNC_REMOTE_ANCHOR"";
                            DROP TABLE IF EXISTS ""ResourceVocabularyMapping"";
                            DROP TABLE IF EXISTS ""ExampleSentence"";
                            DROP TABLE IF EXISTS ""VocabularyLearningContext"";
                            DROP TABLE IF EXISTS ""GradeResponse"";
                            DROP TABLE IF EXISTS ""SceneImage"";
                            DROP TABLE IF EXISTS ""MinimalPairAttempt"";
                            DROP TABLE IF EXISTS ""MinimalPairSession"";
                            DROP TABLE IF EXISTS ""MinimalPair"";
                            DROP TABLE IF EXISTS ""ConversationChunk"";
                            DROP TABLE IF EXISTS ""ConversationMemoryState"";
                            DROP TABLE IF EXISTS ""Conversation"";
                            DROP TABLE IF EXISTS ""ConversationScenario"";
                            DROP TABLE IF EXISTS ""StreamHistory"";
                            DROP TABLE IF EXISTS ""Story"";
                            DROP TABLE IF EXISTS ""Challenge"";
                            DROP TABLE IF EXISTS ""VocabularyProgress"";
                            DROP TABLE IF EXISTS ""VocabularyWord"";
                            DROP TABLE IF EXISTS ""VocabularyList"";
                            DROP TABLE IF EXISTS ""LearningResource"";
                            DROP TABLE IF EXISTS ""SkillProfile"";
                            DROP TABLE IF EXISTS ""DailyPlanCompletion"";
                            DROP TABLE IF EXISTS ""UserActivity"";
                            DROP TABLE IF EXISTS ""UserProfile"";
                            DROP TABLE IF EXISTS ""WordAssociationScore"";
                            DROP TABLE IF EXISTS ""VideoImport"";
                            DROP TABLE IF EXISTS ""MonitoredChannel"";
                            DROP TABLE IF EXISTS ""AspNetUserTokens"";
                            DROP TABLE IF EXISTS ""AspNetUserRoles"";
                            DROP TABLE IF EXISTS ""AspNetUserLogins"";
                            DROP TABLE IF EXISTS ""AspNetUserClaims"";
                            DROP TABLE IF EXISTS ""AspNetRoleClaims"";
                            DROP TABLE IF EXISTS ""AspNetUsers"";
                            DROP TABLE IF EXISTS ""AspNetRoles"";
                            DROP TABLE IF EXISTS ""__EFMigrationsHistory"";
                            DROP TABLE IF EXISTS ""__EFMigrationsLock"";
                            PRAGMA foreign_keys = ON;";
                        await dropCmd.ExecuteNonQueryAsync();
                        _logger.LogInformation("Old schema tables dropped — MigrateAsync will create fresh GUID-based schema");
                    }
                }

                // EF9+ SQLite concurrent migration protection creates __EFMigrationsLock.
                // Per MS Learn docs, if a previous migration failed non-recoverably, this
                // lock table stays and silently blocks all future MigrateAsync() calls.
                // Clear it before migrating so we're never stuck.
                // Ref: https://learn.microsoft.com/ef/core/providers/sqlite/limitations#concurrent-migrations-protection
                using var lockCmd = conn.CreateCommand();
                lockCmd.CommandText = "DROP TABLE IF EXISTS \"__EFMigrationsLock\"";
                await lockCmd.ExecuteNonQueryAsync();
                _logger.LogDebug("Cleared any stale __EFMigrationsLock table");

                // Always patch missing columns before MigrateAsync — safe on DBs
                // that already have them (pragma_table_info check skips existing).
                // This covers databases where the migration history was seeded in a
                // previous run but PatchMissingColumnsAsync wasn't called at that time.
                await PatchMissingColumnsAsync(conn);
            }
            finally
            {
                await conn.CloseAsync();
            }

            // CRITICAL: Migration failures are FATAL — must re-throw.
            // Silently continuing with stale schema causes "column doesn't exist" errors
            // at runtime that are hard to diagnose. Fail fast at startup instead.
            try
            {
                await dbContext.Database.MigrateAsync();
                _logger.LogInformation("Mobile database migrated successfully");
            }
            catch (Exception migrationEx)
            {
                _logger.LogCritical(migrationEx, 
                    "FATAL: Database migration failed. App cannot continue with stale schema. " +
                    "Uninstall and reinstall may be required, or contact support.");
                throw; // Re-throw to surface migration failures immediately
            }

            // Run patch AGAIN after MigrateAsync — on fresh installs, the table didn't
            // exist before MigrateAsync, so the pre-migration patch skipped it. Now the
            // table exists but may be missing columns not yet in a migration file.
            try
            {
                await conn.OpenAsync();
                await PatchMissingColumnsAsync(conn);
            }
            finally
            {
                await conn.CloseAsync();
            }
#else
            _logger.LogDebug("Running EF Core migrations...");
            
            // CRITICAL: Migration failures are FATAL — must re-throw.
            try
            {
                await dbContext.Database.MigrateAsync();
                _logger.LogInformation("EF Core database migrated");
            }
            catch (Exception migrationEx)
            {
                _logger.LogCritical(migrationEx, 
                    "FATAL: Database migration failed on server. Check connection string and database state.");
                throw; // Re-throw to surface migration failures immediately
            }
#endif

#if DEBUG && (IOS || ANDROID || MACCATALYST)
            // Mobile schema sanity check — validates critical columns/tables exist after migration.
            // In Debug: throws on failure to surface schema drift immediately.
            // In Release: logs Critical but continues (don't brick user apps).
            var sanityCheckService = scope.ServiceProvider.GetRequiredService<MigrationSanityCheckService>();
            await sanityCheckService.ValidateSchemaAsync(dbContext);
#endif

            // Background initialization tasks below can fail non-fatally.
            // If backfill or sync provisioning fails, app can still run (degraded).
            try
            {
                // Run vocabulary classification backfill (idempotent)
                var backfillService = scope.ServiceProvider.GetRequiredService<VocabularyClassificationBackfillService>();
                await backfillService.BackfillLexicalUnitTypesAsync();
                
                // Run phrase constituent backfill (idempotent, after classification)
                await backfillService.BackfillPhraseConstituentsAsync();

                // One-time data fix: Known words with near-term review dates
                // PR #155 fixed the code path, this retroactively fixes existing records
                await FixKnownWordSchedulesAsync(dbContext);

                // Then: Apply CoreSync provisioning to create sync tracking tables
                _logger.LogDebug("Applying CoreSync provisioning...");
                await _localSyncProvider.ApplyProvisionAsync();
                _logger.LogInformation("CoreSync provisioning applied");

                _isInitialized = true;
                _logger.LogInformation("SyncService initialization complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Non-fatal initialization failure (backfill/sync): {Message}", ex.Message);
                // Continue — app can run with degraded sync capability
            }
        }
        catch (Exception ex)
        {
            // This outer catch should now only catch initialization failures OUTSIDE
            // of the migration path (e.g., service provider issues, scope creation).
            // Migration failures re-throw above and won't reach here.
            _logger.LogCritical(ex, "FATAL: SyncService initialization failed completely: {Message}", ex.Message);
            throw;
        }
    }

    public async Task TriggerSyncAsync()
    {
        if (!await _syncSemaphore.WaitAsync(100))
        {
            _logger.LogDebug("Sync already in progress, skipping");
            return;
        }

        try
        {
            await _localSyncProvider.ApplyProvisionAsync();
            
            var syncAgent = new SyncAgent(_localSyncProvider, _remoteSyncProvider);
            
            // With GUID PKs, INSERT conflicts only occur for truly duplicate records.
            // Skip INSERT conflicts (same record already exists), ForceWrite for UPDATE/DELETE.
            await syncAgent.SynchronizeAsync(
                remoteConflictResolutionFunc: item =>
                {
                    if (item.ChangeType == ChangeType.Insert)
                    {
                        _logger.LogDebug("Sync: INSERT conflict on remote for {Table} PK={PK}, skipping (already exists)",
                            item.TableName, GetPkFromItem(item));
                        return ConflictResolution.Skip;
                    }
                    return ConflictResolution.ForceWrite;
                },
                localConflictResolutionFunc: item =>
                {
                    if (item.ChangeType == ChangeType.Insert)
                    {
                        _logger.LogDebug("Sync: INSERT conflict on local for {Table} PK={PK}, skipping (already exists)",
                            item.TableName, GetPkFromItem(item));
                        return ConflictResolution.Skip;
                    }
                    return ConflictResolution.ForceWrite;
                });
            
            _logger.LogInformation("Sync completed successfully");
            
            if (IsInitialSyncInProgress)
            {
                IsInitialSyncInProgress = false;
                _logger.LogInformation("Initial sync completed — notifying UI");
                InitialSyncCompleted?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed: {Message}", ex.Message);
            
            if (IsInitialSyncInProgress)
            {
                IsInitialSyncInProgress = false;
                _logger.LogWarning("Initial sync failed — clearing overlay so user can proceed");
                InitialSyncCompleted?.Invoke();
            }
        }
        finally
        {
            _syncSemaphore.Release();
        }
    }

    private static string GetPkFromItem(SyncItem item)
    {
        if (item.Values.TryGetValue("Id", out var id))
            return id?.ToString() ?? "?";
        return "?";
    }

    /// <summary>
    /// One-time data fix: Known words (MasteryScore >= 0.85 AND ProductionInStreak >= 2)
    /// that were mastered before PR #155 may have near-term NextReviewDate values because
    /// SM-2 set a short interval before the mastery check. This retroactively pushes them
    /// to a 60-day interval. Idempotent — safe to run on every startup.
    /// </summary>
    private async Task FixKnownWordSchedulesAsync(ApplicationDbContext dbContext)
    {
        try
        {
            var isPostgres = dbContext.Database.ProviderName?.Contains("Npgsql") == true;

            string sql;
            if (isPostgres)
            {
                sql = @"
                    UPDATE ""VocabularyProgress""
                    SET ""ReviewInterval"" = 60,
                        ""NextReviewDate"" = NOW() + INTERVAL '60 days',
                        ""UpdatedAt"" = NOW()
                    WHERE ""MasteryScore"" >= 0.85
                      AND ""ProductionInStreak"" >= 2
                      AND (""ReviewInterval"" < 60 OR ""ReviewInterval"" IS NULL)";
            }
            else
            {
                sql = @"
                    UPDATE VocabularyProgress
                    SET ReviewInterval = 60,
                        NextReviewDate = datetime('now', '+60 days'),
                        UpdatedAt = datetime('now')
                    WHERE MasteryScore >= 0.85
                      AND ProductionInStreak >= 2
                      AND (ReviewInterval < 60 OR ReviewInterval IS NULL)";
            }

            var affected = await dbContext.Database.ExecuteSqlRawAsync(sql);
            if (affected > 0)
            {
                _logger.LogInformation(
                    "Fixed {Count} Known words with near-term review schedules (set to 60-day interval)",
                    affected);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — the GetDueVocabularyAsync predicate already excludes Known words
            _logger.LogWarning(ex, "Failed to fix Known word schedules — non-fatal, predicate filter is in place");
        }
    }

#if IOS || ANDROID || MACCATALYST
    /// <summary>
    /// Adds any columns that InitialSqlite would have created but that are
    /// missing from a legacy database whose migration history was seeded.
    /// Each entry is checked via pragma_table_info before ALTER TABLE runs,
    /// so this is safe to call on databases that already have the columns.
    /// </summary>
    private async Task PatchMissingColumnsAsync(System.Data.Common.DbConnection conn)
    {
        // (table, column, SQLite type, nullable)
        var expectedColumns = new (string Table, string Column, string SqlType)[]
        {
            ("VocabularyWord", "Language", "TEXT"),
            ("VocabularyWord", "Lemma", "TEXT"),
            ("VocabularyWord", "Tags", "TEXT"),
            ("VocabularyWord", "MnemonicText", "TEXT"),
            ("VocabularyWord", "MnemonicImageUri", "TEXT"),
            ("VocabularyWord", "AudioPronunciationUri", "TEXT"),
            ("DailyPlanCompletion", "NarrativeJson", "TEXT"),
            ("VocabularyProgress", "LastExposedAt", "TEXT"),
            ("VocabularyProgress", "ExposureCount", "INTEGER"),
        };

        foreach (var (table, column, sqlType) in expectedColumns)
        {
            // On a fresh database the table won't exist yet — skip patching;
            // MigrateAsync() will create it with all columns.
            using var tableCmd = conn.CreateCommand();
            tableCmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
            if (Convert.ToInt64(await tableCmd.ExecuteScalarAsync()) == 0)
                continue;

            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
            var exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync()) > 0;

            if (!exists)
            {
                _logger.LogWarning("Legacy schema patch: adding missing column {Table}.{Column}", table, column);
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {sqlType}";
                await alterCmd.ExecuteNonQueryAsync();
            }
        }

        // Patch for LexicalUnitType column (NOT NULL with DEFAULT 0 — SQLite requires special handling)
        using (var vocabTableCmd = conn.CreateCommand())
        {
            vocabTableCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='VocabularyWord'";
            if (Convert.ToInt64(await vocabTableCmd.ExecuteScalarAsync()) > 0)
            {
                using var checkLexicalCmd = conn.CreateCommand();
                checkLexicalCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('VocabularyWord') WHERE name='LexicalUnitType'";
                var lexicalExists = Convert.ToInt64(await checkLexicalCmd.ExecuteScalarAsync()) > 0;

                if (!lexicalExists)
                {
                    _logger.LogWarning("Legacy schema patch: adding missing column VocabularyWord.LexicalUnitType");
                    using var alterLexicalCmd = conn.CreateCommand();
                    alterLexicalCmd.CommandText = "ALTER TABLE \"VocabularyWord\" ADD COLUMN \"LexicalUnitType\" INTEGER NOT NULL DEFAULT 0";
                    await alterLexicalCmd.ExecuteNonQueryAsync();
                }
            }
        }

        // Patch for PhraseConstituent table + indexes (idempotent via IF NOT EXISTS)
        using (var vocabTableCmd = conn.CreateCommand())
        {
            vocabTableCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='VocabularyWord'";
            if (Convert.ToInt64(await vocabTableCmd.ExecuteScalarAsync()) > 0)
            {
                using var checkPhraseTableCmd = conn.CreateCommand();
                checkPhraseTableCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PhraseConstituent'";
                var phraseTableExists = Convert.ToInt64(await checkPhraseTableCmd.ExecuteScalarAsync()) > 0;

                if (!phraseTableExists)
                {
                    _logger.LogWarning("Legacy schema patch: creating missing table PhraseConstituent with indexes");
                    using var createTableCmd = conn.CreateCommand();
                    createTableCmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS ""PhraseConstituent"" (
                            ""Id"" TEXT NOT NULL CONSTRAINT ""PK_PhraseConstituent"" PRIMARY KEY,
                            ""PhraseWordId"" TEXT NOT NULL,
                            ""ConstituentWordId"" TEXT NULL,
                            ""CreatedAt"" TEXT NOT NULL,
                            CONSTRAINT ""FK_PhraseConstituent_VocabularyWord_PhraseWordId"" FOREIGN KEY (""PhraseWordId"") REFERENCES ""VocabularyWord"" (""Id"") ON DELETE CASCADE,
                            CONSTRAINT ""FK_PhraseConstituent_VocabularyWord_ConstituentWordId"" FOREIGN KEY (""ConstituentWordId"") REFERENCES ""VocabularyWord"" (""Id"") ON DELETE SET NULL
                        );
                        CREATE INDEX IF NOT EXISTS ""IX_PhraseConstituent_ConstituentWordId"" ON ""PhraseConstituent"" (""ConstituentWordId"");
                        CREATE INDEX IF NOT EXISTS ""IX_PhraseConstituent_PhraseWordId"" ON ""PhraseConstituent"" (""PhraseWordId"");
                        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PhraseConstituent_PhraseWordId_ConstituentWordId"" ON ""PhraseConstituent"" (""PhraseWordId"", ""ConstituentWordId"");
                    ";
                    await createTableCmd.ExecuteNonQueryAsync();
                }
            }
        }
    }
#endif


}
