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
/// Before a download is applied, <see cref="ReconcileAsync"/> does the whole merge in ONE
/// local SQLite transaction: it adopts the server's Id for the owner's colliding local row,
/// re-points the row's contexts, and writes the fold of the local row and the downloaded
/// server row (with both devices' contexts) into it. The caller then applies the download
/// with conflicts on the adopted Ids skipped, so the merged row survives while the server's
/// contexts insert under it. An interruption at any point leaves either the untouched local
/// row or the complete merged row; the merged row uploads as an ordinary update on the next
/// sync, and a re-download never folds it twice. Nothing is deleted.
/// </summary>
internal sealed class NativeProgressIdentityReconciler(
    string connectionString, ILogger logger)
{
    internal const string ProgressTable = "VocabularyProgress";
    internal const string ContextTable = "VocabularyLearningContext";

    private static readonly IReadOnlySet<string> NoIds = new HashSet<string>();

    /// <summary>Returns the adopted server VocabularyProgress Ids (empty when nothing collided).</summary>
    public async Task<IReadOnlySet<string>> ReconcileAsync(
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
            if (TryMaterialize<VocabularyProgress>(item) is { } remote)
                incoming[remote.VocabularyWordId] = remote;
        }
        if (incoming.Count == 0)
            return NoIds;

        await using var db = OpenContext();
        var wordIds = incoming.Keys.ToArray();
        var remoteIds = incoming.Values.Select(row => row.Id).ToArray();
        // Read under the write transaction so a concurrent local attempt cannot land between
        // the snapshot that is folded and the row that is rewritten.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locals = await db.VocabularyProgresses.AsNoTracking()
            .Where(row => row.UserId == owner && wordIds.Contains(row.VocabularyWordId))
            .ToListAsync(cancellationToken);
        var present = (await db.VocabularyProgresses.AsNoTracking()
            .Where(row => remoteIds.Contains(row.Id))
            .Select(row => row.Id).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        // A local row that already carries the server Id was merged earlier: never fold twice.
        var adoptions = locals
            .Where(local => incoming[local.VocabularyWordId].Id != local.Id
                && !present.Contains(incoming[local.VocabularyWordId].Id))
            .Select(local => (Local: local, Remote: incoming[local.VocabularyWordId]))
            .ToList();
        if (adoptions.Count == 0)
            return NoIds;

        var adopted = adoptions.Select(adoption => adoption.Remote.Id)
            .ToHashSet(StringComparer.Ordinal);
        var downloadedContexts =
            new Dictionary<string, List<VocabularyLearningContext>>(StringComparer.Ordinal);
        foreach (var item in changeSet.Items)
        {
            if (item.TableName != ContextTable
                || item.ChangeType == ChangeType.Delete
                || item.Values is null
                || Text(item, "VocabularyProgressId") is not { } parent
                || !adopted.Contains(parent)
                || TryMaterialize<VocabularyLearningContext>(item) is not { } context)
                continue;
            if (!downloadedContexts.TryGetValue(parent, out var list))
                downloadedContexts[parent] = list = [];
            list.Add(context);
        }

        // The child rows are re-pointed after their parent key changes; check at commit.
        await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON", cancellationToken);
        foreach (var (local, remote) in adoptions)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE VocabularyProgress SET Id = {remote.Id} WHERE Id = {local.Id} AND UserId = {owner}",
                cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE VocabularyLearningContext SET VocabularyProgressId = {remote.Id} WHERE VocabularyProgressId = {local.Id}",
                cancellationToken);

            var keeper = await db.VocabularyProgresses.SingleAsync(
                row => row.Id == remote.Id && row.UserId == owner, cancellationToken);
            var contexts = await db.VocabularyLearningContexts.AsNoTracking()
                .Where(context => context.VocabularyProgressId == remote.Id)
                .ToListAsync(cancellationToken);
            var known = contexts.Select(context => context.Id).ToHashSet(StringComparer.Ordinal);
            if (downloadedContexts.TryGetValue(remote.Id, out var downloaded))
                contexts.AddRange(downloaded.Where(context => known.Add(context.Id)));

            VocabularyProgressFold.FoldInto(keeper, [local, remote], contexts);
            FoldPhaseCounters(keeper, local, remote);
            logger.LogWarning(
                "Native sync merged local VocabularyProgress {LocalId} into server {RemoteId} (word {WordId}); both devices created progress before syncing.",
                local.Id, remote.Id, local.VocabularyWordId);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return adopted;
    }

    /// <summary>
    /// The shared fold does not replay per-phase counters. Each source counted only its own
    /// attempts, so the merged row carries their sum and never loses a counted attempt.
    /// </summary>
    private static void FoldPhaseCounters(
        VocabularyProgress keeper, VocabularyProgress local, VocabularyProgress remote)
    {
        keeper.RecognitionAttempts = local.RecognitionAttempts + remote.RecognitionAttempts;
        keeper.RecognitionCorrect = local.RecognitionCorrect + remote.RecognitionCorrect;
        keeper.ProductionAttempts = local.ProductionAttempts + remote.ProductionAttempts;
        keeper.ProductionCorrect = local.ProductionCorrect + remote.ProductionCorrect;
        keeper.ApplicationAttempts = local.ApplicationAttempts + remote.ApplicationAttempts;
        keeper.ApplicationCorrect = local.ApplicationCorrect + remote.ApplicationCorrect;
        keeper.MultipleChoiceCorrect = local.MultipleChoiceCorrect + remote.MultipleChoiceCorrect;
        keeper.TextEntryCorrect = local.TextEntryCorrect + remote.TextEntryCorrect;
        keeper.CurrentPhase = (LearningPhase)Math.Max((int)local.CurrentPhase, (int)remote.CurrentPhase);
        keeper.IsPromoted = local.IsPromoted || remote.IsPromoted;
        keeper.IsCompleted = local.IsCompleted || remote.IsCompleted;
    }

    private ApplicationDbContext OpenContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder(connectionString)
            {
                Pooling = false
            }.ToString()).Options);

    private T? TryMaterialize<T>(SyncItem item) where T : class, new()
    {
        var row = new T();
        try
        {
            foreach (var property in Columns<T>.All)
            {
                if (!item.Values.TryGetValue(property.Name, out var value)
                    || value?.Value is null or DBNull)
                    continue;
                property.SetValue(row, ConvertValue(value.Value, property.PropertyType));
            }
            return row;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException
            or OverflowException or ArgumentException)
        {
            logger.LogWarning(ex,
                "Native sync could not read downloaded {Table} {Id}; applying it unchanged.",
                item.TableName, Text(item, "Id"));
            return null;
        }
    }

    /// <summary>
    /// Converts a downloaded column value to the model type exactly. Typed DateTime values keep
    /// their ticks and Kind; text dates round-trip their Kind ("Z" stays UTC, no designator stays
    /// unspecified) instead of being shifted to local time; offsets resolve to their UTC instant.
    /// </summary>
    internal static object ConvertValue(object raw, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsInstanceOfType(raw))
            return raw;
        if (target.IsEnum)
            return raw is string name
                ? Enum.Parse(target, name)
                : Enum.ToObject(target, System.Convert.ToInt64(raw, CultureInfo.InvariantCulture));
        if (target == typeof(DateTime))
            return raw switch
            {
                DateTimeOffset offset => offset.UtcDateTime,
                string text => DateTime.Parse(
                    text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => throw new InvalidCastException($"Cannot convert {raw.GetType()} to DateTime.")
            };
        return System.Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
    }

    private static string? Text(SyncItem item, string column) =>
        item.Values.TryGetValue(column, out var value) ? value?.Value?.ToString() : null;

    private static bool IsGuid(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty;

    private static class Columns<T>
    {
        public static readonly PropertyInfo[] All = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .ToArray();
    }
}
