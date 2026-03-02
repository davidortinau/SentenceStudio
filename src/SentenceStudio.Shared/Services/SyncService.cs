using CoreSync;
using CoreSync.Http.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using System.Text.Json;

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

    // FK dependency map: ParentTable → list of (ChildTable, FKColumn)
    private static readonly Dictionary<string, List<(string ChildTable, string FKColumn)>> FkDependencies = new()
    {
        ["UserProfile"] = new()
        {
            ("SkillProfile", "UserProfileId"),
            ("LearningResource", "UserProfileId"),
            ("VocabularyProgress", "UserId"),
        },
        ["SkillProfile"] = new()
        {
            ("LearningResource", "SkillID"),
        },
        ["VocabularyWord"] = new()
        {
            ("ResourceVocabularyMapping", "VocabularyWordId"),
            ("VocabularyProgress", "VocabularyWordId"),
        },
        ["LearningResource"] = new()
        {
            ("ResourceVocabularyMapping", "ResourceId"),
            ("VocabularyLearningContext", "LearningResourceId"),
        },
        ["Conversation"] = new()
        {
            ("ConversationChunk", "ConversationId"),
        },
        ["VocabularyProgress"] = new()
        {
            ("VocabularyLearningContext", "VocabularyProgressId"),
        },
    };

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
            _logger.LogInformation("⏭️ Database already initialized, skipping");
            return;
        }

        try
        {
            _logger.LogInformation("🚀 Initializing CoreSync provider...");

            // First: Ensure EF Core applies all migrations
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            _logger.LogDebug("📋 Running EF Core migrations...");
            await dbContext.Database.MigrateAsync();
            _logger.LogInformation("EF Core database migrated");

            // Then: Apply CoreSync provisioning to create sync tracking tables
            _logger.LogDebug("📋 Applying CoreSync provisioning...");
            await _localSyncProvider.ApplyProvisionAsync();
            _logger.LogInformation("CoreSync provisioning applied");

            _isInitialized = true;
            _logger.LogInformation("✅ SyncService initialization complete");
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
            
            // Pre-sync: remap local PKs that would collide with server
            await RemapConflictingPrimaryKeysAsync();
            
            var syncAgent = new SyncAgent(_localSyncProvider, _remoteSyncProvider);
            
            // After remapping, INSERT conflicts should not occur for different records.
            // Use Skip for INSERT conflicts as a safety net, ForceWrite for UPDATE/DELETE.
            await syncAgent.SynchronizeAsync(
                remoteConflictResolutionFunc: item =>
                {
                    if (item.ChangeType == ChangeType.Insert)
                    {
                        _logger.LogWarning("Sync: INSERT conflict on remote for {Table} PK={PK}, skipping", item.TableName, GetPkFromItem(item));
                        return ConflictResolution.Skip;
                    }
                    return ConflictResolution.ForceWrite;
                },
                localConflictResolutionFunc: item =>
                {
                    if (item.ChangeType == ChangeType.Insert)
                    {
                        _logger.LogWarning("Sync: INSERT conflict on local for {Table} PK={PK}, skipping", item.TableName, GetPkFromItem(item));
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

    /// <summary>
    /// Queries the server for max PKs and remaps any local PKs that would collide.
    /// This prevents INSERT conflicts during CoreSync sync when multiple offline clients
    /// create records with auto-increment integer PKs.
    /// </summary>
    private async Task RemapConflictingPrimaryKeysAsync()
    {
        Dictionary<string, long>? serverMaxIds;
        try
        {
            using var httpClientFactory = _serviceProvider.CreateScope();
            var factory = httpClientFactory.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            using var httpClient = factory.CreateClient("HttpClientToServer");
            
            var response = await httpClient.GetAsync("/api/sync/table-maxids");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not fetch server max IDs (status {Status}), skipping PK remapping", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            serverMaxIds = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            if (serverMaxIds == null || serverMaxIds.Count == 0)
            {
                _logger.LogInformation("Server returned empty max IDs, no remapping needed");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch server max IDs, skipping PK remapping (offline?)");
            return;
        }

        // Get the local DB connection string from EF Core
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connectionString = dbContext.Database.GetConnectionString();
        
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogWarning("Cannot get local DB connection string for PK remapping");
            return;
        }

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // Check each table for PK collisions
        var tablesToRemap = new Dictionary<string, (long serverMax, long localMax, long offset)>();
        
        foreach (var (table, serverMax) in serverMaxIds)
        {
            if (serverMax <= 0) continue;
            
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COALESCE(MAX(Id), 0), COUNT(*) FROM [{table}] WHERE Id <= @serverMax";
            cmd.Parameters.AddWithValue("@serverMax", serverMax);
            
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var conflictCount = reader.GetInt64(1);
                if (conflictCount > 0)
                {
                    // Get local max to calculate safe offset
                    using var maxCmd = connection.CreateCommand();
                    maxCmd.CommandText = $"SELECT COALESCE(MAX(Id), 0) FROM [{table}]";
                    var localMax = (long)(await maxCmd.ExecuteScalarAsync() ?? 0L);
                    
                    var offset = Math.Max(serverMax, localMax);
                    tablesToRemap[table] = (serverMax, localMax, offset);
                    _logger.LogInformation("🔄 Table {Table}: {Count} local PKs (≤{ServerMax}) need remapping, offset={Offset}",
                        table, conflictCount, serverMax, offset);
                }
            }
        }

        if (tablesToRemap.Count == 0)
        {
            _logger.LogInformation("✅ No PK collisions detected, no remapping needed");
            return;
        }

        // Execute remapping in a transaction with FK constraints disabled
        _logger.LogInformation("🔄 Remapping PKs for {Count} tables to prevent sync collisions", tablesToRemap.Count);
        
        // Disable FK constraints BEFORE transaction (SQLite ignores PRAGMA inside transactions)
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = OFF;");
        
        using var transaction = connection.BeginTransaction();
        try
        {
            // Process tables in dependency order (parents first)
            var processOrder = new[] {
                "UserProfile", "SkillProfile", "VocabularyList", "VocabularyWord",
                "LearningResource", "ResourceVocabularyMapping", "Challenge",
                "Conversation", "ConversationChunk", "VocabularyProgress", "VocabularyLearningContext"
            };

            foreach (var table in processOrder)
            {
                if (!tablesToRemap.TryGetValue(table, out var info)) continue;
                
                var (serverMax, _, offset) = info;
                
                // Remap the PK in the main table
                var updatePk = $"UPDATE [{table}] SET Id = Id + @offset WHERE Id <= @serverMax";
                await ExecuteNonQueryAsync(connection, updatePk,
                    ("@offset", offset), ("@serverMax", serverMax));
                _logger.LogDebug("  Remapped {Table} PKs: Id += {Offset} where Id <= {ServerMax}", table, offset, serverMax);

                // Update FK references in child tables
                if (FkDependencies.TryGetValue(table, out var children))
                {
                    foreach (var (childTable, fkColumn) in children)
                    {
                        // Check if column exists in this DB (some tables may not have all FKs)
                        var colExists = await ColumnExistsAsync(connection, childTable, fkColumn);
                        if (!colExists)
                        {
                            _logger.LogDebug("  Skipping FK {Table}.{Column} (column doesn't exist)", childTable, fkColumn);
                            continue;
                        }
                        
                        var updateFk = $"UPDATE [{childTable}] SET [{fkColumn}] = [{fkColumn}] + @offset WHERE [{fkColumn}] <= @serverMax AND [{fkColumn}] > 0";
                        await ExecuteNonQueryAsync(connection, updateFk,
                            ("@offset", offset), ("@serverMax", serverMax));
                        _logger.LogDebug("  Updated {Child}.{FK}: += {Offset} where <= {ServerMax}", childTable, fkColumn, offset, serverMax);
                    }
                }

                // Update CoreSync change tracking table entries
                var updateCt = "UPDATE [__CORE_SYNC_CT] SET PK_Int = PK_Int + @offset WHERE TBL = @table AND PK_Int <= @serverMax";
                await ExecuteNonQueryAsync(connection, updateCt,
                    ("@offset", offset), ("@serverMax", serverMax), ("@table", table));
                _logger.LogDebug("  Updated CT entries for {Table}", table);
            }

            transaction.Commit();
            _logger.LogInformation("✅ PK remapping completed successfully for {Count} tables", tablesToRemap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PK remapping failed, rolling back: {Message}", ex.Message);
            transaction.Rollback();
            throw;
        }
        finally
        {
            // Re-enable FK constraints after transaction completes
            await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;");
        }
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{table}])";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, params (string name, object value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
