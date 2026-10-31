using System.Globalization;
using System.Reflection;
using CoreSync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Two devices of one owner can each create the owner's first VocabularyProgress row for
/// a word before either syncs. Each gets a fresh Id, but (VocabularyWordId, UserId) is
/// unique on both stores, so CoreSync's INSERT OR IGNORE / ON CONFLICT DO NOTHING drops
/// the other device's row and its learning contexts then fail their foreign key. The
/// anchors still advance, so both sides lose those rows permanently.
///
/// Before a download is applied, this adopts the server's Id for the owner's colliding
/// local row (re-pointing its contexts), lets the server row and contexts land, then
/// folds both rows' history into the adopted row with the duplicate-word merge rule.
/// Nothing is deleted; the merged row and re-pointed contexts upload as ordinary updates.
/// </summary>
internal sealed class NativeProgressIdentityReconciler(
    string connectionString, ILogger logger)
{
    internal const string ProgressTable = "VocabularyProgress";

    internal sealed record Adoption(VocabularyProgress Local, VocabularyProgress Remote);

    public async Task<IReadOnlyList<Adoption>> AdoptRemoteIdsAsync(
        SyncChangeSet changeSet, string owner, CancellationToken cancellationToken)
    {
        var incoming = new Dictionary<string, VocabularyProgress>(StringComparer.Ordinal);
        foreach (var item in changeSet.Items)
        {
            if (item.TableName != ProgressTable
                || item.ChangeType == ChangeType.Delete
                || item.Values is null
                || Text(item, "UserId") != owner
                || !IsGuid(Text(item, "Id"))
                || !IsGuid(Text(item, "VocabularyWordId")))
                continue;
            if (TryMaterialize(item) is { } remote)
                incoming[remote.VocabularyWordId] = remote;
        }
        if (incoming.Count == 0)
            return [];

        await using var db = OpenContext();
        var wordIds = incoming.Keys.ToArray();
        var remoteIds = incoming.Values.Select(row => row.Id).ToArray();
        var locals = await db.VocabularyProgresses.AsNoTracking()
            .Where(row => row.UserId == owner && wordIds.Contains(row.VocabularyWordId))
            .ToListAsync(cancellationToken);
        var present = (await db.VocabularyProgresses.AsNoTracking()
            .Where(row => remoteIds.Contains(row.Id))
            .Select(row => row.Id).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var adoptions = locals
            .Where(local => incoming[local.VocabularyWordId].Id != local.Id
                && !present.Contains(incoming[local.VocabularyWordId].Id))
            .Select(local => new Adoption(local, incoming[local.VocabularyWordId]))
            .ToList();
        if (adoptions.Count == 0)
            return [];

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The child rows are re-pointed after their parent key changes; check at commit.
        await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON", cancellationToken);
        foreach (var adoption in adoptions)
        {
            var localId = adoption.Local.Id;
            var remoteId = adoption.Remote.Id;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE VocabularyProgress SET Id = {remoteId} WHERE Id = {localId} AND UserId = {owner}",
                cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE VocabularyLearningContext SET VocabularyProgressId = {remoteId} WHERE VocabularyProgressId = {localId}",
                cancellationToken);
            logger.LogWarning(
                "Native sync adopting server VocabularyProgress {RemoteId} for local {LocalId} (word {WordId}); both devices created progress before syncing.",
                remoteId, localId, adoption.Local.VocabularyWordId);
        }
        await transaction.CommitAsync(cancellationToken);
        return adoptions;
    }

    public async Task FoldAsync(
        IReadOnlyList<Adoption> adoptions, string owner, CancellationToken cancellationToken)
    {
        await using var db = OpenContext();
        foreach (var adoption in adoptions)
        {
            var keeper = await db.VocabularyProgresses.SingleOrDefaultAsync(
                row => row.Id == adoption.Remote.Id && row.UserId == owner, cancellationToken);
            if (keeper is null)
            {
                logger.LogWarning(
                    "Native sync could not fold adopted VocabularyProgress {RemoteId}: row missing after apply.",
                    adoption.Remote.Id);
                continue;
            }
            var contexts = await db.VocabularyLearningContexts.AsNoTracking()
                .Where(context => context.VocabularyProgressId == keeper.Id)
                .ToListAsync(cancellationToken);
            VocabularyProgressFold.FoldInto(keeper, [adoption.Local, adoption.Remote], contexts);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private ApplicationDbContext OpenContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder(connectionString)
            {
                Pooling = false
            }.ToString()).Options);

    private static readonly PropertyInfo[] ProgressColumns = typeof(VocabularyProgress)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.CanWrite)
        .ToArray();

    private VocabularyProgress? TryMaterialize(SyncItem item)
    {
        var row = new VocabularyProgress();
        try
        {
            foreach (var property in ProgressColumns)
            {
                if (!item.Values.TryGetValue(property.Name, out var value)
                    || value?.Value is null or DBNull)
                    continue;
                property.SetValue(row, Convert(value.Value, property.PropertyType));
            }
            return row;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException
            or OverflowException or ArgumentException)
        {
            logger.LogWarning(ex,
                "Native sync could not read downloaded VocabularyProgress {Id}; applying it unchanged.",
                Text(item, "Id"));
            return null;
        }
    }

    private static object Convert(object raw, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsInstanceOfType(raw))
            return raw;
        if (target.IsEnum)
            return Enum.ToObject(target, System.Convert.ToInt64(raw, CultureInfo.InvariantCulture));
        if (target == typeof(DateTime))
            return raw is DateTimeOffset offset ? offset.UtcDateTime
                : DateTime.Parse(raw.ToString()!, CultureInfo.InvariantCulture);
        return System.Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
    }

    private static string? Text(SyncItem item, string column) =>
        item.Values.TryGetValue(column, out var value) ? value?.Value?.ToString() : null;

    private static bool IsGuid(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty;
}
