using CoreSync;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.Services;

/// <summary>
/// The local CoreSync store of a client with one database for every account (the MAUI heads),
/// prepared for owner-checked sync (Captain 2026-11-04, decision 1). Uploads leave out the rows
/// <see cref="SyncUploadRules"/> keeps on the device (device-only rows, and rows that are another
/// learner's, which an installed MAUI app downloaded before owner checks); the server leaves out
/// the rest it cannot place, and applies deletes only of the learner's own rows. Nothing is
/// deleted or re-owned here. Without a verified owner nothing is uploaded. Downloads and every
/// other call pass through.
/// </summary>
public sealed class OwnerCheckedSyncProvider(
    ISyncProvider local,
    Func<ApplicationDbContext> openLocal,
    ISyncOwnerSource owners,
    ILogger<OwnerCheckedSyncProvider> logger) : ISyncProvider
{
    public string[]? SyncTableNames => local.SyncTableNames;

    public async Task<SyncChangeSet> GetChangesAsync(
        Guid otherStoreId, SyncFilterParameter[]? syncFilterParameters = null,
        SyncDirection syncDirection = SyncDirection.UploadAndDownload,
        string[]? tables = null, CancellationToken cancellationToken = default)
    {
        var changes = await local.GetChangesAsync(
            otherStoreId, syncFilterParameters, syncDirection, tables, cancellationToken);
        if (syncDirection == SyncDirection.DownloadOnly || changes.Items.Count == 0)
            return changes;

        var owner = (await owners.GetAsync(cancellationToken))?.Owner
            ?? throw new UnauthorizedAccessException("Sync requires a signed-in learner.");
        var candidates = changes.Items.Where(item => !SyncUploadRules.IsDeviceOnlyRow(item)).ToArray();
        IReadOnlySet<SyncItem> foreign;
        await using (var db = openLocal())
            foreign = await SyncUploadRules.AnotherLearnersRowsAsync(
                db, candidates, owner, cancellationToken);
        var kept = candidates.Where(item => !foreign.Contains(item)).ToArray();
        if (kept.Length != changes.Items.Count)
            logger.LogInformation(
                "Sync kept {Local} rows on this device ({DeviceOnly} device-only, {Foreign} another learner's).",
                changes.Items.Count - kept.Length, changes.Items.Count - candidates.Length,
                foreign.Count);
        return new SyncChangeSet(changes.SourceAnchor, changes.TargetAnchor, kept);
    }

    public Task<SyncAnchor> ApplyChangesAsync(
        SyncChangeSet changeSet, Func<SyncItem, ConflictResolution>? onConflictFunc = null,
        CancellationToken cancellationToken = default) =>
        local.ApplyChangesAsync(changeSet, onConflictFunc, cancellationToken);

    public Task<Guid> GetStoreIdAsync(CancellationToken cancellationToken = default) =>
        local.GetStoreIdAsync(cancellationToken);

    public Task SaveVersionForStoreAsync(
        Guid otherStoreId, long version, CancellationToken cancellationToken = default) =>
        local.SaveVersionForStoreAsync(otherStoreId, version, cancellationToken);

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
}
