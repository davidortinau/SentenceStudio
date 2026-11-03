using System.Globalization;
using CoreSync;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Sync;

/// <summary>
/// Number Drill keeps one mastery row per learner and bucket, with the same ID on every device
/// and the web (<see cref="SentenceStudio.Services.Numbers.NumberSyncIdentity"/>). When an upload
/// conflicts with the stored copy, the copy reviewed most recently wins (Captain 2026-11-02,
/// docs/sync-all-tables-plan.md D5). A stored winner is touched after the upload so the
/// uploading device downloads it in the same sync. Attempts never conflict: each has its own ID.
/// Only the authenticated owner's rows are read or written.
/// </summary>
internal static class NumberMasteryUploadMerge
{
    public const string Table = "NumberMasteryProgress";

    public sealed record Plan(IReadOnlySet<string> UploadWins, IReadOnlySet<string> StoredWins)
    {
        public static Plan Empty { get; } = new(new HashSet<string>(), new HashSet<string>());
        public bool IsEmpty => UploadWins.Count == 0 && StoredWins.Count == 0;
    }

    public static async Task<Plan> PrepareAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner, CancellationToken ct)
    {
        var uploaded = items
            .Where(item => item.TableName == Table && item.ChangeType != ChangeType.Delete
                && item.Values is not null && Text(item, "Id") is { Length: > 0 })
            .ToDictionary(item => Text(item, "Id")!, StringComparer.Ordinal);
        if (uploaded.Count == 0)
            return Plan.Empty;

        var ids = uploaded.Keys.ToArray();
        var stored = await db.NumberMasteryProgresses.AsNoTracking()
            .Where(row => ids.Contains(row.Id) && row.UserProfileId == owner)
            .Select(row => new { row.Id, row.LastReviewed })
            .ToListAsync(ct);

        var uploadWins = new HashSet<string>(StringComparer.Ordinal);
        var storedWins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in stored)
        {
            if (UploadIsNewer(LastReviewed(uploaded[row.Id]), row.LastReviewed))
                uploadWins.Add(row.Id);
            else
                storedWins.Add(row.Id);
        }
        return new Plan(uploadWins, storedWins);
    }

    /// <summary>
    /// Drops uploaded copies that lose to the stored row. CoreSync applies an update without
    /// asking when it sees no conflict, so a losing copy must not reach it at all.
    /// </summary>
    public static IReadOnlyList<SyncItem> WithoutLosers(IReadOnlyList<SyncItem> items, Plan plan) =>
        plan.StoredWins.Count == 0 ? items : items.Where(item => !(item.TableName == Table
            && item.ChangeType != ChangeType.Delete
            && Text(item, "Id") is { } id && plan.StoredWins.Contains(id))).ToArray();

    /// <summary>A newer uploaded copy overwrites the stored row even when CoreSync sees a conflict.</summary>
    public static ConflictResolution? Resolve(Plan plan, SyncItem item) =>
        item.TableName == Table && item.ChangeType != ChangeType.Delete
            && Text(item, "Id") is { } id && plan.UploadWins.Contains(id)
            ? ConflictResolution.ForceWrite
            : null;

    /// <summary>Records the kept stored rows as server changes so the uploader downloads them.</summary>
    public static async Task TouchStoredWinnersAsync(
        ApplicationDbContext db, Plan plan, string owner, CancellationToken ct)
    {
        foreach (var id in plan.StoredWins)
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"NumberMasteryProgress\" SET \"Id\" = \"Id\" WHERE \"Id\" = {0} AND \"UserProfileId\" = {1}",
                [id, owner], ct);
    }

    internal static bool UploadIsNewer(DateTime? uploaded, DateTime? stored) =>
        uploaded is { } upload && (stored is not { } kept || Utc(upload) > Utc(kept));

    private static DateTime? LastReviewed(SyncItem item) =>
        item.Values.TryGetValue("LastReviewed", out var value) && value?.Value is { } raw
            ? raw switch
            {
                DateTime date => date,
                DateTimeOffset offset => offset.UtcDateTime,
                string text when text.Length > 0 => DateTime.Parse(
                    text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => null
            }
            : null;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value
    };

    private static string? Text(SyncItem item, string column) =>
        item.Values.TryGetValue(column, out var value) ? value?.Value?.ToString() : null;
}
