using SentenceStudio.Abstractions;
using System.Collections.Concurrent;
using CoreSync;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// One SQLite file, and therefore one CoreSync store ID, per signed-in owner on
/// a device. The API binds a native store ID to its first owner, so a second
/// account can only sync from its own file. The pre-existing single database
/// stays where it is and is adopted only by the one owner its rows prove;
/// it is never moved, copied over, or rewritten to change hands.
/// </summary>
public sealed class NativeOwnerStores
{
    internal const string OwnersFolder = "owners";
    internal const string OwnerMarkerSuffix = ".owner";
    internal const string UnassignedMarker = "unassigned";

    private readonly string _root;
    private readonly string _fileName;
    private readonly string? _fixedPath;
    private readonly string? _backupDirectory;
    private readonly Action<ApplicationDbContext>? _afterMigrate;
    private readonly Func<ApplicationDbContext, string, CancellationToken, Task>? _prepare;
    private readonly DeviceUserScopeProvider _scope;
    private readonly ILogger<NativeOwnerStores> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _migrated =
        new(StringComparer.Ordinal);
    private volatile ActiveStore? _active;
    private IPreferencesService? _claimPreferences;

    private sealed record ActiveStore(string Owner, string Path);

    public NativeOwnerStores(
        string root, string legacyFileName, DeviceUserScopeProvider scope,
        ILogger<NativeOwnerStores> logger, string? backupDirectory = null,
        Action<ApplicationDbContext>? afterMigrate = null,
        Func<ApplicationDbContext, string, CancellationToken, Task>? prepare = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyFileName);
        if (legacyFileName.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("The database name must be a file name.",
                nameof(legacyFileName));
        _root = root;
        _fileName = legacyFileName;
        _scope = scope;
        _logger = logger;
        _backupDirectory = backupDirectory;
        _afterMigrate = afterMigrate;
        _prepare = prepare;
    }

    private NativeOwnerStores(string fixedPath, DeviceUserScopeProvider scope,
        ILogger<NativeOwnerStores> logger)
    {
        _fixedPath = fixedPath;
        _root = Path.GetDirectoryName(fixedPath) ?? ".";
        _fileName = Path.GetFileName(fixedPath);
        _scope = scope;
        _logger = logger;
    }

    /// <summary>Debug preview fixtures keep their single isolated database.</summary>
    public static NativeOwnerStores Fixed(string path, DeviceUserScopeProvider scope,
        ILogger<NativeOwnerStores> logger) => new(path, scope, logger);

    public bool IsFixed => _fixedPath is not null;

    public string LegacyPath => Path.Combine(_root, _fileName);

    public string? ActiveOwner => _active?.Owner;

    /// <summary>The owner's file. Owner IDs are server GUIDs; anything else fails closed.</summary>
    public string PathForOwner(string owner)
    {
        if (_fixedPath is not null)
            return _fixedPath;
        var id = ParseOwner(owner);
        return ReadLegacyMarker() == id.ToString("N")
            ? LegacyPath
            : Path.Combine(_root, OwnersFolder, id.ToString("N"), _fileName);
    }

    /// <summary>
    /// Selects and migrates the authenticated owner's database. Call after the
    /// token owner is verified and before that owner's first local read or write.
    /// </summary>
    public async Task<string> ActivateAsync(string owner, CancellationToken cancellationToken)
    {
        if (_fixedPath is not null)
            return _fixedPath;
        var id = ParseOwner(owner);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is { } current && current.Owner == owner)
                return current.Path;
            _active = null;
            await EnsureLegacyAssignmentAsync(cancellationToken);
            var path = PathForOwner(owner);
            if (path != LegacyPath)
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!_migrated.ContainsKey(path))
            {
                await using var db = CreateContext(path);
                if (_prepare is not null)
                    await _prepare(db, path, cancellationToken);
                else
                    await SqliteMigrationBackup.MigrateWithBackupAsync(
                        db, path, _logger, cancellationToken, _backupDirectory);
                _afterMigrate?.Invoke(db);
                if (_claimPreferences is { } preferences)
                    await ClaimLegacyQuizSavesOnceAsync(db, owner, preferences, cancellationToken);
                if (path != LegacyPath)
                    await AdoptUnownedRowsAsync(db, owner, cancellationToken);
                _migrated[path] = true;
            }
            _active = new ActiveStore(owner, path);
            _logger.LogInformation("Native owner store activated ({Store}).",
                path == LegacyPath ? "existing device database" : "owner database");
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Quiz runs saved by a native build from before they synced (2026-11-03) carry no
    /// <c>savedBy</c>, but only this device could have saved them. Mark the owner's runs as this
    /// install's, so this device still finishes its own graded answer and other devices treat it
    /// as this install's (docs/activity-session-format.md). Web/MAUI saves record no plan row and
    /// are left alone. Runs once per owner database, at its first activation by a build that
    /// syncs runs, so before that database's first sync: afterwards a run without
    /// <c>savedBy</c> may be another client's, downloaded, and is never claimed.
    /// </summary>
    internal static async Task ClaimLegacyQuizSavesOnceAsync(
        ApplicationDbContext db, string owner, IPreferencesService preferences,
        CancellationToken cancellationToken)
    {
        var key = LegacyQuizClaimKey + owner;
        if (preferences.Get(key, false))
            return;
        await ClaimLegacyQuizSavesAsync(
            db, owner, NativeInstallId.Get(preferences), cancellationToken);
        // Set after the claim: a stop in between repeats it, still before any sync.
        preferences.Set(key, true);
    }

    internal const string LegacyQuizClaimKey = "native.legacyQuizRunsClaimed.";

    /// <summary>
    /// Scene images, custom scenarios and example sentences had no owner before phase 3 of
    /// docs/sync-all-tables-plan.md. Every row in a per-learner database was written while that
    /// learner was signed in, so it is theirs (Captain 2026-11-03); it gets the owner here and
    /// then syncs. The pre-account device database is never passed here: its rows stay unowned
    /// and local (D3). Built-in scenarios belong to nobody. Runs at activation, before any sync;
    /// the server never sends a row without an owner, so later runs find nothing.
    /// </summary>
    internal static async Task<int> AdoptUnownedRowsAsync(
        ApplicationDbContext db, string owner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return await db.Database.ExecuteSqlRawAsync("""
            UPDATE "SceneImage" SET "UserProfileId" = {0} WHERE "UserProfileId" IS NULL;
            UPDATE "ConversationScenario" SET "UserProfileId" = {0}
                WHERE "UserProfileId" IS NULL AND "IsPredefined" = 0 AND "Id" NOT LIKE 'scn:%';
            UPDATE "ExampleSentence" SET "UserProfileId" = {0} WHERE "UserProfileId" IS NULL;
            """, [owner], cancellationToken);
    }

    internal static async Task<int> ClaimLegacyQuizSavesAsync(
        ApplicationDbContext db, string owner, string installId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(installId);
        return await db.Database.ExecuteSqlRawAsync("""
            UPDATE "ActivitySession" SET "StateJson" = json_set("StateJson", '$.savedBy', {0})
            WHERE "UserId" = {1} AND "ActivityType" = 'VocabularyReview' AND json_valid("StateJson")
                AND json_type("StateJson", '$.snapshot') = 'object'
                AND json_type("StateJson", '$.planItemRowId') = 'text'
                AND json_type("StateJson", '$.savedBy') IS NULL
            """, [installId, owner], cancellationToken);
    }

    /// <summary>Preferences that hold this install's ID, for claiming saves made before quiz runs synced.</summary>
    internal void UseLegacyQuizClaim(IPreferencesService preferences) =>
        _claimPreferences = preferences;

    /// <summary>Sign-out or a refused sign-in leaves no owner database selectable.</summary>
    public void Deactivate()
    {
        if (_fixedPath is null)
            _active = null;
    }

    /// <summary>
    /// The database a new DbContext must open. A revoked readiness lease, a
    /// scope owner other than the active store's, or no active store throws.
    /// </summary>
    public (string Path, string? Owner) ResolveForContext()
    {
        if (_fixedPath is not null)
            return (_fixedPath, null);
        if (_scope.HasRevokedReadinessLease)
            throw new UnauthorizedAccessException(
                "A revoked owner operation cannot open a local database.");
        var active = _active
            ?? throw new UnauthorizedAccessException("No owner database is active.");
        if (_scope.TryGetUserProfileId(out var scoped)
            && !string.Equals(scoped, active.Owner, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "The active device owner does not own the open local database.");
        return (active.Path, active.Owner);
    }

    /// <summary>CoreSync may only touch the leased owner's own, active database.</summary>
    public string PathForSync(string leasedOwner)
    {
        if (_fixedPath is not null)
            return _fixedPath;
        if (_active is not { } active
            || !string.Equals(active.Owner, leasedOwner, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "Native sync owner does not match the active owner database.");
        return active.Path;
    }

    private static Guid ParseOwner(string owner) =>
        Guid.TryParse(owner, out var id) && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException("A native owner database requires a profile ID.");

    private string MarkerPath => LegacyPath + OwnerMarkerSuffix;

    private string? ReadLegacyMarker()
    {
        try
        {
            return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "The existing device database owner marker is unreadable.");
            return UnassignedMarker;
        }
    }

    /// <summary>
    /// The pre-owner database belongs to the single profile its owned rows name.
    /// No owned rows means the single local profile; anything else stays unassigned
    /// (kept intact on disk, adopted by nobody) rather than guessing.
    /// </summary>
    private async Task EnsureLegacyAssignmentAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(MarkerPath) || !File.Exists(LegacyPath))
            return;
        var owner = await DetermineLegacyOwnerAsync(LegacyPath, cancellationToken);
        var marker = owner is { } id ? id.ToString("N") : UnassignedMarker;
        var temporary = MarkerPath + ".tmp";
        await File.WriteAllTextAsync(temporary, marker, cancellationToken);
        File.Move(temporary, MarkerPath, overwrite: false);
        if (owner is null)
            _logger.LogWarning(
                "The existing device database has no single provable owner; it is kept unassigned.");
        else
            _logger.LogInformation("The existing device database was assigned to its proven owner.");
    }

    internal static async Task<Guid?> DetermineLegacyOwnerAsync(
        string path, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, column) in OwnedColumns)
        {
            if (!await HasColumnAsync(connection, table, column, cancellationToken))
                continue;
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT DISTINCT \"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL AND \"{column}\" <> ''";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                owners.Add(reader.GetString(0));
        }

        if (owners.Count == 0 && await HasColumnAsync(connection, "UserProfile", "Id",
                cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Id\" FROM \"UserProfile\" LIMIT 2";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                owners.Add(reader.GetString(0));
        }

        return owners.Count == 1 && Guid.TryParse(owners.Single(), out var owner)
            && owner != Guid.Empty
                ? owner : null;
    }

    /// <summary>
    /// Every owner-bearing table and column in the model (UserProfileId or UserId),
    /// so adoption checks all of an owner's rows, not a hand-kept subset.
    /// </summary>
    internal static readonly IReadOnlyList<(string Table, string Column)> OwnedColumns =
        BuildOwnedColumns();

    private static IReadOnlyList<(string Table, string Column)> BuildOwnedColumns()
    {
        using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite("Data Source=:memory:").Options);
        return context.Model.GetEntityTypes()
            .Where(entity => entity.ClrType != typeof(UserProfile)
                && entity.GetTableName() is not null)
            .SelectMany(entity => new[] { "UserProfileId", "UserId" }
                .Select(entity.FindProperty)
                .Where(property => property is not null && property.ClrType == typeof(string))
                .Select(property => (entity.GetTableName()!, property!.GetColumnName())))
            .Distinct()
            .OrderBy(item => item.Item1, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection,
        string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info(@table) WHERE name = @column";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static ApplicationDbContext CreateContext(string path) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
}

/// <summary>
/// Refuses an EF write of an owned row that names a different owner than the
/// database file it is being written to. CoreSync applies bypass EF; the API
/// separately refuses foreign rows in uploads.
/// </summary>
internal sealed class NativeOwnerStoreWriteGuard(string owner) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Check(DbContext? context)
    {
        if (context is null)
            return;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)
                || entry.Entity is VocabularyWord)
                continue;
            var property = entry.Entity is UserProfile
                ? "Id"
                : entry.Metadata.FindProperty("UserProfileId") is not null
                    ? "UserProfileId"
                    : "UserId";
            if (entry.Metadata.FindProperty(property) is null)
                continue;
            if (entry.Property(property).CurrentValue is string value
                && value.Length > 0
                && !string.Equals(value, owner, StringComparison.Ordinal))
                throw new UnauthorizedAccessException(
                    "A local write named an owner other than this database's owner.");
        }
    }
}

/// <summary>
/// Routes each CoreSync call to the database of the readiness lease that is
/// running it. Without a current lease for the active owner the call throws.
/// </summary>
internal sealed class NativeOwnerSyncProviderRouter(
    NativeOwnerStores stores,
    DeviceUserScopeProvider scope,
    Func<string, ISyncProvider> create) : ISyncProvider, ISyncFollowUpSource
{
    public bool TakeFollowUpRequest() =>
        Current is ISyncFollowUpSource followUp && followUp.TakeFollowUpRequest();

    private readonly ConcurrentDictionary<string, ISyncProvider> _providers =
        new(StringComparer.Ordinal);

    internal ISyncProvider Current
    {
        get
        {
            if (!scope.TryGetLeasedReadinessOwner(out var owner))
                throw new UnauthorizedAccessException("Native sync requires a verified owner.");
            var path = stores.PathForSync(owner);
            return _providers.GetOrAdd(path, create);
        }
    }

    public string[]? SyncTableNames => Current.SyncTableNames;
    public Task<Guid> GetStoreIdAsync(CancellationToken cancellationToken = default) =>
        Current.GetStoreIdAsync(cancellationToken);
    public Task SaveVersionForStoreAsync(Guid otherStoreId, long version,
        CancellationToken cancellationToken = default) =>
        Current.SaveVersionForStoreAsync(otherStoreId, version, cancellationToken);
    public Task<SyncAnchor> ApplyChangesAsync(SyncChangeSet changeSet,
        Func<SyncItem, ConflictResolution>? onConflictFunc = null,
        CancellationToken cancellationToken = default) =>
        Current.ApplyChangesAsync(changeSet, onConflictFunc, cancellationToken);
    public Task ApplyProvisionAsync(CancellationToken cancellationToken = default) =>
        Current.ApplyProvisionAsync(cancellationToken);
    public Task RemoveProvisionAsync(CancellationToken cancellationToken = default) =>
        Current.RemoveProvisionAsync(cancellationToken);
    public Task<SyncVersion> GetSyncVersionAsync(CancellationToken cancellationToken = default) =>
        Current.GetSyncVersionAsync(cancellationToken);
    public Task<SyncVersion> ApplyRetentionPolicyAsync(int minVersion,
        CancellationToken cancellationToken = default) =>
        Current.ApplyRetentionPolicyAsync(minVersion, cancellationToken);
    public Task EnableChangeTrackingForTable(string tableName,
        CancellationToken cancellationToken = default) =>
        Current.EnableChangeTrackingForTable(tableName, cancellationToken);
    public Task DisableChangeTrackingForTable(string tableName,
        CancellationToken cancellationToken = default) =>
        Current.DisableChangeTrackingForTable(tableName, cancellationToken);
    public Task<SyncChangeSet> GetChangesAsync(Guid otherStoreId,
        SyncFilterParameter[]? syncFilterParameters = null,
        SyncDirection syncDirection = SyncDirection.UploadAndDownload,
        string[]? tables = null, CancellationToken cancellationToken = default) =>
        Current.GetChangesAsync(otherStoreId, syncFilterParameters, syncDirection, tables,
            cancellationToken);
}

public static class NativeOwnerStoreRegistration
{
    /// <summary>
    /// Registers owner-routed SQLite: <see cref="ApplicationDbContext"/> opens the
    /// active owner's file and refuses foreign-owner writes. Call before
    /// <c>AddOwnerBoundNativeSyncReadiness</c>, which then routes CoreSync too.
    /// </summary>
    public static IServiceCollection AddNativeOwnerSqlite(
        this IServiceCollection services, Func<IServiceProvider, NativeOwnerStores> create)
    {
        services.TryAddSingleton(provider =>
        {
            var stores = create(provider);
            // Both app heads register preferences; a host without them claims nothing.
            if (provider.GetService<IPreferencesService>() is { } preferences)
                stores.UseLegacyQuizClaim(preferences);
            return stores;
        });
        services.AddDbContext<ApplicationDbContext>((provider, options) =>
        {
            var (path, owner) = provider.GetRequiredService<NativeOwnerStores>()
                .ResolveForContext();
            options.UseSqlite($"Data Source={path};Pooling=False");
            if (owner is not null)
                options.AddInterceptors(new NativeOwnerStoreWriteGuard(owner));
        });
        return services;
    }

    internal static ISyncProvider CreateRouter(IServiceProvider provider,
        Func<SqliteSyncProvider, ISyncProvider> decorate)
    {
        var stores = provider.GetRequiredService<NativeOwnerStores>();
        var logger = provider.GetRequiredService<ILogger<NativeOwnerSyncProviderRouter>>();
        return new NativeOwnerSyncProviderRouter(
            stores, provider.GetRequiredService<DeviceUserScopeProvider>(),
            path => decorate(new SqliteSyncProvider(
                new SqliteSyncConfigurationBuilder($"Data Source={path}")
                    .ConfigureSyncTables(downloadOnlyProfile: true).Build(),
                ProviderMode.Local, new CoreSyncLogger(logger))));
    }

    private sealed class CoreSyncLogger(ILogger logger) : ISyncLogger
    {
        public void Trace(string message) => logger.LogTrace("{Message}", message);
        public void Info(string message) => logger.LogInformation("Sync: {Message}", message);
        public void Warning(string message) => logger.LogWarning("Sync: {Message}", message);
        public void Error(string message) => logger.LogError("Sync: {Message}", message);
    }
}
