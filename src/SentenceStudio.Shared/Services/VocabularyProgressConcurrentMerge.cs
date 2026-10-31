using System.Globalization;
using System.Reflection;
using CoreSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Two copies of ONE VocabularyProgress row (same Id) diverge when two devices, or a device
/// and the web app, each record attempts before syncing. CoreSync's last-writer-wins update
/// then drops the other side's counted attempts even though their learning contexts (one per
/// attempt or exposure) all survive.
///
/// The merge is event-based: the stored row already counts every stored context, so the merged
/// row is the stored row plus only the uploaded contexts the store does not hold yet, applied with
/// the live scoring path. A context is identified by its Id, so merging the same upload twice
/// (a retried request, a re-sent change set) adds nothing. An upload whose counters already
/// include the stored row plus its new contexts was made on top of the current stored row and is
/// kept unchanged. Nothing is deleted; other owners' rows are never read or written.
/// </summary>
public static class VocabularyProgressConcurrentMerge
{
    public const string ProgressTable = "VocabularyProgress";
    public const string ContextTable = "VocabularyLearningContext";

    /// <summary>The columns a merge rewrites; identity and owner columns never change.</summary>
    internal static readonly string[] MergedColumns =
    [
        nameof(VocabularyProgress.MasteryScore), nameof(VocabularyProgress.TotalAttempts),
        nameof(VocabularyProgress.CorrectAttempts), nameof(VocabularyProgress.CurrentStreak),
        nameof(VocabularyProgress.ProductionInStreak),
        nameof(VocabularyProgress.QuizRecognitionDemonstrations),
        nameof(VocabularyProgress.QuizProductionDemonstrations),
        nameof(VocabularyProgress.RecognitionAttempts), nameof(VocabularyProgress.RecognitionCorrect),
        nameof(VocabularyProgress.ProductionAttempts), nameof(VocabularyProgress.ProductionCorrect),
        nameof(VocabularyProgress.ApplicationAttempts), nameof(VocabularyProgress.ApplicationCorrect),
        nameof(VocabularyProgress.CurrentPhase), nameof(VocabularyProgress.NextReviewDate),
        nameof(VocabularyProgress.ReviewInterval), nameof(VocabularyProgress.EaseFactor),
        nameof(VocabularyProgress.MultipleChoiceCorrect), nameof(VocabularyProgress.TextEntryCorrect),
        nameof(VocabularyProgress.IsPromoted), nameof(VocabularyProgress.IsCompleted),
        nameof(VocabularyProgress.IsUserDeclared), nameof(VocabularyProgress.UserDeclaredAt),
        nameof(VocabularyProgress.VerificationState), nameof(VocabularyProgress.FirstSeenAt),
        nameof(VocabularyProgress.LastPracticedAt), nameof(VocabularyProgress.MasteredAt),
        nameof(VocabularyProgress.LastExposedAt), nameof(VocabularyProgress.ExposureCount),
        nameof(VocabularyProgress.CreatedAt), nameof(VocabularyProgress.UpdatedAt),
    ];

    /// <summary>
    /// Returns false (and <paramref name="merged"/> = <paramref name="incoming"/>) when the
    /// incoming row already counts the stored row and its new contexts. All dates are expected in
    /// one convention (see <see cref="Utc"/>).
    /// </summary>
    public static bool TryMerge(
        VocabularyProgress stored, VocabularyProgress incoming,
        IReadOnlyCollection<VocabularyLearningContext> storedContexts,
        IReadOnlyCollection<VocabularyLearningContext> uploadedContexts,
        DateTime utcNow, out VocabularyProgress merged)
    {
        var known = storedContexts.Select(context => context.Id).ToHashSet(StringComparer.Ordinal);
        var fresh = uploadedContexts.Where(context => known.Add(context.Id))
            .OrderBy(context => context.LearnedAt).ThenBy(context => context.CreatedAt)
            .ToList();

        var candidate = Copy(stored);
        foreach (var context in fresh)
            Apply(candidate, context);

        if (incoming.TotalAttempts >= candidate.TotalAttempts
            && incoming.CorrectAttempts >= candidate.CorrectAttempts
            && incoming.ExposureCount >= candidate.ExposureCount)
        {
            // The counts already include the stored row, but flags set elsewhere (for
            // example a declaration or completion made on the web) must not be erased.
            var flagged = Copy(incoming);
            MergeFlags(flagged, stored, incoming);
            if (SameFlags(flagged, incoming))
            {
                merged = incoming;
                return false;
            }
            flagged.UpdatedAt = Max(Max(stored.UpdatedAt, incoming.UpdatedAt), utcNow);
            merged = flagged;
            return true;
        }

        // With every counted attempt backed by a context, a chronological replay gives the
        // mastery and schedule "as if one device had made every attempt". Legacy rows that
        // predate contexts keep the incremental result instead of being reset toward zero.
        var all = storedContexts.Concat(fresh).ToList();
        var passive = all.Count(VocabularyMasteryCalculator.IsPassive);
        if (candidate.TotalAttempts == all.Count - passive && candidate.ExposureCount == passive)
        {
            var replay = Copy(candidate);
            VocabularyMasteryCalculator.RecalculateInto(replay, all, out _);
            candidate.MasteryScore = replay.MasteryScore;
            candidate.CurrentStreak = replay.CurrentStreak;
            candidate.ProductionInStreak = replay.ProductionInStreak;
            candidate.ReviewInterval = replay.ReviewInterval;
            candidate.EaseFactor = replay.EaseFactor;
            candidate.NextReviewDate = replay.NextReviewDate;
            candidate.MasteredAt = replay.MasteredAt;
            candidate.LastExposedAt = replay.LastExposedAt;
        }

        // Never lose a count the incoming row has and the stored side does not explain.
        candidate.TotalAttempts = Math.Max(candidate.TotalAttempts, incoming.TotalAttempts);
        candidate.CorrectAttempts = Math.Min(candidate.TotalAttempts,
            Math.Max(candidate.CorrectAttempts, incoming.CorrectAttempts));
        candidate.ExposureCount = Math.Max(candidate.ExposureCount, incoming.ExposureCount);
        candidate.QuizRecognitionDemonstrations = Math.Max(
            candidate.QuizRecognitionDemonstrations, incoming.QuizRecognitionDemonstrations);
        candidate.QuizProductionDemonstrations = Math.Max(
            candidate.QuizProductionDemonstrations, incoming.QuizProductionDemonstrations);
#pragma warning disable CS0618
        candidate.RecognitionAttempts = Math.Max(candidate.RecognitionAttempts, incoming.RecognitionAttempts);
        candidate.RecognitionCorrect = Math.Max(candidate.RecognitionCorrect, incoming.RecognitionCorrect);
        candidate.ProductionAttempts = Math.Max(candidate.ProductionAttempts, incoming.ProductionAttempts);
        candidate.ProductionCorrect = Math.Max(candidate.ProductionCorrect, incoming.ProductionCorrect);
        candidate.ApplicationAttempts = Math.Max(candidate.ApplicationAttempts, incoming.ApplicationAttempts);
        candidate.ApplicationCorrect = Math.Max(candidate.ApplicationCorrect, incoming.ApplicationCorrect);
        candidate.MultipleChoiceCorrect = Math.Max(candidate.MultipleChoiceCorrect, incoming.MultipleChoiceCorrect);
        candidate.TextEntryCorrect = Math.Max(candidate.TextEntryCorrect, incoming.TextEntryCorrect);
        candidate.CurrentPhase = (LearningPhase)Math.Max(
            (int)candidate.CurrentPhase, (int)incoming.CurrentPhase);
        candidate.IsPromoted = candidate.MasteryScore >= 0.50f
            || stored.IsPromoted || incoming.IsPromoted;
        candidate.IsCompleted = candidate.IsKnown || stored.IsCompleted || incoming.IsCompleted;
#pragma warning restore CS0618

        candidate.FirstSeenAt = Min(stored.FirstSeenAt, incoming.FirstSeenAt);
        candidate.CreatedAt = Min(stored.CreatedAt, incoming.CreatedAt);
        candidate.LastPracticedAt = Max(Max(stored.LastPracticedAt, incoming.LastPracticedAt),
            candidate.LastPracticedAt);
        candidate.LastExposedAt = MaxOf(candidate.LastExposedAt, stored.LastExposedAt, incoming.LastExposedAt);
        candidate.MasteredAt = MinOf(candidate.MasteredAt, stored.MasteredAt, incoming.MasteredAt);

        MergeDeclaration(candidate, stored, incoming);
        candidate.UpdatedAt = Max(Max(stored.UpdatedAt, incoming.UpdatedAt), utcNow);
        merged = candidate;
        return true;
    }

    /// <summary>The most recent explicit declaration wins; an undeclared side never erases one.</summary>
    private static void MergeDeclaration(
        VocabularyProgress target, VocabularyProgress stored, VocabularyProgress incoming)
    {
        var declared = new[] { stored, incoming }
            .Where(row => row.IsUserDeclared)
            .OrderByDescending(row => row.UserDeclaredAt ?? DateTime.MinValue)
            .FirstOrDefault();
        if (declared is null)
            return;
        target.IsUserDeclared = true;
        target.UserDeclaredAt = declared.UserDeclaredAt;
        target.VerificationState = declared.VerificationState;
    }

    /// <summary>Flags either side may have set independently of attempt counts.</summary>
    private static void MergeFlags(
        VocabularyProgress target, VocabularyProgress stored, VocabularyProgress incoming)
    {
        MergeDeclaration(target, stored, incoming);
        target.IsCompleted = stored.IsCompleted || incoming.IsCompleted;
        target.IsPromoted = stored.IsPromoted || incoming.IsPromoted;
        target.MasteredAt = MinOf(stored.MasteredAt, incoming.MasteredAt);
    }

    private static bool SameFlags(VocabularyProgress left, VocabularyProgress right) =>
        left.IsUserDeclared == right.IsUserDeclared
        && left.UserDeclaredAt == right.UserDeclaredAt
        && left.VerificationState == right.VerificationState
        && left.IsCompleted == right.IsCompleted
        && left.IsPromoted == right.IsPromoted
        && left.MasteredAt == right.MasteredAt;

    private static void Apply(VocabularyProgress progress, VocabularyLearningContext context)
    {
        if (VocabularyMasteryCalculator.IsPassive(context))
        {
            VocabularyProgressService.ApplyPassiveExposure(progress, context.LearnedAt);
            return;
        }
        VocabularyProgressService.ApplyAttemptScoring(progress,
            VocabularyMasteryCalculator.ToAttempt(context, progress.VocabularyWordId, progress.UserId),
            context.LearnedAt);
    }

    /// <summary>
    /// Prepares an authenticated owner's uploaded change set before it is applied. For every
    /// uploaded VocabularyProgress Insert/Update of a row the store already holds for that owner,
    /// rewrites the item's values to the merge when the upload does not already include the
    /// stored row. The rewritten items must be force-written by the caller (inside the same apply
    /// transaction as their contexts) and then touched with <see cref="TouchMergedAsync"/>.
    /// Returns the rewritten progress Ids.
    /// </summary>
    /// <remarks>
    /// Dates are compared and written as UTC instants. Native SQLite clients send zone-less
    /// wall-clock values that CoreSync already stores as UTC instants (the PostgreSQL session
    /// zone), so a zone-less value is treated as UTC and an EF value is converted to UTC.
    /// </remarks>
    public static async Task<IReadOnlySet<string>> PrepareUploadAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner,
        ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return Empty;
        var uploads = items.Where(item => item.TableName == ProgressTable
                && item.ChangeType is ChangeType.Insert or ChangeType.Update
                && item.Values is not null
                && Text(item, "UserId") == owner
                && IsGuid(Text(item, "Id")))
            .GroupBy(item => Text(item, "Id")!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        if (uploads.Count == 0)
            return Empty;

        var ids = uploads.Keys.ToArray();
        var storedRows = await db.VocabularyProgresses.AsNoTracking()
            .Where(row => ids.Contains(row.Id) && row.UserId == owner)
            .ToListAsync(cancellationToken);
        if (storedRows.Count == 0)
            return Empty;
        var storedIds = storedRows.Select(row => row.Id).ToArray();
        var storedContexts = (await db.VocabularyLearningContexts.AsNoTracking()
                .Where(context => storedIds.Contains(context.VocabularyProgressId))
                .ToListAsync(cancellationToken))
            .Select(Normalize)
            .ToLookup(context => context.VocabularyProgressId, StringComparer.Ordinal);

        var uploadedContexts = new Dictionary<string, List<VocabularyLearningContext>>(
            StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.TableName != ContextTable
                || item.ChangeType is not (ChangeType.Insert or ChangeType.Update)
                || item.Values is null
                || Text(item, "VocabularyProgressId") is not { } parent
                || !storedIds.Contains(parent, StringComparer.Ordinal))
                continue;
            if (Overlay(new VocabularyLearningContext(), item, logger) is not { } context
                || string.IsNullOrWhiteSpace(context.Id))
                continue;
            if (!uploadedContexts.TryGetValue(parent, out var list))
                uploadedContexts[parent] = list = [];
            list.Add(Normalize(context));
        }

        var merged = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        foreach (var storedRow in storedRows)
        {
            var stored = Normalize(storedRow);
            var item = uploads[stored.Id];
            // Missing upload columns keep the stored values, so a partial item compares fairly.
            if (Overlay(Copy(stored), item, logger) is not { } incomingRaw)
                continue;
            var incoming = Normalize(incomingRaw);
            if (incoming.UserId != owner || incoming.VocabularyWordId != stored.VocabularyWordId)
                continue;
            if (!TryMerge(stored, incoming, storedContexts[stored.Id].ToList(),
                    uploadedContexts.TryGetValue(stored.Id, out var uploaded) ? uploaded : [],
                    now, out var result))
                continue;

            foreach (var column in MergedColumns)
            {
                var value = Columns.ByName[column].GetValue(result);
                value = value switch
                {
                    Enum enumValue => Convert.ToInt32(enumValue, CultureInfo.InvariantCulture),
                    _ => value
                };
                if (item.Values.TryGetValue(column, out var existing))
                    existing.Value = value;
                else
                    item.Values[column] = new SyncItemValue(value);
            }
            merged.Add(stored.Id);
            logger.LogWarning(
                "Native sync merged concurrent VocabularyProgress {ProgressId}: stored {StoredTotal} and uploaded {UploadedTotal} attempts became {MergedTotal}.",
                stored.Id, stored.TotalAttempts, incoming.TotalAttempts, result.TotalAttempts);
        }
        return merged;
    }

    /// <summary>
    /// The apply attributes the merged write to the uploading store, so that store would never
    /// download it. Re-touch every row whose stored counters now equal the merge (a server-origin
    /// change) so the uploading device receives the merged row in the same sync. Missing this
    /// step loses nothing: the next upload of the row is merged against the stored row again.
    /// </summary>
    public static async Task TouchMergedAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, IReadOnlySet<string> mergedIds,
        string owner, CancellationToken cancellationToken)
    {
        if (mergedIds.Count == 0 || string.IsNullOrWhiteSpace(owner))
            return;
        var expected = items.Where(item => item.TableName == ProgressTable
                && Text(item, "Id") is { } id && mergedIds.Contains(id))
            .ToDictionary(item => Text(item, "Id")!,
                item => Convert.ToInt32(item.Values[nameof(VocabularyProgress.TotalAttempts)].Value,
                    CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var ids = expected.Keys.ToArray();
        var written = (await db.VocabularyProgresses.AsNoTracking()
                .Where(row => ids.Contains(row.Id) && row.UserId == owner)
                .Select(row => new { row.Id, row.TotalAttempts })
                .ToListAsync(cancellationToken))
            .Where(row => expected[row.Id] == row.TotalAttempts)
            .Select(row => row.Id).ToArray();
        if (written.Length == 0)
            return;
        await db.VocabularyProgresses
            .Where(row => written.Contains(row.Id) && row.UserId == owner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                row => row.UpdatedAt, row => row.UpdatedAt), cancellationToken);
    }

    /// <summary>Normalizes a date to a UTC instant; a zone-less value is read as UTC.</summary>
    public static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value
    };

    private static DateTime? Utc(DateTime? value) => value is { } date ? Utc(date) : null;

    private static VocabularyProgress Normalize(VocabularyProgress row)
    {
        var copy = Copy(row);
        copy.FirstSeenAt = Utc(copy.FirstSeenAt);
        copy.LastPracticedAt = Utc(copy.LastPracticedAt);
        copy.CreatedAt = Utc(copy.CreatedAt);
        copy.UpdatedAt = Utc(copy.UpdatedAt);
        copy.NextReviewDate = Utc(copy.NextReviewDate);
        copy.MasteredAt = Utc(copy.MasteredAt);
        copy.LastExposedAt = Utc(copy.LastExposedAt);
        copy.UserDeclaredAt = Utc(copy.UserDeclaredAt);
        return copy;
    }

    private static VocabularyLearningContext Normalize(VocabularyLearningContext context)
    {
        context.LearnedAt = Utc(context.LearnedAt);
        context.CreatedAt = Utc(context.CreatedAt);
        context.UpdatedAt = Utc(context.UpdatedAt);
        return context;
    }

    internal static VocabularyProgress Copy(VocabularyProgress source)
    {
        var copy = new VocabularyProgress();
        foreach (var property in Columns.All)
            property.SetValue(copy, property.GetValue(source));
        return copy;
    }

    private static T? Overlay<T>(T row, SyncItem item, ILogger logger) where T : class
    {
        try
        {
            foreach (var property in ColumnsOf<T>.All)
            {
                if (!item.Values.TryGetValue(property.Name, out var value))
                    continue;
                if (value?.Value is null or DBNull)
                {
                    if (!property.PropertyType.IsValueType
                        || Nullable.GetUnderlyingType(property.PropertyType) is not null)
                        property.SetValue(row, null);
                    continue;
                }
                property.SetValue(row, ConvertValue(value.Value, property.PropertyType));
            }
            return row;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException
            or OverflowException or ArgumentException)
        {
            logger.LogWarning(ex,
                "Native sync could not read uploaded {Table} {Id}; it is applied unmerged.",
                item.TableName, Text(item, "Id"));
            return null;
        }
    }

    private static object ConvertValue(object raw, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsInstanceOfType(raw))
            return raw;
        if (target == typeof(string))
            return Convert.ToString(raw, CultureInfo.InvariantCulture)!;
        if (target.IsEnum)
            return raw is string name
                ? Enum.Parse(target, name)
                : Enum.ToObject(target, Convert.ToInt64(raw, CultureInfo.InvariantCulture));
        if (target == typeof(DateTime))
            return raw switch
            {
                DateTimeOffset offset => offset.UtcDateTime,
                string text => DateTime.Parse(
                    text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => throw new InvalidCastException($"Cannot convert {raw.GetType()} to DateTime.")
            };
        return Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
    }

    private static string? Text(SyncItem item, string column) =>
        item.Values.TryGetValue(column, out var value) ? value?.Value?.ToString() : null;

    private static bool IsGuid(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty;

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;
    private static DateTime Max(DateTime left, DateTime right) => left >= right ? left : right;

    private static DateTime? MaxOf(params DateTime?[] values) =>
        values.Where(value => value.HasValue).Max();

    private static DateTime? MinOf(params DateTime?[] values) =>
        values.Where(value => value.HasValue).Min();

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    private static class Columns
    {
        public static readonly PropertyInfo[] All = ColumnsOf<VocabularyProgress>.All;
        public static readonly Dictionary<string, PropertyInfo> ByName =
            All.ToDictionary(property => property.Name, StringComparer.Ordinal);
    }

    private static class ColumnsOf<T>
    {
        // Scalar columns only: navigations (references and collections) are never copied.
        public static readonly PropertyInfo[] All = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite
                && property.GetIndexParameters().Length == 0
                && (property.PropertyType.IsValueType
                    || property.PropertyType == typeof(string)))
            .ToArray();
    }
}
