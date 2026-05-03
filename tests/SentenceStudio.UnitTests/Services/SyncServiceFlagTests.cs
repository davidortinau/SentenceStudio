// CRITICAL REGRESSION TESTS — ISyncService.IsInitialSyncInProgress flag lifecycle
// These tests exist because the post-login navigation race (issue #187) requires the
// flag to flip to true SYNCHRONOUSLY (via BeginInitialSync) before the background
// Task.Run that calls TriggerSyncAsync starts. They also pin down that the flag is
// always cleared and InitialSyncCompleted always fires on failure — so the MainLayout
// sync overlay can never get stuck when the backend is unreachable.
// DO NOT DELETE OR WEAKEN THESE TESTS.

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

public class SyncServiceFlagTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public SyncServiceFlagTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts => opts.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private SyncService BuildSut(Mock<CoreSync.ISyncProvider>? localProvider = null)
    {
        return new SyncService(
            (localProvider ?? new Mock<CoreSync.ISyncProvider>()).Object,
            new Mock<CoreSync.Http.Client.ISyncProviderHttpClient>().Object,
            NullLogger<SyncService>.Instance,
            _serviceProvider);
    }

    [Fact]
    public void BeginInitialSync_FlipsFlagSynchronously()
    {
        // Arrange
        var sut = BuildSut();
        sut.IsInitialSyncInProgress.Should().BeFalse("flag starts false");

        // Act — must be synchronous (no await), so MainLayout's first render sees true.
        sut.BeginInitialSync();

        // Assert
        sut.IsInitialSyncInProgress.Should().BeTrue(
            "BeginInitialSync must flip the flag in the same call so MainLayout can show the overlay before navigation");
    }

    [Fact]
    public async Task TriggerSyncAsync_OnFailure_ClearsFlagAndFiresCompletedEvent()
    {
        // Arrange — local provider's ApplyProvisionAsync throws, so SyncAgent doesn't start.
        // SyncService catches the exception and MUST still clear the flag + fire the event,
        // otherwise MainLayout's sync overlay would be stuck forever on a transient backend outage.
        var localProvider = new Mock<CoreSync.ISyncProvider>();
        localProvider
            .Setup(p => p.ApplyProvisionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated sync failure"));

        var sut = BuildSut(localProvider);
        sut.BeginInitialSync();

        var completedFired = false;
        sut.InitialSyncCompleted += () => completedFired = true;

        // Act — must NOT rethrow; SyncService swallows the error after logging.
        await sut.TriggerSyncAsync();

        // Assert
        sut.IsInitialSyncInProgress.Should().BeFalse(
            "flag must clear even when sync fails so user can proceed past the overlay");
        completedFired.Should().BeTrue(
            "InitialSyncCompleted must fire on failure too — otherwise the spinner is stuck forever");
    }

    [Fact]
    public async Task TriggerSyncAsync_WhenNoInitialSyncInProgress_DoesNotFireCompletedEvent()
    {
        // Arrange — TriggerSyncAsync is also called for ad-hoc post-login sync (not initial).
        // In that case, the InitialSyncCompleted event must NOT fire spuriously.
        var localProvider = new Mock<CoreSync.ISyncProvider>();
        localProvider
            .Setup(p => p.ApplyProvisionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated"));

        var sut = BuildSut(localProvider);
        // NOTE: we deliberately do NOT call BeginInitialSync — flag stays false.

        var completedFired = false;
        sut.InitialSyncCompleted += () => completedFired = true;

        // Act
        await sut.TriggerSyncAsync();

        // Assert
        sut.IsInitialSyncInProgress.Should().BeFalse();
        completedFired.Should().BeFalse(
            "InitialSyncCompleted is reserved for the post-login initial sync — non-initial syncs must not fire it");
    }

    [Fact]
    public void NoOpSyncService_BeginInitialSync_DoesNotThrow()
    {
        // Arrange — webapp uses NoOpSyncService; BeginInitialSync must be a safe no-op.
        var noOp = new NoOpSyncService();

        // Act + Assert
        var act = () => noOp.BeginInitialSync();
        act.Should().NotThrow();
        noOp.IsInitialSyncInProgress.Should().BeFalse(
            "NoOpSyncService never reports a sync in progress (server doesn't sync to itself)");
    }
}
