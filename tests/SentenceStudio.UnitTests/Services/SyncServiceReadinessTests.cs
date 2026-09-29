using CoreSync;
using CoreSync.Http.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

public sealed class SyncServiceReadinessTests
{
    [Fact]
    public async Task RemoteFailure_IsSwallowedByLegacyTrigger_ButNotByReadinessSync()
    {
        var local = new Mock<ISyncProvider>();
        local.Setup(provider => provider.ApplyProvisionAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        local.Setup(provider => provider.GetStoreIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        var remote = new Mock<ISyncProviderHttpClient>();
        remote.Setup(provider => provider.GetStoreIdAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Synthetic remote failure."));

        using var services = new ServiceCollection().BuildServiceProvider();
        var sync = new SyncService(local.Object, remote.Object,
            NullLogger<SyncService>.Instance, services);

        // A normally completed TriggerSyncAsync is deliberately not a success receipt.
        await sync.TriggerSyncAsync();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            ((IRemoteSyncCompletion)sync).SynchronizeForReadinessAsync(CancellationToken.None));

        remote.Verify(provider => provider.GetStoreIdAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ReadinessSync_WaitsForLegacySyncSemaphore_AndHonorsCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = new Mock<ISyncProvider>();
        local.Setup(provider => provider.ApplyProvisionAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Stop the synthetic legacy sync.");
            });

        using var services = new ServiceCollection().BuildServiceProvider();
        var sync = new SyncService(local.Object, new Mock<ISyncProviderHttpClient>().Object,
            NullLogger<SyncService>.Instance, services);
        var legacy = sync.TriggerSyncAsync();

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var readiness = ((IRemoteSyncCompletion)sync)
                .SynchronizeForReadinessAsync(cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness);
            local.Verify(provider => provider.ApplyProvisionAsync(It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            release.TrySetResult();
            await legacy;
        }
    }
}
