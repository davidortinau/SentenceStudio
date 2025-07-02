using CoreSync;
using CoreSync.Http.Client;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

public interface ISyncService
{
    Task TriggerSyncAsync();
}

public class SyncService : ISyncService
{
    private readonly ISyncProvider _localSyncProvider;
    private readonly ISyncProviderHttpClient _remoteSyncProvider;
    private readonly ILogger<SyncService> _logger;
    private readonly SemaphoreSlim _syncSemaphore = new(1, 1);

    public SyncService(
        ISyncProvider localSyncProvider, 
        ISyncProviderHttpClient remoteSyncProvider,
        ILogger<SyncService> logger)
    {
        _localSyncProvider = localSyncProvider;
        _remoteSyncProvider = remoteSyncProvider;
        _logger = logger;
    }

    public async Task TriggerSyncAsync()
    {
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
