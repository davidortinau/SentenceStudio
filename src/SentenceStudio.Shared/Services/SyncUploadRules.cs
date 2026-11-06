using CoreSync;
using Microsoft.EntityFrameworkCore;
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
    /// this device and built-in scenarios (Captain 2026-11-03).</item>
    /// </list>
    /// Deletes leave the device (Captain 2026-11-04, decision 2): the server applies a delete of
    /// the learner's own row and sends it to their other devices, and leaves out any other delete
    /// without refusing the upload. A delete carries only the row's ID, so only a receipt's delete
    /// is recognised as device-only.
    /// </summary>
    public static bool IsDeviceOnlyRow(SyncItem item)
    {
        string? Text(string column) =>
            item.Values.TryGetValue(column, out var value) ? value?.Value?.ToString() : null;

        if (item.ChangeType == ChangeType.Delete)
            return item.TableName == ActivitySessionTable
                && ActivitySessionTypes.IsDeviceOnly(null, Text("Id"));
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
    /// A word recorded to no one is no one's (the one ownership rule, Captain 2026-11-06), so
    /// the server refuses any change to its stored copy; a device still holds one back when
    /// another learner's resource or progress on this device uses it. That decides nothing about
    /// whose the word is: it only keeps a word another learner may be about to sync from
    /// reaching the server first under this learner's account.
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
            if (table.Key != "VocabularyWord")
                continue;
            var unrecorded = ids.Where(id => stored.TryGetValue(id, out var storedOwner)
                && string.IsNullOrEmpty(storedOwner)).ToArray();
            var heldBack = await WordsAnotherLearnerUsesHereAsync(
                local, unrecorded, owner, cancellationToken);
            foreach (var item in table)
                if (heldBack.Contains(Text(item, "Id")!))
                    foreign.Add(item);
        }
        return foreign;
    }

    private static async Task<HashSet<string>> WordsAnotherLearnerUsesHereAsync(
        ApplicationDbContext local, string[] ids, string owner, CancellationToken cancellationToken)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Length == 0)
            return used;
        used.UnionWith(await (
            from mapping in local.ResourceVocabularyMappings.AsNoTracking()
            join resource in local.LearningResources.AsNoTracking()
                on mapping.ResourceId equals resource.Id
            where ids.Contains(mapping.VocabularyWordId)
                && resource.UserProfileId != null && resource.UserProfileId != ""
                && resource.UserProfileId != owner
            select mapping.VocabularyWordId).ToListAsync(cancellationToken));
        used.UnionWith(await local.VocabularyProgresses.AsNoTracking()
            .Where(progress => ids.Contains(progress.VocabularyWordId)
                && progress.UserId != null && progress.UserId != "" && progress.UserId != owner)
            .Select(progress => progress.VocabularyWordId).ToListAsync(cancellationToken));
        return used;
    }

    private static string? Text(SyncItem item, string column) =>
        item.Values is not null && item.Values.TryGetValue(column, out var value)
            ? value?.Value?.ToString() : null;
}
