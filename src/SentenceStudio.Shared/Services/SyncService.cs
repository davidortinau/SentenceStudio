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
}

public class SyncService : ISyncService
{
    private readonly ISyncProvider _localSyncProvider;
    private readonly ISyncProviderHttpClient _remoteSyncProvider;
    private readonly ILogger<SyncService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly SemaphoreSlim _syncSemaphore = new(1, 1);
    private bool _isInitialized = false;

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
            // the migration history so only NEW migrations (e.g. YouTube tables) run.
            var conn = dbContext.Database.GetDbConnection();
            await conn.OpenAsync();
            try
            {
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
                var historyExists = await checkCmd.ExecuteScalarAsync() != null;

                if (!historyExists)
                {
                    // Check if this is an existing DB (has app tables) vs a fresh install
                    using var appTableCmd = conn.CreateCommand();
                    appTableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='LearningResource'";
                    var isLegacyDb = await appTableCmd.ExecuteScalarAsync() != null;

                    if (isLegacyDb)
                    {
                        _logger.LogInformation("Legacy database detected — seeding migration history for InitialSqlite");
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
            }
            finally
            {
                await conn.CloseAsync();
            }

            try
            {
                await dbContext.Database.MigrateAsync();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("PendingModelChangesWarning"))
            {
                // On mobile, the compiled model (DbContext) differs from the migration snapshot
                // (generated from IdentityDbContext on server TFM). Identity tables don't exist
                // in the mobile model. Fall back to EnsureCreated for new DB, or create only
                // missing tables for existing DB.
                _logger.LogWarning("PendingModelChangesWarning caught — mobile model diverges from snapshot. Using fallback...");
                
                var hasHistory = false;
                var conn2 = dbContext.Database.GetDbConnection();
                await conn2.OpenAsync();
                try
                {
                    using var cmd = conn2.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
                    hasHistory = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                }
                catch { /* table doesn't exist */ }
                finally { await conn2.CloseAsync(); }

                if (!hasHistory)
                {
                    // Fresh or legacy DB — use EnsureCreated to create all tables from model
                    _logger.LogInformation("Falling back to EnsureCreated for database schema...");
                    await dbContext.Database.EnsureCreatedAsync();
                }
            }
            _logger.LogInformation("Mobile database migrated successfully");
#else
            _logger.LogDebug("Running EF Core migrations...");
            await dbContext.Database.MigrateAsync();
            _logger.LogInformation("EF Core database migrated");
#endif

            // Then: Apply CoreSync provisioning to create sync tracking tables
            _logger.LogDebug("Applying CoreSync provisioning...");
            await _localSyncProvider.ApplyProvisionAsync();
            _logger.LogInformation("CoreSync provisioning applied");

            _isInitialized = true;
            _logger.LogInformation("SyncService initialization complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize CoreSync: {Message}", ex.Message);
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed: {Message}", ex.Message);
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
}
