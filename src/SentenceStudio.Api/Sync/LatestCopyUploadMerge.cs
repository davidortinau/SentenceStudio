using System.Globalization;
using CoreSync;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Sync;

/// <summary>
/// For a table whose row has the same ID on every client, an uploaded copy that meets the stored
/// row keeps the copy changed most recently (by a timestamp column), not whichever upload arrives
/// last as CoreSync would. Used by Number Drill mastery (<see cref="NumberMasteryUploadMerge"/>)
/// and quiz runs (<see cref="ActivitySessionUploadMerge"/>). A stored winner is touched after the
/// upload so the uploading device downloads it in the same sync. Only the authenticated owner's
/// rows are compared or written.
/// </summary>
internal static class LatestCopyUploadMerge
{
    /// <summary>The rows of one table where the upload wins and where the stored copy wins.</summary>
    public sealed record Plan(
        string Table, string OwnerColumn,
        IReadOnlySet<string> UploadWins, IReadOnlySet<string> StoredWins)
    {
        public static Plan Empty(string table, string ownerColumn) =>
            new(table, ownerColumn, new HashSet<string>(), new HashSet<string>());

        public bool IsEmpty => UploadWins.Count == 0 && StoredWins.Count == 0;
    }

    /// <summary>The uploaded copies of a table's rows, by ID (the last copy of a repeated row).</summary>
    public static Dictionary<string, SyncItem> UploadedById(IReadOnlyList<SyncItem> items, string table) =>
        items
            .Where(item => item.TableName == table && item.ChangeType != ChangeType.Delete
                && item.Values is not null && Text(item, "Id") is { Length: > 0 })
            .GroupBy(item => Text(item, "Id")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    /// <summary>Compares each stored row with its uploaded copy by <paramref name="column"/>.</summary>
    public static Plan Compare(
        string table, string ownerColumn, string column,
        IReadOnlyDictionary<string, SyncItem> uploaded,
        IEnumerable<(string Id, DateTime? Stored)> stored)
    {
        var uploadWins = new HashSet<string>(StringComparer.Ordinal);
        var storedWins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, kept) in stored)
        {
            if (UploadIsNewer(Timestamp(uploaded[id], column), kept))
                uploadWins.Add(id);
            else
                storedWins.Add(id);
        }
        return new Plan(table, ownerColumn, uploadWins, storedWins);
    }

    /// <summary>
    /// Drops uploaded copies that lose to the stored row. CoreSync applies an update without
    /// asking when it sees no conflict, so a losing copy must not reach it at all.
    /// </summary>
    public static IReadOnlyList<SyncItem> WithoutLosers(IReadOnlyList<SyncItem> items, Plan plan) =>
        plan.StoredWins.Count == 0 ? items : items.Where(item => !(item.TableName == plan.Table
            && item.ChangeType != ChangeType.Delete
            && Text(item, "Id") is { } id && plan.StoredWins.Contains(id))).ToArray();

    /// <summary>A newer uploaded copy overwrites the stored row even when CoreSync sees a conflict.</summary>
    public static ConflictResolution? Resolve(Plan plan, SyncItem item) =>
        item.TableName == plan.Table && item.ChangeType != ChangeType.Delete
            && Text(item, "Id") is { } id && plan.UploadWins.Contains(id)
            ? ConflictResolution.ForceWrite
            : null;

    /// <summary>Records the kept stored rows as server changes so the uploader downloads them.</summary>
    public static async Task TouchStoredWinnersAsync(
        ApplicationDbContext db, Plan plan, string owner, CancellationToken ct)
    {
        if (plan.StoredWins.Count == 0)
            return;
        // One statement while the upload lock is held. Table and column names are constants.
        var ids = plan.StoredWins.ToArray();
        var placeholders = string.Join(", ", ids.Select((_, index) => "{" + (index + 1) + "}"));
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"" + plan.Table + "\" SET \"Id\" = \"Id\" WHERE \"" + plan.OwnerColumn
                + "\" = {0} AND \"Id\" IN (" + placeholders + ")",
            [owner, .. ids], ct);
    }

    internal static bool UploadIsNewer(DateTime? uploaded, DateTime? stored) =>
        uploaded is { } upload && (stored is not { } kept || Utc(upload) > Utc(kept));

    /// <summary>A timestamp column of an uploaded row (a SQLite text value or a DateTime).</summary>
    internal static DateTime? Timestamp(SyncItem item, string column) =>
        item.Values.TryGetValue(column, out var value) && value?.Value is { } raw
            ? raw switch
            {
                DateTime date => date,
                DateTimeOffset offset => offset.UtcDateTime,
                string text when text.Length > 0 => DateTime.Parse(
                    text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => null
            }
            : null;

    internal static string? Text(SyncItem item, string column) =>
        item.Values.TryGetValue(column, out var value) ? value?.Value?.ToString() : null;

    internal static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value
    };
}
