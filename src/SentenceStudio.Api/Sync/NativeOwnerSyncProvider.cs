using CoreSync;
using CoreSync.PostgreSQL;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Api.Sync;

/// <summary>
/// Keeps the existing CoreSync protocol and PostgreSQL provider, but limits the
/// native slice's change sets to rows reachable from its authenticated owner.
/// Legacy clients retain their existing table set until their own migration.
/// </summary>
internal sealed class NativeOwnerSyncProvider(
    PostgreSQLSyncProvider provider,
    NativeSyncStoreBindings stores,
    IHttpContextAccessor requests,
    IServiceScopeFactory scopes,
    ILogger<NativeOwnerSyncProvider> logger,
    SyncAccessOptions access) : ISyncProvider
{
    private const long UploadLockKey = 0x53534E4154495645L;

    public string[]? SyncTableNames => provider.SyncTableNames;

    public Task<Guid> GetStoreIdAsync(CancellationToken ct = default) =>
        provider.GetStoreIdAsync(ct);

    public async Task SaveVersionForStoreAsync(
        Guid storeId, long version, CancellationToken ct = default)
    {
        var owner = RequestOwner();
        var scopedStoreId = owner is null
            ? storeId : await stores.BindAsync(storeId, owner, ct);
        await provider.SaveVersionForStoreAsync(scopedStoreId, version, ct);
    }

    public async Task<SyncChangeSet> GetChangesAsync(
        Guid otherStoreId, SyncFilterParameter[]? filters = null,
        SyncDirection direction = SyncDirection.UploadAndDownload,
        string[]? tables = null, CancellationToken ct = default)
    {
        var owner = RequestOwner();
        var scopedStoreId = owner is null
            ? otherStoreId : await stores.BindAsync(otherStoreId, owner, ct);
        var changes = await provider.GetChangesAsync(scopedStoreId, filters, direction, tables, ct);
        if (owner is null)
            return changes;

        var filtered = changes;
        if (changes.Items.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            filtered = await FilterForOwnerAsync(
                changes, owner, scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), ct);
            NormalizeNativeTimestamps(filtered.Items);
        }
        if (filtered.TargetAnchor.StoreId == scopedStoreId)
            filtered.TargetAnchor = new SyncAnchor(otherStoreId, filtered.TargetAnchor.Version);
        logger.LogInformation(
            "Native CoreSync download scoped {VisibleCount} of {TotalCount} changes.",
            filtered.Items.Count, changes.Items.Count);
        return filtered;
    }

    /// <summary>
    /// The request's verified owner. Without the owner header the request is refused unless
    /// <c>Sync:AllowHeaderlessClients</c> is open; the CoreSync endpoint filter refuses it first,
    /// and this repeats the check for any route that might miss that filter.
    /// </summary>
    private string? RequestOwner()
    {
        var owner = GetNativeOwner(requests.HttpContext);
        if (owner is null && !access.AllowHeaderlessClients)
            throw new BadHttpRequestException(
                "Sync requires the owner header.", StatusCodes.Status403Forbidden);
        return owner;
    }

    private static void NormalizeNativeTimestamps(IReadOnlyList<SyncItem> items)
    {
        // Npgsql's legacy timestamp switch materializes timestamptz as Local.
        // CoreSync SQLite stores wall-clock ticks, so convert native downloads
        // back to UTC before either JSON or MessagePack serializes them.
        foreach (var item in items)
        {
            foreach (var value in item.Values.Values)
            {
                if (value.Value is DateTime { Kind: DateTimeKind.Local } local)
                    value.Value = local.ToUniversalTime();
            }
        }
    }

    public async Task<SyncAnchor> ApplyChangesAsync(
        SyncChangeSet changes, Func<SyncItem, ConflictResolution>? onConflict = null,
        CancellationToken ct = default)
    {
        var owner = RequestOwner();
        var scopedStoreId = owner is null
            ? changes.SourceAnchor.StoreId
            : await stores.BindAsync(changes.SourceAnchor.StoreId, owner, ct);

        // CoreSync opens its own connection and transaction. A session lock serializes
        // sync uploads across API replicas while ownership is checked and written.
        var lockConnectionString = new NpgsqlConnectionStringBuilder(provider.Configuration.ConnectionString)
        {
            // Closing a physical connection releases its session-level advisory lock.
            Pooling = false
        }.ConnectionString;
        await using var gate = new NpgsqlConnection(lockConnectionString);
        await gate.OpenAsync(ct);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", gate))
        {
            command.Parameters.AddWithValue("key", UploadLockKey);
            await command.ExecuteNonQueryAsync(ct);
        }

        // Only the server decides word owners (Captain 2026-11-06): a word the device creates is
        // recorded to the authenticated learner, its creator; a stored word keeps its stored
        // owner (the placement below applies only the learner's own). A client value never
        // decides it: it could blank a stored owner or name someone else.
        var wordsNamingOthers = owner is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : WordsNamingAnotherOwner(changes.Items, owner);
        var wordsWithoutDeviceOwner = owner is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : WordsWithoutOwner(changes.Items);
        RecordUploadedWordOwners(changes.Items, owner);

        // CoreSync SQLite reports one item per (row, operation), so a row inserted and
        // then updated before one sync arrives twice with identical current values.
        var items = owner is null ? changes.Items : CollapseRepeatedRows(changes.Items);

        IReadOnlySet<string> mergedProgress = new HashSet<string>();
        var mastery = NumberMasteryUploadMerge.Empty;
        var runs = ActivitySessionUploadMerge.RunPlan.Empty;
        var lookups = VocabularyLookupUploadMerge.Empty;
        if (owner is not null && items.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // A malformed upload, or a row that names another learner in its own owner column,
            // refuses the whole upload: a correct client never sends one. Anything decided from
            // the server's stored or inferred state is left out instead (below), because a
            // client's copy can disagree and a refused upload is offered again forever.
            if (IsMalformed(items)
                || NamesAnotherOwner(items, owner)
                || !await OwnerProfileExistsAsync(db, items, owner, ct))
            {
                logger.LogWarning("Native CoreSync upload refused a malformed row or another learner's row.");
                throw new BadHttpRequestException(
                    "Native sync refused a row outside the authenticated owner.",
                    StatusCodes.Status403Forbidden);
            }

            // Every other row the server cannot place for this owner stays on the device.
            var visible = await GetVisibleKeysAsync(db, owner, ct);
            items = await PlaceableItemsAsync(db, items, owner, visible, wordsNamingOthers, ct);

            // Last-writer-wins would drop attempts another device or the web app counted
            // since this device's copy. Merge them while the upload lock is held; the merged
            // values are force-written in CoreSync's transaction with this upload's contexts.
            mergedProgress = await VocabularyProgressConcurrentMerge.PrepareUploadAsync(
                db, items, owner, logger, ct);

            // Number Drill mastery from two devices: the most recently reviewed copy wins.
            mastery = await NumberMasteryUploadMerge.PrepareAsync(db, items, owner, ct);
            items = LatestCopyUploadMerge.WithoutLosers(items, mastery);

            // A quiz run saved on several clients: the most recent save wins, and a different
            // run of the same launch that loses is kept as its own record.
            runs = await ActivitySessionUploadMerge.PrepareAsync(db, items, owner, ct);
            items = LatestCopyUploadMerge.WithoutLosers(items, runs.Copies);
            await ActivitySessionUploadMerge.KeepReplacedRunsAsync(db, runs, ct);

            // Lookup history: the copy looked up most recently wins; the earliest first lookup is kept.
            lookups = await VocabularyLookupUploadMerge.PrepareAsync(db, items, owner, ct);
            items = LatestCopyUploadMerge.WithoutLosers(items, lookups.Copies);
        }

        var applied = owner is null ? changes : new SyncChangeSet(
            new SyncAnchor(scopedStoreId, changes.SourceAnchor.Version),
            changes.TargetAnchor, OrderNativeItems(items));
        var resolve = mergedProgress.Count == 0 && mastery.IsEmpty && runs.Copies.IsEmpty
            && lookups.Copies.IsEmpty ? onConflict : item =>
            item.TableName == VocabularyProgressConcurrentMerge.ProgressTable
                && item.ChangeType != ChangeType.Delete
                && Value(item, "Id") is { } id && mergedProgress.Contains(id)
                ? ConflictResolution.ForceWrite
                : LatestCopyUploadMerge.Resolve(mastery, item)
                    ?? LatestCopyUploadMerge.Resolve(runs.Copies, item)
                    ?? LatestCopyUploadMerge.Resolve(lookups.Copies, item)
                    ?? onConflict?.Invoke(item) ?? ConflictResolution.Skip;
        var anchor = await provider.ApplyChangesAsync(applied, resolve, ct);

        if (mastery.StoredWins.Count > 0 || runs.Copies.StoredWins.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await LatestCopyUploadMerge.TouchStoredWinnersAsync(db, mastery, owner!, ct);
            await LatestCopyUploadMerge.TouchStoredWinnersAsync(db, runs.Copies, owner!, ct);
        }

        if (!lookups.IsEmpty)
        {
            await using var scope = scopes.CreateAsyncScope();
            await VocabularyLookupUploadMerge.FinishAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), lookups, owner!, ct);
        }

        if (mergedProgress.Count > 0)
        {
            // CoreSync attributes the merged write to the uploading store, which would never
            // download it; a server-origin touch sends the merged row back in this same sync.
            await using var scope = scopes.CreateAsyncScope();
            await VocabularyProgressConcurrentMerge.TouchMergedAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                items, mergedProgress, owner!, ct);
        }

        // A word the device sent without an owner (an older build, or a legacy row) was stored
        // with the learner as owner, but CoreSync attributes the write to the uploading store,
        // which never downloads it, so the device's copy would stay ownerless and hidden there.
        // A server-origin touch sends the recorded owner back in this same sync.
        var unrecordedOnDevice = items
            .Where(item => item.TableName == "VocabularyWord" && item.ChangeType != ChangeType.Delete
                && wordsWithoutDeviceOwner.Contains(Value(item, "Id") ?? string.Empty))
            .Select(item => Value(item, "Id")!)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (owner is not null && unrecordedOnDevice.Length > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().VocabularyWords
                .Where(word => unrecordedOnDevice.Contains(word.Id) && word.UserProfileId == owner)
                .ExecuteUpdateAsync(set => set.SetProperty(word => word.UpdatedAt, word => word.UpdatedAt), ct);
        }
        return anchor;
    }

    /// <summary>Written words whose device copy names no owner (NULL or '').</summary>
    internal static HashSet<string> WordsWithoutOwner(IReadOnlyList<SyncItem> items) =>
        items.Where(item => item.TableName == "VocabularyWord" && item.Values is not null
                && item.ChangeType != ChangeType.Delete
                && VocabularyWordOwnership.IsOwnerless(Value(item, "UserProfileId"))
                && Value(item, "Id") is { Length: > 0 })
            .Select(item => Value(item, "Id")!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Folds repeated Insert/Update items for one row into a single item when every
    /// copy carries the same values, preferring Insert. Any other repetition (a delete,
    /// or differing values) is left in place so ownership validation still refuses it.
    /// </summary>
    internal static IReadOnlyList<SyncItem> CollapseRepeatedRows(IReadOnlyList<SyncItem> items)
    {
        var result = new List<SyncItem>(items.Count);
        foreach (var copies in items.GroupBy(
            item => (item.TableName, Id: item.Values is null ? null : Value(item, "Id"))))
        {
            var first = copies.First();
            if (copies.Key.Id is { Length: > 0 }
                && copies.All(copy => copy.ChangeType is ChangeType.Insert or ChangeType.Update
                    && SameValues(first, copy)))
                result.Add(copies.FirstOrDefault(copy => copy.ChangeType == ChangeType.Insert)
                    ?? first);
            else
                result.AddRange(copies);
        }
        return result;
    }

    private static bool SameValues(SyncItem first, SyncItem second) =>
        first.Values.Count == second.Values.Count
        && first.Values.All(pair => second.Values.TryGetValue(pair.Key, out var other)
            && System.Text.Json.JsonSerializer.Serialize(pair.Value?.Value)
                == System.Text.Json.JsonSerializer.Serialize(other?.Value));

    /// <summary>
    /// Replaces each written word's owner value with the server's choice: the authenticated
    /// <paramref name="owner"/> (for a new word that is its creator; for a stored word the
    /// placement keeps only the owner's own, so the stored owner is unchanged), or, for a
    /// headerless legacy upload, nothing (the stored owner stays; a new word stays ownerless and
    /// hidden). A client value never reaches the database.
    /// </summary>
    internal static void RecordUploadedWordOwners(IReadOnlyList<SyncItem> items, string? owner)
    {
        foreach (var item in items)
        {
            if (item.TableName != "VocabularyWord" || item.Values is null)
                continue;
            foreach (var key in item.Values.Keys
                .Where(key => string.Equals(key, "UserProfileId", StringComparison.OrdinalIgnoreCase))
                .ToArray())
                item.Values.Remove(key);
            if (owner is not null && item.ChangeType != ChangeType.Delete)
                item.Values["UserProfileId"] = new SyncItemValue(owner);
        }
    }

    /// <summary>
    /// Written words whose device copy names another learner. A new one is not this learner's
    /// creation, so it is left out (it stays on the device); a stored one is decided by its
    /// stored owner instead.
    /// </summary>
    internal static HashSet<string> WordsNamingAnotherOwner(IReadOnlyList<SyncItem> items, string owner) =>
        items.Where(item => item.TableName == "VocabularyWord" && item.Values is not null
                && item.ChangeType != ChangeType.Delete
                && Value(item, "UserProfileId") is { Length: > 0 } named && named != owner
                && Value(item, "Id") is { Length: > 0 })
            .Select(item => Value(item, "Id")!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Deletes first, children before parents, then writes, parents before children. Pinned
    /// CoreSync can skip a child-first FK insert despite returning a successful completion, so
    /// writes go parents first. A delete is the reverse: a minimal pair answer references its
    /// words with ON DELETE RESTRICT, so the answer must go before the word; and an old row
    /// deleted first frees its unique keys for the row that replaces it in the same upload.
    /// </summary>
    internal static IReadOnlyList<SyncItem> OrderNativeItems(IReadOnlyList<SyncItem> items)
    {
        static int Rank(SyncItem item) => item.TableName switch
        {
            "UserProfile" => 0,
            "SkillProfile" or "Conversation" or "MonitoredChannel"
                or "DailyPlan" or "VocabularyWord" => 1,
            "LearningResource" => 2,
            "ConversationChunk" or "VideoImport" or "DailyPlanCompletion"
                or "ResourceVocabularyMapping" or "PhraseConstituent" => 3,
            "VocabularyProgress" or "MinimalPair" or "MinimalPairSession" => 4,
            "VocabularyLearningContext" or "MinimalPairAttempt" => 5,
            _ => 6
        };
        return [
            .. items.Where(item => item.ChangeType == ChangeType.Delete).OrderByDescending(Rank),
            .. items.Where(item => item.ChangeType != ChangeType.Delete).OrderBy(Rank)];
    }

    /// <summary>A row without values or an ID, or one row sent twice with different values.</summary>
    private static bool IsMalformed(IReadOnlyList<SyncItem> items) =>
        items.Any(item => item.Values is null || string.IsNullOrWhiteSpace(Value(item, "Id")))
        || items.Where(item => item.ChangeType != ChangeType.Delete)
            .GroupBy(item => (item.TableName, Value(item, "Id")))
            .Any(group => group.Count() != 1);

    /// <summary>
    /// True when a written row names another learner in its own owner column: the one thing a
    /// client can always check before sending. A stored row that the server records or infers as
    /// another learner's is left out instead (<see cref="UnplaceableWritesAsync"/>): the client's
    /// copy may differ (an old shared word the device resolves to no owner, a word the server
    /// stamped after the device last synced). Deletes are not judged here.
    /// </summary>
    private static bool NamesAnotherOwner(IReadOnlyList<SyncItem> items, string owner) =>
        items.Any(item => item.ChangeType != ChangeType.Delete
            && SyncRowOwners.OwnerColumn(item.TableName) is { } column
            && Value(item, column) is { Length: > 0 } named && named != owner);

    private static async Task<bool> OwnerProfileExistsAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner, CancellationToken ct) =>
        await db.UserProfiles.AsNoTracking().AnyAsync(profile => profile.Id == owner, ct)
        || items.Any(item => item.TableName == "UserProfile"
            && item.ChangeType != ChangeType.Delete && Value(item, "Id") == owner);

    /// <summary>
    /// The rows of this upload the server applies. A row the server cannot place for the owner
    /// (it names no owner, its stored copy has none, its parent or word is not the owner's, or
    /// its table does not sync from devices) is left out instead of refusing the upload: it stays
    /// on the device, as native keeps its device-only rows, and everything else syncs. Leaving a
    /// row out can leave its children without a parent, so the check repeats until nothing more
    /// is left out. Deletes are applied only for the owner's own rows (<see cref="OwnDeletesAsync"/>).
    /// </summary>
    private async Task<IReadOnlyList<SyncItem>> PlaceableItemsAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner,
        VisibleKeys visibleBeforeUpload, IReadOnlySet<string> wordsNamingOthers, CancellationToken ct)
    {
        IReadOnlyList<SyncItem> writes = WithoutUnreachableExamples(
            items.Where(item => item.ChangeType != ChangeType.Delete).ToArray(),
            owner, visibleBeforeUpload);
        while (writes.Count > 0)
        {
            var unplaceable = await UnplaceableWritesAsync(
                db, writes, owner, visibleBeforeUpload.Copy(), wordsNamingOthers, ct);
            if (unplaceable.Count == 0)
                break;
            writes = writes.Where(item => !unplaceable.Contains(item)).ToArray();
        }
        var deletes = await OwnDeletesAsync(db, items, owner, ct);

        var kept = new HashSet<SyncItem>(writes.Concat(deletes), ReferenceEqualityComparer.Instance);
        var leftOut = items.Where(item => !kept.Contains(item)).ToArray();
        if (leftOut.Length > 0)
            logger.LogInformation(
                "Native CoreSync upload left {Count} rows on the device that the server cannot place for its owner: {Tables}.",
                leftOut.Length,
                string.Join(", ", leftOut.GroupBy(item => (item.TableName, item.ChangeType))
                    .Select(group => $"{group.Key.TableName} {group.Key.ChangeType} x{group.Count()}")));
        return [.. writes, .. deletes];
    }

    /// <summary>
    /// The deletes this upload applies (Captain 2026-11-04, decision 2): a device deletes only a
    /// row the server stores for this owner, looked up by its ID before the delete. A delete of a
    /// row this upload also writes is older history of a row the device has again. A learner
    /// profile is never deleted from a device, and a word only when nothing of another learner's
    /// depends on it (its links, progress, examples and pairs go with it). Any other delete is
    /// left out and the row stays: a device cannot know the owner of a row it already deleted, so
    /// such a delete never refuses the upload. The deletion log records each applied delete for
    /// the owner's other devices (migration AddSyncDeletionLog).
    /// </summary>
    private static async Task<IReadOnlyList<SyncItem>> OwnDeletesAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner, CancellationToken ct)
    {
        var written = items.Where(item => item.ChangeType != ChangeType.Delete)
            .Select(item => (item.TableName, Value(item, "Id")))
            .ToHashSet();
        var deletes = items.Where(item => item.ChangeType == ChangeType.Delete
                && item.TableName != "UserProfile" && SyncRowOwners.HasOwner(item.TableName)
                && !written.Contains((item.TableName, Value(item, "Id"))))
            .ToArray();
        var accepted = new List<SyncItem>();
        var deletedIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Words last: whether a word can go depends on the answers this upload deletes.
        foreach (var table in deletes.GroupBy(item => item.TableName, StringComparer.Ordinal)
            .OrderBy(group => group.Key == "VocabularyWord"))
        {
            var ids = table.Select(item => Value(item, "Id")!)
                .Distinct(StringComparer.Ordinal).ToArray();
            var stored = await SyncRowOwners.GetStoredOwnersAsync(db, table.Key, ids, ct);
            var owned = stored.Where(row => row.Value == owner)
                .Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
            if (table.Key == "VocabularyWord" && owned.Count > 0)
            {
                owned.ExceptWith(await VocabularyWordDependents.OthersDependOnAsync(
                    db, owned.ToArray(), owner, ct));
                owned.ExceptWith(await VocabularyWordDependents.AnswersStillUseAsync(
                    db, owned.ToArray(), deletedIds, ct));
            }
            deletedIds[table.Key] = owned;
            accepted.AddRange(table.Where(item => owned.Contains(Value(item, "Id")!)));
        }
        return accepted;
    }

    /// <summary>
    /// The written rows the server cannot place for <paramref name="owner"/> given the other rows
    /// of this upload, including rows whose stored copy is another learner's or nobody's (a word
    /// is its recorded owner's only; one recorded to no one is never changed by a sync) and new
    /// words whose device copy names another learner (<paramref name="wordsNamingOthers"/>). Rows
    /// that name another learner in an owner column were refused before this runs.
    /// </summary>
    private static async Task<HashSet<SyncItem>> UnplaceableWritesAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner,
        VisibleKeys visible, IReadOnlySet<string> wordsNamingOthers, CancellationToken ct)
    {
        var refused = new HashSet<SyncItem>(ReferenceEqualityComparer.Instance);

        // A stored row that is not this owner's (another learner's, or nobody's: saved before
        // its table had an owner) is never claimed or changed.
        foreach (var table in items.GroupBy(item => item.TableName, StringComparer.Ordinal))
        {
            var ids = table.Select(item => Value(item, "Id")!)
                .Distinct(StringComparer.Ordinal).ToArray();
            var existing = await SyncRowOwners.GetStoredOwnersAsync(db, table.Key, ids, ct);
            foreach (var item in table)
            {
                var id = Value(item, "Id")!;
                if (existing.TryGetValue(id, out var storedOwner)
                    ? !VocabularyWordOwnership.IsLearners(storedOwner, owner)
                    : table.Key == "VocabularyWord" && wordsNamingOthers.Contains(id))
                    refused.Add(item);
            }
        }

        var referencedWords = items
            .Where(item => item.TableName is "ResourceVocabularyMapping"
                or "VocabularyProgress" or "PhraseConstituent")
            .SelectMany(item => item.TableName == "PhraseConstituent"
                ? new[] { Value(item, "PhraseWordId"), Value(item, "ConstituentWordId") }
                : new[] { Value(item, "VocabularyWordId") })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var wordOwners = await SyncRowOwners.GetStoredWordOwnersAsync(db, referencedWords, ct);
        var uploadedWords = items
            .Where(item => item.TableName == "VocabularyWord")
            .Select(item => Value(item, "Id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);

        // A link, progress row or phrase part may name only the owner's own words: stored words
        // recorded to them, or words this upload creates (the owner's by creation). A missing
        // word is not theirs (fail closed).
        foreach (var item in items)
        {
            if ((item.TableName is "ResourceVocabularyMapping" or "VocabularyProgress")
                && !OwnsWord(Value(item, "VocabularyWordId")))
                refused.Add(item);
            if (item.TableName == "PhraseConstituent"
                && (!OwnsWord(Value(item, "PhraseWordId"))
                    || Value(item, "ConstituentWordId") is { Length: > 0 } constituentId
                    && !OwnsWord(constituentId)))
                refused.Add(item);
        }

        bool OwnsWord(string? id) =>
            !string.IsNullOrWhiteSpace(id)
            && (wordOwners.TryGetValue(id, out var storedOwner)
                ? VocabularyWordOwnership.IsLearners(storedOwner, owner)
                : uploadedWords.Contains(id));

        await RefuseUnlinkedReferencesAsync(db, items, owner, visible, refused, ct);
        return refused;
    }

    /// <summary>
    /// Adds to <paramref name="refused"/> every row whose owner-linked parent (a resource's
    /// skill, a chunk's conversation, an import's channel or resource, a plan item's resource or
    /// skill, a learning record's progress row or resource) is not the owner's, and every row
    /// the owner could not download (<see cref="IsVisible"/>).
    /// </summary>
    private static async Task RefuseUnlinkedReferencesAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner,
        VisibleKeys visible, HashSet<SyncItem> refused, CancellationToken ct)
    {
        var resources = items.Where(item => item.TableName == "LearningResource").ToArray();
        var chunks = items.Where(item => item.TableName == "ConversationChunk").ToArray();
        var videos = items.Where(item => item.TableName == "VideoImport").ToArray();
        var completions = items.Where(item => item.TableName == "DailyPlanCompletion").ToArray();
        var contexts = items.Where(item => item.TableName == "VocabularyLearningContext").ToArray();

        var resourceSkills = await StoredReferencesAsync<LearningResource>(
            db, resources, "SkillID", ct);
        var chunkConversations = await StoredReferencesAsync<ConversationChunk>(
            db, chunks, "ConversationId", ct);
        var videoChannels = await StoredReferencesAsync<VideoImport>(
            db, videos, "MonitoredChannelId", ct);
        var videoResources = await StoredReferencesAsync<VideoImport>(
            db, videos, "LearningResourceId", ct);
        var completionResources = await StoredReferencesAsync<DailyPlanCompletion>(
            db, completions, "ResourceId", ct);
        var completionSkills = await StoredReferencesAsync<DailyPlanCompletion>(
            db, completions, "SkillId", ct);
        var contextResources = await StoredReferencesAsync<VocabularyLearningContext>(
            db, contexts, "LearningResourceId", ct);

        var skills = await OwnedParentsAsync(db, "SkillProfile",
            resources.Select(item => Reference(item, "SkillID", resourceSkills))
                .Concat(completions.Select(item => Reference(item, "SkillId", completionSkills))),
            owner, ct);
        foreach (var item in items.Where(item => item.TableName == "SkillProfile"
            && Value(item, "UserProfileId") == owner && !refused.Contains(item)))
            skills.Add(Value(item, "Id")!);

        foreach (var item in resources)
        {
            if (Value(item, "UserProfileId") != owner
                || !OptionalParentIsOwned(Reference(item, "SkillID", resourceSkills), skills))
                refused.Add(item);
            else if (!refused.Contains(item))
                visible.ResourceIds.Add(Value(item, "Id")!);
        }

        var conversations = await OwnedParentsAsync(db, "Conversation",
            chunks.Select(item => Reference(item, "ConversationId", chunkConversations)),
            owner, ct);
        foreach (var item in items.Where(item => item.TableName == "Conversation"
            && Value(item, "UserProfileId") == owner && !refused.Contains(item)))
            conversations.Add(Value(item, "Id")!);
        foreach (var item in chunks.Where(item => Value(item, "UserProfileId") != owner
            || !conversations.Contains(
                Reference(item, "ConversationId", chunkConversations) ?? string.Empty)))
            refused.Add(item);

        var channels = await OwnedParentsAsync(db, "MonitoredChannel",
            videos.Select(item => Reference(item, "MonitoredChannelId", videoChannels)),
            owner, ct);
        foreach (var item in items.Where(item => item.TableName == "MonitoredChannel"
            && Value(item, "UserProfileId") == owner && !refused.Contains(item)))
            channels.Add(Value(item, "Id")!);
        foreach (var item in videos.Where(item => Value(item, "UserProfileId") != owner
            || !OptionalParentIsOwned(
                Reference(item, "MonitoredChannelId", videoChannels), channels)
            || !OptionalParentIsOwned(
                Reference(item, "LearningResourceId", videoResources), visible.ResourceIds)))
            refused.Add(item);

        foreach (var item in completions.Where(item => Value(item, "UserProfileId") != owner
            || !OptionalParentIsOwned(
                Reference(item, "ResourceId", completionResources), visible.ResourceIds)
            || !OptionalParentIsOwned(
                Reference(item, "SkillId", completionSkills), skills)))
            refused.Add(item);

        // Words this upload writes that are still placeable are the owner's (new ones by
        // creation, stored ones by their recorded owner), so their dependents can follow them.
        foreach (var item in items.Where(item =>
            item.TableName == "VocabularyWord" && !refused.Contains(item)))
            visible.WordIds.Add(Value(item, "Id")!);
        foreach (var item in items.Where(item => item.TableName == "VocabularyProgress"
            && !refused.Contains(item) && Value(item, "UserId") == owner
            && visible.WordIds.Contains(Value(item, "VocabularyWordId") ?? string.Empty)))
            visible.ProgressIds.Add(Value(item, "Id")!);
        // A pair this upload writes that the owner's devices receive can carry its answers.
        foreach (var item in items.Where(item => item.TableName == "MinimalPair"
            && !refused.Contains(item) && IsVisible(item, owner, visible)))
            visible.PairIds.Add(Value(item, "Id")!);

        var referencedProgress = contexts
            .Select(item => Value(item, "VocabularyProgressId"))
            .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (referencedProgress.Length > 0)
        {
            var storedProgress = await db.VocabularyProgresses.AsNoTracking()
                .Where(progress => referencedProgress.Contains(progress.Id)
                    && progress.UserId == owner
                    && visible.WordIds.Contains(progress.VocabularyWordId))
                .Select(progress => progress.Id).ToListAsync(ct);
            visible.ProgressIds.UnionWith(storedProgress);
        }

        foreach (var item in contexts.Where(item => !visible.ProgressIds.Contains(
                    Value(item, "VocabularyProgressId") ?? string.Empty)
                || !OptionalParentIsOwned(
                    Reference(item, "LearningResourceId", contextResources),
                    visible.ResourceIds)))
            refused.Add(item);
        foreach (var item in items.Where(item => !IsVisible(item, owner, visible)))
            refused.Add(item);
    }

    /// <summary>
    /// A device's own example sentence on a word that is not this owner's (another learner's,
    /// one recorded to no one, a word the server does not have), or from a resource that is not
    /// theirs, is left out of the upload rather than refusing the whole upload: the server would
    /// never send it back, and the row stays on the device. A sentence naming another owner is
    /// not dropped here; the owner checks refuse it.
    /// </summary>
    internal static IReadOnlyList<SyncItem> WithoutUnreachableExamples(
        IReadOnlyList<SyncItem> items, string owner, VisibleKeys visible)
    {
        bool IsOwnExample(SyncItem item) => item.TableName == "ExampleSentence"
            && item.ChangeType != ChangeType.Delete && Value(item, "UserProfileId") == owner;
        if (!items.Any(IsOwnExample))
            return items;

        var resources = new HashSet<string>(visible.ResourceIds, StringComparer.Ordinal);
        foreach (var item in items.Where(item => item.TableName == "LearningResource"
            && Value(item, "UserProfileId") == owner))
            resources.Add(Value(item, "Id")!);
        // The owner's stored words, plus words this upload writes (placement leaves out any that
        // are not theirs, and then the sentence with it).
        var words = new HashSet<string>(visible.WordIds, StringComparer.Ordinal);
        foreach (var item in items.Where(item => item.TableName == "VocabularyWord"))
            if (Value(item, "Id") is { } word)
                words.Add(word);

        return items.Where(item => !IsOwnExample(item)
            || (words.Contains(Value(item, "VocabularyWordId") ?? string.Empty)
                && (string.IsNullOrEmpty(Value(item, "LearningResourceId"))
                    || resources.Contains(Value(item, "LearningResourceId")!)))).ToArray();
    }

    private static Task<Dictionary<string, string?>> StoredReferencesAsync<TEntity>(
        ApplicationDbContext db, SyncItem[] items, string column, CancellationToken ct)
        where TEntity : class
    {
        var ids = items.Where(item => !item.Values.ContainsKey(column))
            .Select(item => Value(item, "Id")!).ToArray();
        return ids.Length == 0
            ? Task.FromResult(new Dictionary<string, string?>(StringComparer.Ordinal))
            : SyncRowOwners.DirectOwnersAsync<TEntity>(db, ids, column, ct);
    }

    private static async Task<HashSet<string>> OwnedParentsAsync(
        ApplicationDbContext db, string table, IEnumerable<string?> references,
        string owner, CancellationToken ct)
    {
        var ids = references.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        var stored = await SyncRowOwners.GetStoredOwnersAsync(db, table, ids, ct);
        return stored.Where(row => row.Value == owner)
            .Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static string? Reference(
        SyncItem item, string column, IReadOnlyDictionary<string, string?> stored)
    {
        if (item.Values.ContainsKey(column))
            return Value(item, column);
        return stored.TryGetValue(Value(item, "Id")!, out var value) ? value : null;
    }

    private static bool OptionalParentIsOwned(string? parentId, HashSet<string> owned) =>
        string.IsNullOrWhiteSpace(parentId) || owned.Contains(parentId);

    internal static string? GetNativeOwner(HttpContext? context)
    {
        if (context is null
            || !context.Request.Headers.TryGetValue(
                SharedSyncRegistration.NativeOwnerHeader, out var values))
            return null;

        var owner = values.Count == 1 ? values[0] : null;
        if (owner is null
            || !Guid.TryParse(owner, out _)
            || context.User.Identity?.IsAuthenticated != true
            || !context.Request.Headers.Authorization.ToString().StartsWith(
                "Bearer ", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.User.FindFirst(AuthClaimTypes.UserProfileId)?.Value,
                owner, StringComparison.Ordinal))
            throw new BadHttpRequestException(
                "Native sync owner must match the authenticated bearer.",
                StatusCodes.Status403Forbidden);

        return owner;
    }

    internal static async Task<SyncChangeSet> FilterForOwnerAsync(
        SyncChangeSet changes, string owner, ApplicationDbContext db, CancellationToken ct = default)
    {
        var visible = await GetVisibleKeysAsync(db, owner, ct);
        var deletes = await DeletesForOwnerAsync(db, changes.Items, owner, ct);
        var items = changes.Items.Where(item => item.ChangeType == ChangeType.Delete
            ? deletes.Contains(item)
            : IsVisible(item, owner, visible)).ToArray();
        return new SyncChangeSet(changes.SourceAnchor, changes.TargetAnchor, items);
    }

    /// <summary>
    /// The deletes a device of <paramref name="owner"/> receives (decision 2): rows gone from the
    /// server whose last logged delete names this owner ("SyncDeletion", filled by triggers for
    /// every delete path: the web app, cascades, other devices). Nobody else's devices learn of
    /// them, and a row that exists again on the server is not deleted on the device.
    /// </summary>
    private static async Task<IReadOnlySet<SyncItem>> DeletesForOwnerAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner, CancellationToken ct)
    {
        var result = new HashSet<SyncItem>(ReferenceEqualityComparer.Instance);
        var deletes = items.Where(item => item.ChangeType == ChangeType.Delete
            && item.TableName != "UserProfile" && SyncRowOwners.HasOwner(item.TableName)
            && !string.IsNullOrWhiteSpace(Value(item, "Id"))).ToArray();
        if (deletes.Length == 0)
            return result;

        foreach (var table in deletes.GroupBy(item => item.TableName, StringComparer.Ordinal))
        {
            var ids = table.Select(item => Value(item, "Id")!).Distinct(StringComparer.Ordinal).ToArray();
            var logged = await db.Database.SqlQueryRaw<LoggedDeletion>(LatestLoggedDeletionsSql,
                    new NpgsqlParameter("table", table.Key), new NpgsqlParameter("ids", ids))
                .ToListAsync(ct);
            var deletedFor = logged.ToDictionary(row => row.RowId, row => row.OwnerId, StringComparer.Ordinal);
            var existing = await SyncRowOwners.GetStoredOwnersAsync(db, table.Key, ids, ct);
            foreach (var item in table)
            {
                var id = Value(item, "Id")!;
                if (!existing.ContainsKey(id)
                    && deletedFor.TryGetValue(id, out var deletedOwner)
                    && deletedOwner == owner)
                    result.Add(item);
            }
        }
        return result;
    }

    /// <summary>
    /// The latest logged delete of each row, for one table at a time so PostgreSQL reads it
    /// through the ("TableName", "RowId") index instead of scanning the whole log.
    /// </summary>
    internal const string LatestLoggedDeletionsSql = """
        SELECT DISTINCT ON ("RowId") "TableName", "RowId", "OwnerId"
        FROM "SyncDeletion" WHERE "TableName" = @table AND "RowId" = ANY(@ids)
        ORDER BY "RowId", "Id" DESC
        """;

    private sealed class LoggedDeletion
    {
        public string TableName { get; set; } = string.Empty;
        public string RowId { get; set; } = string.Empty;
        public string? OwnerId { get; set; }
    }

    /// <summary>
    /// The owner's rows that decide what else a device may hold (Captain 2026-11-06: "There is
    /// no reason for a mobile device to sync any data for another user/owner than the one logged
    /// in."): their resources, the words recorded to them (whether or not a resource links them),
    /// their progress on those words, and their minimal pairs of two such words. Before, a device got the words its owner's resources
    /// linked, which sent another learner's or no one's word sitting in the learner's resource
    /// and withheld the learner's own words outside every resource.
    /// </summary>
    private static async Task<VisibleKeys> GetVisibleKeysAsync(
        ApplicationDbContext db, string owner, CancellationToken ct)
    {
        var resourceIds = (await db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == owner)
            .Select(resource => resource.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var wordIds = (await db.VocabularyWords.AsNoTracking().OwnedBy(owner)
            .Select(word => word.Id).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var progressIds = (await db.VocabularyProgresses.AsNoTracking()
            .Where(progress => progress.UserId == owner
                && db.VocabularyWords.Any(word => word.Id == progress.VocabularyWordId
                    && word.UserProfileId == owner))
            .Select(progress => progress.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        // The owner's pairs whose two words are theirs: the pairs a device receives, so the
        // only pairs its Minimal Pairs answers may name.
        var pairIds = (await db.MinimalPairs.AsNoTracking()
            .Where(pair => pair.UserId == owner
                && db.VocabularyWords.Any(word => word.Id == pair.VocabularyWordAId
                    && word.UserProfileId == owner)
                && db.VocabularyWords.Any(word => word.Id == pair.VocabularyWordBId
                    && word.UserProfileId == owner))
            .Select(pair => pair.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return new VisibleKeys(resourceIds, wordIds, progressIds, pairIds);
    }

    private static string? Value(SyncItem item, string column) =>
        item.Values.TryGetValue(column, out var value) ? value.Value?.ToString() : null;

    private static bool IsVisible(SyncItem item, string owner, VisibleKeys keys)
    {
        var id = Value(item, "Id");
        if (string.IsNullOrWhiteSpace(id))
            return false;

        return item.TableName switch
        {
            "UserProfile" => id == owner,
            "VocabularyProgress" => Value(item, "UserId") == owner
                && keys.ProgressIds.Contains(id),
            // A word goes only to its recorded owner's devices, and its dependents follow it, so a
            // device never holds a link, record or phrase part naming a word it lacks.
            "VocabularyWord" => Value(item, "UserProfileId") == owner && keys.WordIds.Contains(id),
            "ResourceVocabularyMapping" => keys.ResourceIds.Contains(
                    Value(item, "ResourceId") ?? string.Empty)
                && keys.WordIds.Contains(Value(item, "VocabularyWordId") ?? string.Empty),
            "VocabularyLearningContext" => keys.ProgressIds.Contains(
                Value(item, "VocabularyProgressId") ?? string.Empty),
            "PhraseConstituent" => keys.WordIds.Contains(
                    Value(item, "PhraseWordId") ?? string.Empty)
                && (string.IsNullOrEmpty(Value(item, "ConstituentWordId"))
                    || keys.WordIds.Contains(Value(item, "ConstituentWordId")!)),
            "LearningResource" or "SkillProfile" or "Conversation"
                or "ConversationChunk" or "MonitoredChannel" or "VideoImport"
                or "DailyPlan" or "DailyPlanCompletion" or "UserActivity"
                or "DiaryEntry" or "NumberAttempt" or "NumberMasteryProgress"
                or "WordAssociationScore" or "StreamHistory"
                or "ConversationMemoryState" => Value(item, "UserProfileId") == owner,
            "MinimalPairSession" => Value(item, "UserId") == owner,
            // Quiz answer receipts are device-only bookkeeping (phase 2b): refused on upload,
            // and never stored here to send.
            "ActivitySession" => Value(item, "UserId") == owner
                && !ActivitySessionTypes.IsDeviceOnly(Value(item, "ActivityType"), id),
            // A pair or answer travels only with words this owner can see, and an answer only
            // with a pair that travels, so a device never receives one whose words or pair it
            // lacks. A correct answer names only one word (prompt and choice are the same), so
            // its pair's other word is checked through the pair (review of bf701c42, finding 6).
            "MinimalPair" => Value(item, "UserId") == owner
                && keys.WordIds.Contains(Value(item, "VocabularyWordAId") ?? string.Empty)
                && keys.WordIds.Contains(Value(item, "VocabularyWordBId") ?? string.Empty),
            "MinimalPairAttempt" => Value(item, "UserId") == owner
                && keys.PairIds.Contains(Value(item, "PairId") ?? string.Empty)
                && keys.WordIds.Contains(Value(item, "PromptWordId") ?? string.Empty)
                && keys.WordIds.Contains(Value(item, "SelectedWordId") ?? string.Empty),
            // Phase 3: a photo stored on one device and a built-in scenario never travel; rows
            // with no owner (saved before these tables had one) never match an owner.
            "SceneImage" => Value(item, "UserProfileId") == owner
                && !SceneImage.IsDevicePhoto(Value(item, "Url")),
            "ConversationScenario" => Value(item, "UserProfileId") == owner
                && !SyncIds.IsBuiltInScenario(id),
            // An example travels with a word this owner can see, and only from their resource.
            "ExampleSentence" => Value(item, "UserProfileId") == owner
                && keys.WordIds.Contains(Value(item, "VocabularyWordId") ?? string.Empty)
                && (string.IsNullOrEmpty(Value(item, "LearningResourceId"))
                    || keys.ResourceIds.Contains(Value(item, "LearningResourceId")!)),
            // Lookup history (decision 4 B): only the owner's rows; no owner never matches.
            "VocabularyLookup" => Value(item, "UserProfileId") == owner,
            _ => false,
        };
    }

    internal sealed record VisibleKeys(
        HashSet<string> ResourceIds, HashSet<string> WordIds, HashSet<string> ProgressIds,
        HashSet<string> PairIds)
    {
        internal VisibleKeys Copy() => new(
            new HashSet<string>(ResourceIds, StringComparer.Ordinal),
            new HashSet<string>(WordIds, StringComparer.Ordinal),
            new HashSet<string>(ProgressIds, StringComparer.Ordinal),
            new HashSet<string>(PairIds, StringComparer.Ordinal));
    }

    public Task ApplyProvisionAsync(CancellationToken ct = default) =>
        provider.ApplyProvisionAsync(ct);

    public Task RemoveProvisionAsync(CancellationToken ct = default) =>
        provider.RemoveProvisionAsync(ct);

    public Task<SyncVersion> GetSyncVersionAsync(CancellationToken ct = default) =>
        provider.GetSyncVersionAsync(ct);

    public Task<SyncVersion> ApplyRetentionPolicyAsync(int minVersion, CancellationToken ct = default) =>
        provider.ApplyRetentionPolicyAsync(minVersion, ct);

    public Task EnableChangeTrackingForTable(string tableName, CancellationToken ct = default) =>
        provider.EnableChangeTrackingForTable(tableName, ct);

    public Task DisableChangeTrackingForTable(string tableName, CancellationToken ct = default) =>
        provider.DisableChangeTrackingForTable(tableName, ct);
}
