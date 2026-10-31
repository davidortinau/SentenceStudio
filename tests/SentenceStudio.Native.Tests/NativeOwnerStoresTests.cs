using System.Security.Cryptography;
using CoreSync;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Account switching on one device: each owner signs in to its own SQLite file
/// and CoreSync store, the pre-existing database stays with the owner its rows
/// prove, and unsynced rows survive a switch away and back.
/// </summary>
public sealed class NativeOwnerStoresTests
{
    private const string Password = "fixture-password";
    private const string EmailA = "owner-a@example.invalid";
    private const string EmailB = "owner-b@example.invalid";
    private static readonly Guid ServerStore = Guid.Parse("5e7d0000-0000-0000-0000-000000000001");

    [Fact]
    public async Task SignOutAThenSignInB_IsolatesDataAndSyncStore_ThenAResumesWithPendingRows()
    {
        using var fixture = new NativeSliceFixture(ownerStores: true);
        var ownerA = Guid.NewGuid().ToString();
        var ownerB = Guid.NewGuid().ToString();
        fixture.RegisterUser(EmailA, ownerA);
        fixture.RegisterUser(EmailB, ownerB);

        // An existing install: A's database with an unsynced, tracked resource and
        // the stray profile row an earlier B sign-in bootstrapped into it.
        var legacyStore = await CreateLegacyDatabaseAsync(
            fixture.DatabasePath, ownerA, strayProfile: ownerB);
        var router = NativeOwnerStoreRegistration.CreateRouter(fixture.Provider, s => s);
        var stores = fixture.Provider.GetRequiredService<NativeOwnerStores>();

        var auth = fixture.Auth;
        var signedInA = await auth.SignInAsync(EmailA, Password);
        Assert.Equal(NativePhase.Ready, signedInA.Phase);
        Assert.Equal(ownerA, signedInA.UserId);
        Assert.Equal(fixture.DatabasePath, stores.PathForOwner(ownerA));
        Assert.Contains("res-a-pending", await ResourceIdsAsync(fixture));

        await auth.SignOutAsync();
        Assert.Null(stores.ActiveOwner);
        Assert.Throws<UnauthorizedAccessException>(() => stores.ResolveForContext());
        var legacyBytes = Hash(fixture.DatabasePath);

        var signedInB = await auth.SignInAsync(EmailB, Password);
        Assert.Equal(NativePhase.Ready, signedInB.Phase);
        Assert.Equal(ownerB, signedInB.UserId);
        var pathB = stores.PathForOwner(ownerB);
        Assert.NotEqual(fixture.DatabasePath, pathB);
        Assert.Equal(Path.Combine(fixture.Root, "owners", Guid.Parse(ownerB).ToString("N"),
            "native-slice.sqlite"), pathB);
        Assert.Empty(await ResourceIdsAsync(fixture));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(Resource("res-b", ownerB));
            await db.SaveChangesAsync();
        }
        using (var scope = fixture.Scope())
        {
            // A row naming another owner cannot be written into B's database.
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(Resource("res-a-foreign", ownerA));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
        }

        Guid storeB;
        using (Lease(fixture, ownerB))
        {
            await router.ApplyProvisionAsync();
            storeB = await router.GetStoreIdAsync();
            var upload = await router.GetChangesAsync(
                ServerStore, syncDirection: SyncDirection.UploadOnly);
            Assert.DoesNotContain(upload.Items, item => RowId(item) == "res-a-pending");
        }
        Assert.NotEqual(legacyStore, storeB);
        Assert.Equal(["res-b"], await ResourceIdsAsync(fixture));

        await auth.SignOutAsync();
        Assert.Equal(legacyBytes, Hash(fixture.DatabasePath));

        var backToA = await auth.SignInAsync(EmailA, Password);
        Assert.Equal(NativePhase.Ready, backToA.Phase);
        Assert.Equal(ownerA, backToA.UserId);
        Assert.Equal(["res-a-pending"], await ResourceIdsAsync(fixture));
        using (Lease(fixture, ownerA))
        {
            Assert.Equal(legacyStore, await router.GetStoreIdAsync());
            var upload = await router.GetChangesAsync(
                ServerStore, syncDirection: SyncDirection.UploadOnly);
            Assert.Contains(upload.Items, item => RowId(item) == "res-a-pending");
            Assert.DoesNotContain(upload.Items, item => RowId(item) == "res-b");
        }
    }

    [Fact]
    public async Task RevokedLeaseAndForeignSyncOwner_CannotReachTheNextOwnersDatabase()
    {
        using var fixture = new NativeSliceFixture(ownerStores: true);
        var ownerA = Guid.NewGuid().ToString();
        var ownerB = Guid.NewGuid().ToString();
        fixture.RegisterUser(EmailA, ownerA);
        fixture.RegisterUser(EmailB, ownerB);
        var stores = fixture.Provider.GetRequiredService<NativeOwnerStores>();
        var router = NativeOwnerStoreRegistration.CreateRouter(fixture.Provider, s => s);

        var auth = fixture.Auth;
        Assert.Equal(NativePhase.Ready, (await auth.SignInAsync(EmailA, Password)).Phase);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception> staleA;
        using (Lease(fixture, ownerA))
        {
            staleA = Task.Run(async () =>
            {
                await release.Task;
                return Record.Exception(() => stores.ResolveForContext())
                    ?? await Record.ExceptionAsync(() => router.GetStoreIdAsync())
                    ?? new InvalidOperationException("The revoked lease reached a database.");
            });
        }

        await auth.SignOutAsync();
        Assert.Equal(NativePhase.Ready, (await auth.SignInAsync(EmailB, Password)).Phase);
        release.SetResult();
        Assert.IsType<UnauthorizedAccessException>(await staleA);

        // Only B's own lease reaches B's store; no lease reaches nothing.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.GetStoreIdAsync());
        Assert.Throws<UnauthorizedAccessException>(() => stores.PathForSync(ownerA));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => stores.ActivateAsync("not-a-profile-id", CancellationToken.None));
    }

    [Fact]
    public void OwnerRoutedHead_ResolvesCoreSyncThroughTheOwnerRouter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DeviceUserScopeProvider());
        services.AddSingleton<SentenceStudio.Abstractions.IPreferencesService>(
            new NativeSliceFixture.ScratchPreferences());
        services.AddNativeOwnerSqlite(sp => new NativeOwnerStores(
            Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.sqlite",
            sp.GetRequiredService<DeviceUserScopeProvider>(),
            NullLogger<NativeOwnerStores>.Instance));
        services.AddSyncServices($"unused-{Guid.NewGuid():N}.sqlite",
            new Uri("https://api.example.invalid/"), downloadOnlyProfile: true);
        services.AddOwnerBoundNativeSyncReadiness();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<NativeOwnerSyncProviderRouter>(provider.GetRequiredService<ISyncProvider>());
        using var scope = provider.CreateScope();
        Assert.Throws<UnauthorizedAccessException>(
            () => scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    [Fact]
    public async Task ExistingDatabaseWithTwoOwnersRowsStaysUnassignedAndUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "sentencestudio-native-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacy = Path.Combine(root, "native.sqlite");
            var ownerA = Guid.NewGuid().ToString();
            var ownerB = Guid.NewGuid().ToString();
            await CreateLegacyDatabaseAsync(legacy, ownerA, strayProfile: null,
                secondOwnerResource: ownerB);
            var before = Hash(legacy);
            var stores = new NativeOwnerStores(root, "native.sqlite",
                new DeviceUserScopeProvider(), NullLogger<NativeOwnerStores>.Instance,
                prepare: (db, _, ct) => db.Database.EnsureCreatedAsync(ct));

            var pathA = await stores.ActivateAsync(ownerA, CancellationToken.None);
            var pathB = await stores.ActivateAsync(ownerB, CancellationToken.None);

            Assert.NotEqual(legacy, pathA);
            Assert.NotEqual(legacy, pathB);
            Assert.NotEqual(pathA, pathB);
            Assert.Equal(NativeOwnerStores.UnassignedMarker,
                File.ReadAllText(legacy + NativeOwnerStores.OwnerMarkerSuffix));
            Assert.Equal(before, Hash(legacy));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingDatabaseWithOnlyOneProfileBelongsToThatProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "sentencestudio-native-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacy = Path.Combine(root, "native.sqlite");
            var owner = Guid.NewGuid();
            await using (var db = Context(legacy))
            {
                await db.Database.EnsureCreatedAsync();
                db.UserProfiles.Add(Profile(owner.ToString()));
                await db.SaveChangesAsync();
            }

            Assert.Equal(owner,
                await NativeOwnerStores.DetermineLegacyOwnerAsync(legacy, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Guid> CreateLegacyDatabaseAsync(
        string path, string owner, string? strayProfile, string? secondOwnerResource = null)
    {
        await using (var db = Context(path))
            await db.Database.EnsureCreatedAsync();
        var provider = new SqliteSyncProvider(
            new SqliteSyncConfigurationBuilder($"Data Source={path}")
                .ConfigureSyncTables(downloadOnlyProfile: true).Build(),
            ProviderMode.Local);
        await provider.ApplyProvisionAsync();
        await using (var db = Context(path))
        {
            db.UserProfiles.Add(Profile(owner));
            if (strayProfile is not null)
                db.UserProfiles.Add(Profile(strayProfile));
            db.LearningResources.Add(Resource("res-a-pending", owner));
            if (secondOwnerResource is not null)
                db.LearningResources.Add(Resource("res-second", secondOwnerResource));
            await db.SaveChangesAsync();
        }
        var store = await provider.GetStoreIdAsync();
        SqliteConnection.ClearAllPools();
        return store;
    }

    private static DeviceUserScopeProvider.ReadinessLease Lease(
        NativeSliceFixture fixture, string owner) =>
        fixture.UserScope.TryBeginReadiness(owner, fixture.UserScope.CaptureStamp())
        ?? throw new InvalidOperationException("The owner readiness lease was refused.");

    private static async Task<List<string>> ResourceIdsAsync(NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.LearningResources.AsNoTracking()
            .Select(resource => resource.Id).OrderBy(id => id).ToListAsync();
    }

    private static string? RowId(SyncItem item) =>
        item.TableName == "LearningResource" && item.Values.TryGetValue("Id", out var id)
            ? id?.Value as string : null;

    private static ApplicationDbContext Context(string path) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);

    private static UserProfile Profile(string id) => new()
    {
        Id = id,
        Name = "Synthetic",
        Email = $"{id}@example.invalid",
        NativeLanguage = "English",
        TargetLanguage = "Korean",
        CreatedAt = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc)
    };

    private static LearningResource Resource(string id, string owner) => new()
    {
        Id = id,
        UserProfileId = owner,
        Language = "Korean",
        MediaType = "Vocabulary List",
        Title = "Synthetic vocabulary",
        CreatedAt = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc)
    };

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
