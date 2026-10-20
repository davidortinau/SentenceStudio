using CoreSync;
using CoreSync.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Coalesces native mapping inserts undone before the server saw them. All other
/// CoreSync changes, including deletes of previously uploaded mappings, retain
/// their normal fail-closed server behavior.
/// </summary>
internal sealed class NativeLocalOnlyMappingExportProvider(
    SqliteSyncProvider local,
    ISyncProviderBase remote,
    Func<string?> activeOwner,
    ILogger<NativeLocalOnlyMappingExportProvider> logger) : ISyncProvider
{
    private const string MappingTable = "ResourceVocabularyMapping";
    private const string ResourceTable = "LearningResource";

    public string[]? SyncTableNames => local.SyncTableNames;

    public Task<Guid> GetStoreIdAsync(CancellationToken cancellationToken = default) =>
        local.GetStoreIdAsync(cancellationToken);

    public Task SaveVersionForStoreAsync(
        Guid otherStoreId, long version, CancellationToken cancellationToken = default) =>
        local.SaveVersionForStoreAsync(otherStoreId, version, cancellationToken);

    public Task<SyncAnchor> ApplyChangesAsync(
        SyncChangeSet changeSet, Func<SyncItem, ConflictResolution>? onConflictFunc = null,
        CancellationToken cancellationToken = default) =>
        local.ApplyChangesAsync(changeSet, onConflictFunc, cancellationToken);

    public Task ApplyProvisionAsync(CancellationToken cancellationToken = default) =>
        local.ApplyProvisionAsync(cancellationToken);

    public Task RemoveProvisionAsync(CancellationToken cancellationToken = default) =>
        local.RemoveProvisionAsync(cancellationToken);

    public Task<SyncVersion> GetSyncVersionAsync(CancellationToken cancellationToken = default) =>
        local.GetSyncVersionAsync(cancellationToken);

    public Task<SyncVersion> ApplyRetentionPolicyAsync(
        int minVersion, CancellationToken cancellationToken = default) =>
        local.ApplyRetentionPolicyAsync(minVersion, cancellationToken);

    public Task EnableChangeTrackingForTable(
        string tableName, CancellationToken cancellationToken = default) =>
        local.EnableChangeTrackingForTable(tableName, cancellationToken);

    public Task DisableChangeTrackingForTable(
        string tableName, CancellationToken cancellationToken = default) =>
        local.DisableChangeTrackingForTable(tableName, cancellationToken);

    public async Task<SyncChangeSet> GetChangesAsync(
        Guid otherStoreId, SyncFilterParameter[]? syncFilterParameters = null,
        SyncDirection syncDirection = SyncDirection.UploadAndDownload,
        string[]? tables = null, CancellationToken cancellationToken = default)
    {
        var changes = await local.GetChangesAsync(
            otherStoreId, syncFilterParameters, syncDirection, tables, cancellationToken);
        if (syncDirection != SyncDirection.UploadOnly
            || !changes.Items.Any(IsMappingDelete)
            || activeOwner() is not { Length: > 0 } owner
            || !Guid.TryParse(owner, out var ownerId)
            || ownerId == Guid.Empty)
            return changes;

        var localId = await local.GetStoreIdAsync(cancellationToken);
        if (changes.SourceAnchor.StoreId != localId || otherStoreId == Guid.Empty)
            return changes;

        var proof = await FindLocalUndoPairsAsync(
            changes, otherStoreId, owner, cancellationToken);
        if (proof is null || proof.InsertVersions.Count == 0)
        {
            logger.LogInformation(
                "Native sync retained {Table} deletes: local proof unavailable at version {Version}.",
                MappingTable, changes.SourceAnchor.Version);
            return changes;
        }

        // The server writes its REMOTE_VERSION in the same transaction as the
        // upload. A client can crash after that commit but before SaveVersion:
        // its local anchor alone cannot prove that an insert was never sent.
        // Pinned CoreSync HTTP requires a nonempty table filter to transmit it.
        var server = await remote.GetChangesAsync(
            localId, null, SyncDirection.DownloadOnly, [ "UserProfile" ],
            cancellationToken);
        if (server.SourceAnchor.StoreId != otherStoreId
            || server.TargetAnchor.StoreId != localId
            || server.TargetAnchor.Version < proof.LocalAnchor
            || !string.Equals(activeOwner(), owner, StringComparison.Ordinal)
            || !await StillAtVersionAsync(proof, otherStoreId, cancellationToken))
        {
            logger.LogInformation(
                "Native sync retained {Table} deletes: receipt or local version changed at anchor {Anchor}.",
                MappingTable, proof.LocalAnchor);
            return changes;
        }

        var safeIds = proof.InsertVersions
            .Where(pair => pair.Value > server.TargetAnchor.Version)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (safeIds.Count == 0)
            return changes;

        foreach (var id in safeIds)
            logger.LogInformation(
                "Native sync coalesced local {Table} undo {Id} after anchor {Anchor}.",
                MappingTable, id, proof.LocalAnchor);

        return new SyncChangeSet(changes.SourceAnchor, changes.TargetAnchor,
            changes.Items.Where(item => !IsMappingDelete(item)
                || !safeIds.Contains((string)item.Values["Id"].Value!)).ToArray());
    }

    private static bool IsMappingDelete(SyncItem item) =>
        item.TableName == MappingTable
        && item.ChangeType == ChangeType.Delete
        && item.Values is { Count: 1 }
        && item.Values.TryGetValue("Id", out var id)
        && id.Value is string value
        && Guid.TryParse(value, out var mappingId)
        && mappingId != Guid.Empty;

    private async Task<LocalProof?> FindLocalUndoPairsAsync(
        SyncChangeSet changes, Guid otherStoreId, string owner,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT MIN(ID) FROM __CORE_SYNC_CT),
                (SELECT MAX(ID) FROM __CORE_SYNC_CT),
                (SELECT LOCAL_VERSION FROM __CORE_SYNC_REMOTE_ANCHOR WHERE ID = $store),
                (SELECT COUNT(*) FROM LearningResource),
                (SELECT COUNT(*) FROM LearningResource
                    WHERE UserProfileId IS NULL OR UserProfileId <> $owner),
                (SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN (
                    '__LearningResource_ct-UPDATE__', '__LearningResource_ct-DELETE__',
                    '__ResourceVocabularyMapping_ct-INSERT__',
                    '__ResourceVocabularyMapping_ct-DELETE__')),
                (SELECT COUNT(*) FROM pragma_foreign_key_list('ResourceVocabularyMapping')
                    WHERE "table" = 'LearningResource'
                        AND "from" = 'ResourceId' AND "to" = 'Id')
            """;
        command.Parameters.AddWithValue("$store", otherStoreId.ToString());
        command.Parameters.AddWithValue("$owner", owner);
        long localAnchor;
        await using (var summary = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await summary.ReadAsync(cancellationToken)
                || summary.IsDBNull(0) || summary.GetInt64(0) != 1
                || summary.IsDBNull(1)
                || summary.GetInt64(1) != changes.SourceAnchor.Version
                || summary.IsDBNull(2)
                || summary.GetInt64(2) < 0
                || summary.GetInt64(2) >= changes.SourceAnchor.Version
                || summary.GetInt64(3) == 0
                || summary.GetInt64(4) != 0
                || summary.GetInt64(5) != 4
                || summary.GetInt64(6) != 1)
                return null;
            localAnchor = summary.GetInt64(2);
        }

        // A deleted mapping no longer records its ResourceId. Only a store
        // whose surviving resources all belong to this owner and whose parent
        // rows have not changed since insertion can prove its former parent.
        command.CommandText = """
            SELECT ID, OP, SRC FROM __CORE_SYNC_CT
            WHERE TBL = $table AND PK_TEXT = $id ORDER BY ID
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$table", MappingTable);
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        using var rowCommand = connection.CreateCommand();
        rowCommand.Transaction = transaction;
        rowCommand.CommandText = """
            SELECT EXISTS(SELECT 1 FROM ResourceVocabularyMapping WHERE Id = $id)
            """;
        var rowIdParameter = rowCommand.Parameters.Add("$id", SqliteType.Text);
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in changes.Items.Where(IsMappingDelete))
        {
            var id = (string)item.Values["Id"].Value!;
            idParameter.Value = id;
            var history = new List<(long Version, string Operation, bool Local)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    history.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2)));
            }
            if (history.Count != 2)
                continue;
            var insert = history[0];
            var deletion = history[1];
            if (insert.Operation != "I" || deletion.Operation != "D"
                || !insert.Local || !deletion.Local
                || insert.Version <= localAnchor || insert.Version >= deletion.Version
                || deletion.Version > changes.SourceAnchor.Version)
                continue;

            rowIdParameter.Value = id;
            if (Convert.ToInt64(
                    await rowCommand.ExecuteScalarAsync(cancellationToken)) == 0)
                versions[id] = insert.Version;
        }

        if (versions.Count == 0)
            return null;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM __CORE_SYNC_CT
                WHERE TBL = $table AND OP IN ('U', 'D')
                    AND ID >= $first AND ID <= $last)
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$table", ResourceTable);
        command.Parameters.AddWithValue("$first", versions.Values.Min());
        command.Parameters.AddWithValue("$last", changes.SourceAnchor.Version);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
            return null;

        versions = versions.Where(pair => pair.Value > localAnchor)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        transaction.Commit();
        return new LocalProof(localAnchor, changes.SourceAnchor.Version, versions);
    }

    private async Task<bool> StillAtVersionAsync(
        LocalProof proof, Guid otherStoreId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT MIN(ID) FROM __CORE_SYNC_CT),
                   (SELECT MAX(ID) FROM __CORE_SYNC_CT),
                   (SELECT LOCAL_VERSION FROM __CORE_SYNC_REMOTE_ANCHOR WHERE ID = $store)
            """;
        command.Parameters.AddWithValue("$store", otherStoreId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            && !reader.IsDBNull(0) && reader.GetInt64(0) == 1
            && !reader.IsDBNull(1) && reader.GetInt64(1) == proof.SourceVersion
            && !reader.IsDBNull(2) && reader.GetInt64(2) == proof.LocalAnchor;
    }

    private async Task<SqliteConnection> OpenReadConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder(local.Configuration.ConnectionString)
        {
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed record LocalProof(
        long LocalAnchor, long SourceVersion, Dictionary<string, long> InsertVersions);
}
