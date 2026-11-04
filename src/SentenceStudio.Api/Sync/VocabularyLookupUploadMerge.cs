using CoreSync;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Sync;

/// <summary>
/// Lookup history (Captain 2026-11-04, decision 4 B) keeps one row per learner, language,
/// direction and text, with the same ID on every client (<see cref="SentenceStudio.Services.SyncIds.VocabularyLookup"/>).
/// Two devices that looked up the same text offline both upload that ID. CoreSync would turn the
/// second insert into an update and skip it as a conflict, or overwrite with whichever upload
/// arrives last; instead the copy looked up most recently wins (<see cref="LatestCopyUploadMerge"/>)
/// and the earliest first-lookup time of the two is kept. The upload is never refused for it.
/// Only the authenticated owner's rows are read or written.
/// </summary>
internal static class VocabularyLookupUploadMerge
{
    public const string Table = "VocabularyLookup";
    private const string OwnerColumn = "UserProfileId";

    /// <summary>Which copy wins per row, and rows whose first-lookup time must move earlier.</summary>
    public sealed record Merge(
        LatestCopyUploadMerge.Plan Copies, IReadOnlyDictionary<string, DateTime> EarliestFirst)
    {
        public bool IsEmpty => Copies.IsEmpty && EarliestFirst.Count == 0;
    }

    public static Merge Empty { get; } = new(
        LatestCopyUploadMerge.Plan.Empty(Table, OwnerColumn), new Dictionary<string, DateTime>());

    public static async Task<Merge> PrepareAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner, CancellationToken ct)
    {
        var uploaded = LatestCopyUploadMerge.UploadedById(items, Table);
        if (uploaded.Count == 0)
            return Empty;

        var ids = uploaded.Keys.ToArray();
        var stored = await db.VocabularyLookups.AsNoTracking()
            .Where(row => ids.Contains(row.Id) && row.UserProfileId == owner)
            .Select(row => new { row.Id, row.FirstLookedUpAt, row.LastLookedUpAt })
            .ToListAsync(ct);
        if (stored.Count == 0)
            return Empty;

        var copies = LatestCopyUploadMerge.Compare(Table, OwnerColumn, "LastLookedUpAt", uploaded,
            stored.Select(row => (row.Id, (DateTime?)row.LastLookedUpAt)));
        var earliest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var row in stored)
        {
            var keptFirst = LatestCopyUploadMerge.Utc(row.FirstLookedUpAt);
            if (LatestCopyUploadMerge.Timestamp(uploaded[row.Id], "FirstLookedUpAt") is not { } raw)
                continue;
            var uploadedFirst = LatestCopyUploadMerge.Utc(raw);
            // The winner's own first time stands unless the other copy's is earlier.
            if (copies.UploadWins.Contains(row.Id) && keptFirst < uploadedFirst)
                earliest[row.Id] = keptFirst;
            else if (copies.StoredWins.Contains(row.Id) && uploadedFirst < keptFirst)
                earliest[row.Id] = uploadedFirst;
        }
        return new Merge(copies, earliest);
    }

    /// <summary>
    /// After CoreSync applied the upload: keeps the earliest first-lookup time and touches stored
    /// winners, both as server changes, so the uploading device downloads the merged row in the
    /// same sync.
    /// </summary>
    public static async Task FinishAsync(
        ApplicationDbContext db, Merge merge, string owner, CancellationToken ct)
    {
        if (merge.EarliestFirst.Count > 0)
        {
            // Written through EF, like the other merges, so the timestamp is stored the way
            // every other server write stores it.
            var ids = merge.EarliestFirst.Keys.ToArray();
            var rows = await db.VocabularyLookups
                .Where(row => ids.Contains(row.Id) && row.UserProfileId == owner)
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                var first = merge.EarliestFirst[row.Id];
                if (first < LatestCopyUploadMerge.Utc(row.FirstLookedUpAt))
                    row.FirstLookedUpAt = first;
            }
            await db.SaveChangesAsync(ct);
        }
        await LatestCopyUploadMerge.TouchStoredWinnersAsync(db, merge.Copies, owner, ct);
    }
}
