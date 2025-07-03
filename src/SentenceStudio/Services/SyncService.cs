using CoreSync;
using CoreSync.Http.Client;
using Microsoft.Extensions.Logging;

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
    private readonly SemaphoreSlim _syncSemaphore = new(1, 1);
    private bool _isInitialized = false;

    public SyncService(
        ISyncProvider localSyncProvider, 
        ISyncProviderHttpClient remoteSyncProvider,
        ILogger<SyncService> logger)
    {
        _localSyncProvider = localSyncProvider;
        _remoteSyncProvider = remoteSyncProvider;
        _logger = logger;
    }

    public async Task InitializeDatabaseAsync()
    {
        if (_isInitialized) return;

        try
        {
            _logger.LogInformation("Initializing CoreSync provider...");

            // Apply CoreSync provisioning to create sync tracking tables
            await _localSyncProvider.ApplyProvisionAsync();
            _logger.LogInformation("CoreSync provisioning applied");

            _isInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize CoreSync: {Message}", ex.Message);
            throw;
        }
    }

    public async Task TriggerSyncAsync()
    {
        await InitializeDatabaseAsync();

        // Only allow one sync operation at a time
        if (!await _syncSemaphore.WaitAsync(100)) // Quick timeout to prevent blocking
        {
            _logger.LogDebug("Sync already in progress, skipping");
            return;
        }

        try
        {
            var syncAgent = new SyncAgent(_localSyncProvider, _remoteSyncProvider);
            await syncAgent.SynchronizeAsync();
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
