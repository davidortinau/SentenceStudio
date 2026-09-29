using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidAuthCompositionTests
{
    [Fact]
    public void NormalHeadLeavesOwnerBoundSyncDisabled()
    {
        var root = Path.GetFullPath(typeof(AndroidAuthCompositionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);
        var source = File.ReadAllText(Path.Combine(root, "AndroidServiceHost.cs"));

        Assert.Contains("services.AddHttpClient(\"AuthClient\"", source);
        Assert.Contains("services.AddSyncServices(databasePath, apiUri);", source);
        Assert.Contains("services.AddTransient<AuthenticatedHttpMessageHandler>();", source);
        Assert.Contains("services.AddSingleton<ISyncService, SyncService>();", source);
        Assert.Contains("services.AddSentenceStudioNativeSlice();", source);
        Assert.Contains("services.AddSentenceStudioNativeIdentityAuth();", source);
        Assert.DoesNotContain("services.AddOwnerBoundNativeSyncReadiness();", source);
        Assert.DoesNotContain("services.AddSentenceStudioNativeProductionAdapters();", source);
    }

    [Fact]
    public async Task NativeSliceOnlineWithoutExplicitSyncOptIn_RemainsUnconfirmed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService>(new OnlineConnectivity());
        services.AddSentenceStudioNativeSlice();
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var readiness = provider.GetRequiredService<INativeSyncReadiness>();
        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await readiness.SynchronizeAsync("native-owner-a", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            readiness.SynchronizeAsync("", CancellationToken.None));
    }

    private sealed class OnlineConnectivity : IConnectivityService
    {
        public bool IsInternetAvailable => true;
    }
}
