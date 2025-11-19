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
            _logger.LogInformation("⏭️ Database already initialized, skipping");
            System.Diagnostics.Debug.WriteLine("⏭️ Database already initialized, skipping");
            return;
        }

        try
        {
            _logger.LogInformation("🚀 Initializing CoreSync provider...");
            System.Diagnostics.Debug.WriteLine("🚀 SyncService.InitializeDatabaseAsync - START");

            // First: Ensure EF Core applies all migrations
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            
            System.Diagnostics.Debug.WriteLine("📊 Running EF Core migrations...");
            await dbContext.Database.MigrateAsync();
            _logger.LogInformation("EF Core database migrated");
            System.Diagnostics.Debug.WriteLine("✅ EF Core migrations complete");

            // Then: Apply CoreSync provisioning to create sync tracking tables
            System.Diagnostics.Debug.WriteLine("📊 Applying CoreSync provisioning...");
            await _localSyncProvider.ApplyProvisionAsync();
            _logger.LogInformation("CoreSync provisioning applied");
            System.Diagnostics.Debug.WriteLine("✅ CoreSync provisioning complete");

            _isInitialized = true;
            System.Diagnostics.Debug.WriteLine("✅ SyncService.InitializeDatabaseAsync - COMPLETE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize CoreSync: {Message}", ex.Message);
            System.Diagnostics.Debug.WriteLine($"❌ SyncService.InitializeDatabaseAsync - ERROR: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"❌ Stack trace: {ex.StackTrace}");
            // throw;
        }
    }

    public async Task TriggerSyncAsync()
    {
        // await InitializeDatabaseAsync();

        // Only allow one sync operation at a time
        if (!await _syncSemaphore.WaitAsync(100)) // Quick timeout to prevent blocking
        {
            _logger.LogDebug("Sync already in progress, skipping");
            return;
        }

        try
        {
            await _localSyncProvider.ApplyProvisionAsync();
            var syncAgent = new SyncAgent(_localSyncProvider, _remoteSyncProvider);
            await syncAgent.SynchronizeAsync(conflictResolutionOnLocalStore: ConflictResolution.ForceWrite);
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
}
