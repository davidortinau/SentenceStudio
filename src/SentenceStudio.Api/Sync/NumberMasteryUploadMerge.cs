using CoreSync;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Sync;

/// <summary>
/// Number Drill keeps one mastery row per learner and bucket, with the same ID on every device
/// and the web (<see cref="SentenceStudio.Services.Numbers.NumberSyncIdentity"/>). When an upload
/// conflicts with the stored copy, the copy reviewed most recently wins (Captain 2026-11-02,
/// docs/sync-all-tables-plan.md D5; <see cref="LatestCopyUploadMerge"/>). Attempts never
/// conflict: each has its own ID. Only the authenticated owner's rows are read or written.
/// </summary>
internal static class NumberMasteryUploadMerge
{
    public const string Table = "NumberMasteryProgress";
    private const string OwnerColumn = "UserProfileId";

    public static LatestCopyUploadMerge.Plan Empty { get; } =
        LatestCopyUploadMerge.Plan.Empty(Table, OwnerColumn);

    public static async Task<LatestCopyUploadMerge.Plan> PrepareAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner, CancellationToken ct)
    {
        var uploaded = LatestCopyUploadMerge.UploadedById(items, Table);
        if (uploaded.Count == 0)
            return Empty;

        var ids = uploaded.Keys.ToArray();
        var stored = await db.NumberMasteryProgresses.AsNoTracking()
            .Where(row => ids.Contains(row.Id) && row.UserProfileId == owner)
            .Select(row => new { row.Id, row.LastReviewed })
            .ToListAsync(ct);
        return LatestCopyUploadMerge.Compare(Table, OwnerColumn, "LastReviewed", uploaded,
            stored.Select(row => (row.Id, (DateTime?)row.LastReviewed)));
    }
}
