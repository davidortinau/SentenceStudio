using CoreSync;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// What a client leaves out of a sync upload, shared by every client (native, MAUI; the
/// Flutter port follows docs/sync-all-tables-plan.md, "Owner-checked sync for every client").
/// A left-out row stays on the device: the upload anchor still moves past it, so it is not
/// offered again until it changes.
/// </summary>
public static class SyncUploadRules
{
    private const string ActivitySessionTable = "ActivitySession";

    /// <summary>
    /// Rows that never leave this device:
    /// <list type="bullet">
    /// <item>quiz answer receipts (phase 2b);</item>
    /// <item>phase 3 rows with no owner (saved before these tables had one, D3), photos stored on
    /// this device and built-in scenarios (Captain 2026-11-03);</item>
    /// <item>deletes, word links included (Captain 2026-11-04, decision 2): the server cannot yet
    /// prove what a delete carrying only an ID removes, so a delete stays on this device and the
    /// server and other clients keep the row.</item>
    /// </list>
    /// </summary>
    public static bool IsDeviceOnlyRow(SyncItem item)
    {
        string? Text(string column) =>
            item.Values.TryGetValue(column, out var value) ? value?.Value?.ToString() : null;

        if (item.ChangeType == ChangeType.Delete)
            return true;
        return item.TableName switch
        {
            ActivitySessionTable => ActivitySessionTypes.IsDeviceOnly(Text("ActivityType"), Text("Id")),
            "SceneImage" => string.IsNullOrEmpty(Text("UserProfileId"))
                || SceneImage.IsDevicePhoto(Text("Url")),
            "ConversationScenario" => string.IsNullOrEmpty(Text("UserProfileId"))
                || SyncIds.IsBuiltInScenario(Text("Id")),
            "ExampleSentence" => string.IsNullOrEmpty(Text("UserProfileId")),
            _ => false
        };
    }

    /// <summary>
    /// The written rows of <paramref name="items"/> that are another learner's: the row names
    /// another learner in its owner column, or the local row with its ID resolves to another
    /// learner (<see cref="SyncRowOwners"/>, the server's rules). The server refuses a whole
    /// upload that contains one, so a client keeps them local. A device database holds such
    /// rows only from before owner checks (an installed MAUI app downloaded every learner's rows)
    /// or from a second account signed in on the same install.
    /// </summary>
    public static async Task<IReadOnlySet<SyncItem>> AnotherLearnersRowsAsync(
        ApplicationDbContext local, IReadOnlyList<SyncItem> items, string owner,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var foreign = new HashSet<SyncItem>(ReferenceEqualityComparer.Instance);
        var writes = items.Where(item => item.ChangeType != ChangeType.Delete
            && Text(item, "Id") is { Length: > 0 }).ToArray();
        foreach (var item in writes)
        {
            if (SyncRowOwners.OwnerColumn(item.TableName) is { } column
                && Text(item, column) is { Length: > 0 } named
                && !string.Equals(named, owner, StringComparison.Ordinal))
                foreign.Add(item);
        }

        foreach (var table in writes.Where(item => !foreign.Contains(item))
            .GroupBy(item => item.TableName, StringComparer.Ordinal))
        {
            var ids = table.Select(item => Text(item, "Id")!)
                .Distinct(StringComparer.Ordinal).ToArray();
            var stored = await SyncRowOwners.GetStoredOwnersAsync(
                local, table.Key, ids, cancellationToken);
            foreach (var item in table)
                if (stored.TryGetValue(Text(item, "Id")!, out var storedOwner)
                    && storedOwner is { Length: > 0 }
                    && !string.Equals(storedOwner, owner, StringComparison.Ordinal))
                    foreign.Add(item);
        }
        return foreign;
    }

    private static string? Text(SyncItem item, string column) =>
        item.Values is not null && item.Values.TryGetValue(column, out var value)
            ? value?.Value?.ToString() : null;
}
