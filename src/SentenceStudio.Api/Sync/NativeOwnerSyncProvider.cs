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
    ILogger<NativeOwnerSyncProvider> logger) : ISyncProvider
{
    private const long UploadLockKey = 0x53534E4154495645L;

    public string[]? SyncTableNames => provider.SyncTableNames;

    public Task<Guid> GetStoreIdAsync(CancellationToken ct = default) =>
        provider.GetStoreIdAsync(ct);

    public async Task SaveVersionForStoreAsync(
        Guid storeId, long version, CancellationToken ct = default)
    {
        var owner = GetNativeOwner(requests.HttpContext);
        var scopedStoreId = owner is null
            ? storeId : await stores.BindAsync(storeId, owner, ct);
        await provider.SaveVersionForStoreAsync(scopedStoreId, version, ct);
    }

    public async Task<SyncChangeSet> GetChangesAsync(
        Guid otherStoreId, SyncFilterParameter[]? filters = null,
        SyncDirection direction = SyncDirection.UploadAndDownload,
        string[]? tables = null, CancellationToken ct = default)
    {
        var owner = GetNativeOwner(requests.HttpContext);
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
        var owner = GetNativeOwner(requests.HttpContext);
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

        // Only the server decides word owners: a client value could blank a stored owner
        // or claim a word it cannot see is shared. Owners are stamped after apply.
        IgnoreClientWordOwners(changes.Items);

        // CoreSync SQLite reports one item per (row, operation), so a row inserted and
        // then updated before one sync arrives twice with identical current values.
        var items = owner is null ? changes.Items : CollapseRepeatedRows(changes.Items);

        IReadOnlySet<string> mergedProgress = new HashSet<string>();
        var mastery = NumberMasteryUploadMerge.Plan.Empty;
        if (owner is not null && items.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var visible = await GetVisibleKeysAsync(db, owner, ct);
            if (!await ExistingRowsBelongToOwnerAsync(db, items, owner, visible, ct)
                || !await OwnerLinkedReferencesMatchAsync(db, items, owner, visible, ct))
            {
                logger.LogWarning("Native CoreSync upload refused an existing row or parent outside the authenticated owner.");
                throw new BadHttpRequestException(
                    "Native sync refused a row outside the authenticated owner.",
                    StatusCodes.Status403Forbidden);
            }

            // Last-writer-wins would drop attempts another device or the web app counted
            // since this device's copy. Merge them while the upload lock is held; the merged
            // values are force-written in CoreSync's transaction with this upload's contexts.
            mergedProgress = await VocabularyProgressConcurrentMerge.PrepareUploadAsync(
                db, items, owner, logger, ct);

            // Number Drill mastery from two devices: the most recently reviewed copy wins.
            mastery = await NumberMasteryUploadMerge.PrepareAsync(db, items, owner, ct);
            items = NumberMasteryUploadMerge.WithoutLosers(items, mastery);
        }

        var applied = owner is null ? changes : new SyncChangeSet(
            new SyncAnchor(scopedStoreId, changes.SourceAnchor.Version),
            changes.TargetAnchor, OrderNativeItems(items));
        var resolve = mergedProgress.Count == 0 && mastery.IsEmpty ? onConflict : item =>
            item.TableName == VocabularyProgressConcurrentMerge.ProgressTable
                && item.ChangeType != ChangeType.Delete
                && Value(item, "Id") is { } id && mergedProgress.Contains(id)
                ? ConflictResolution.ForceWrite
                : NumberMasteryUploadMerge.Resolve(mastery, item)
                    ?? onConflict?.Invoke(item) ?? ConflictResolution.Skip;
        var anchor = await provider.ApplyChangesAsync(applied, resolve, ct);

        if (mastery.StoredWins.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            await NumberMasteryUploadMerge.TouchStoredWinnersAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                mastery, owner!, ct);
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

        // CoreSync writes rows directly, bypassing ApplicationDbContext's owner stamping.
        // Older clients never send VocabularyWord.UserProfileId, so stamp it here while
        // the upload lock is still held.
        var touchedWords = TouchedWordIds(items);
        if (touchedWords.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            await VocabularyWordOwnership.StampAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                touchedWords, cancellationToken: ct);
        }
        return anchor;
    }

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

    internal static void IgnoreClientWordOwners(IReadOnlyList<SyncItem> items)
    {
        foreach (var item in items)
        {
            if (item.TableName != "VocabularyWord" || item.Values is null)
                continue;
            foreach (var key in item.Values.Keys
                .Where(key => string.Equals(key, "UserProfileId", StringComparison.OrdinalIgnoreCase))
                .ToArray())
                item.Values.Remove(key);
        }
    }

    private static HashSet<string> TouchedWordIds(IReadOnlyList<SyncItem> items)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Values is null || item.ChangeType == ChangeType.Delete)
                continue;
            var id = item.TableName switch
            {
                "VocabularyWord" => Value(item, "Id"),
                "ResourceVocabularyMapping" or "VocabularyProgress" => Value(item, "VocabularyWordId"),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }
        return ids;
    }

    private static IReadOnlyList<SyncItem> OrderNativeItems(IReadOnlyList<SyncItem> items)
    {
        // Pinned CoreSync can skip a child-first FK insert despite returning a
        // successful completion. Native uploads apply validated parents first.
        return items.OrderBy(item => item.TableName switch
        {
            "UserProfile" => 0,
            "SkillProfile" or "Conversation" or "MonitoredChannel"
                or "DailyPlan" or "VocabularyWord" => 1,
            "LearningResource" => 2,
            "ConversationChunk" or "VideoImport" or "DailyPlanCompletion"
                or "ResourceVocabularyMapping" or "PhraseConstituent" => 3,
            "VocabularyProgress" => 4,
            "VocabularyLearningContext" => 5,
            _ => 6
        }).ToArray();
    }

    private static async Task<bool> ExistingRowsBelongToOwnerAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner,
        VisibleKeys visibleBeforeUpload, CancellationToken ct)
    {
        foreach (var table in items.GroupBy(item => item.TableName, StringComparer.Ordinal))
        {
            if (table.Any(item => item.Values is null
                || string.IsNullOrWhiteSpace(Value(item, "Id"))
                || item.ChangeType is not (ChangeType.Insert or ChangeType.Update))
                || table.GroupBy(item => Value(item, "Id"), StringComparer.Ordinal)
                    .Any(group => group.Count() != 1))
                return false;

            var ids = table.Select(item => Value(item, "Id"))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var existing = await GetStoredOwnersAsync(db, table.Key, ids, ct);
            if (existing.Values.Any(storedOwner => storedOwner != owner))
                return false;
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
        var wordOwners = await GetStoredWordOwnersAsync(db, referencedWords, ct);
        var uploadedWords = items
            .Where(item => item.TableName == "VocabularyWord")
            .Select(item => Value(item, "Id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if ((item.TableName is "ResourceVocabularyMapping" or "VocabularyProgress")
                && !CanReferenceWord(Value(item, "VocabularyWordId")))
                return false;
            if (item.TableName == "PhraseConstituent"
                && (!OwnsPhraseWord(Value(item, "PhraseWordId"))
                    || Value(item, "ConstituentWordId") is { Length: > 0 } constituentId
                    && !CanReferenceWord(constituentId)))
                return false;
        }

        return true;

        bool CanReferenceWord(string? id) =>
            !string.IsNullOrWhiteSpace(id)
            && (wordOwners.TryGetValue(id, out var storedOwner)
                ? storedOwner == owner || visibleBeforeUpload.WordIds.Contains(id)
                : uploadedWords.Contains(id));

        bool OwnsPhraseWord(string? id) =>
            !string.IsNullOrWhiteSpace(id)
            && (wordOwners.TryGetValue(id, out var storedOwner)
                ? storedOwner == owner
                : uploadedWords.Contains(id));
    }

    private static async Task<bool> OwnerLinkedReferencesMatchAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner,
        VisibleKeys visible, CancellationToken ct)
    {
        if (!await db.UserProfiles.AsNoTracking().AnyAsync(profile => profile.Id == owner, ct)
            && !items.Any(item => item.TableName == "UserProfile"
                && Value(item, "Id") == owner))
            return false;

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
            && Value(item, "UserProfileId") == owner))
            skills.Add(Value(item, "Id")!);

        foreach (var item in resources)
        {
            if (Value(item, "UserProfileId") != owner
                || !OptionalParentIsOwned(Reference(item, "SkillID", resourceSkills), skills))
                return false;
            visible.ResourceIds.Add(Value(item, "Id")!);
        }

        var conversations = await OwnedParentsAsync(db, "Conversation",
            chunks.Select(item => Reference(item, "ConversationId", chunkConversations)),
            owner, ct);
        foreach (var item in items.Where(item => item.TableName == "Conversation"
            && Value(item, "UserProfileId") == owner))
            conversations.Add(Value(item, "Id")!);
        if (chunks.Any(item => Value(item, "UserProfileId") != owner
            || !conversations.Contains(
                Reference(item, "ConversationId", chunkConversations) ?? string.Empty)))
            return false;

        var channels = await OwnedParentsAsync(db, "MonitoredChannel",
            videos.Select(item => Reference(item, "MonitoredChannelId", videoChannels)),
            owner, ct);
        foreach (var item in items.Where(item => item.TableName == "MonitoredChannel"
            && Value(item, "UserProfileId") == owner))
            channels.Add(Value(item, "Id")!);
        if (videos.Any(item => Value(item, "UserProfileId") != owner
            || !OptionalParentIsOwned(
                Reference(item, "MonitoredChannelId", videoChannels), channels)
            || !OptionalParentIsOwned(
                Reference(item, "LearningResourceId", videoResources), visible.ResourceIds)))
            return false;

        if (completions.Any(item => Value(item, "UserProfileId") != owner
            || !OptionalParentIsOwned(
                Reference(item, "ResourceId", completionResources), visible.ResourceIds)
            || !OptionalParentIsOwned(
                Reference(item, "SkillId", completionSkills), skills)))
            return false;

        foreach (var item in items.Where(item =>
            item.TableName == "ResourceVocabularyMapping"
            && visible.ResourceIds.Contains(Value(item, "ResourceId") ?? string.Empty)))
        {
            if (Value(item, "VocabularyWordId") is { } wordId)
                visible.WordIds.Add(wordId);
        }
        foreach (var item in items.Where(item => item.TableName == "VocabularyProgress"
            && Value(item, "UserId") == owner
            && visible.WordIds.Contains(Value(item, "VocabularyWordId") ?? string.Empty)))
            visible.ProgressIds.Add(Value(item, "Id")!);

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

        return contexts.All(item => visible.ProgressIds.Contains(
                    Value(item, "VocabularyProgressId") ?? string.Empty)
                && OptionalParentIsOwned(
                    Reference(item, "LearningResourceId", contextResources),
                    visible.ResourceIds))
            && items.All(item => IsVisible(item, owner, visible));
    }

    private static Task<Dictionary<string, string?>> StoredReferencesAsync<TEntity>(
        ApplicationDbContext db, SyncItem[] items, string column, CancellationToken ct)
        where TEntity : class
    {
        var ids = items.Where(item => !item.Values.ContainsKey(column))
            .Select(item => Value(item, "Id")!).ToArray();
        return ids.Length == 0
            ? Task.FromResult(new Dictionary<string, string?>(StringComparer.Ordinal))
            : DirectOwnersAsync<TEntity>(db, ids, column, ct);
    }

    private static async Task<HashSet<string>> OwnedParentsAsync(
        ApplicationDbContext db, string table, IEnumerable<string?> references,
        string owner, CancellationToken ct)
    {
        var ids = references.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        var stored = await GetStoredOwnersAsync(db, table, ids, ct);
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

    internal static Task<Dictionary<string, string?>> GetStoredOwnersAsync(
        ApplicationDbContext db, string table, string[] ids, CancellationToken ct) =>
        table switch
        {
            "UserProfile" => DirectOwnersAsync<UserProfile>(db, ids, "Id", ct),
            "LearningResource" => DirectOwnersAsync<LearningResource>(db, ids, "UserProfileId", ct),
            "SkillProfile" => DirectOwnersAsync<SkillProfile>(db, ids, "UserProfileId", ct),
            "Conversation" => DirectOwnersAsync<Conversation>(db, ids, "UserProfileId", ct),
            "ConversationChunk" => DirectOwnersAsync<ConversationChunk>(db, ids, "UserProfileId", ct),
            "MonitoredChannel" => DirectOwnersAsync<MonitoredChannel>(db, ids, "UserProfileId", ct),
            "VideoImport" => DirectOwnersAsync<VideoImport>(db, ids, "UserProfileId", ct),
            "DailyPlan" => DirectOwnersAsync<DailyPlan>(db, ids, "UserProfileId", ct),
            "DailyPlanCompletion" => DirectOwnersAsync<DailyPlanCompletion>(db, ids, "UserProfileId", ct),
            "UserActivity" => DirectOwnersAsync<UserActivity>(db, ids, "UserProfileId", ct),
            "DiaryEntry" => DirectOwnersAsync<DiaryEntry>(db, ids, "UserProfileId", ct),
            "NumberAttempt" => DirectOwnersAsync<NumberAttempt>(db, ids, "UserProfileId", ct),
            "NumberMasteryProgress" => DirectOwnersAsync<NumberMasteryProgress>(db, ids, "UserProfileId", ct),
            "WordAssociationScore" => DirectOwnersAsync<WordAssociationScore>(db, ids, "UserProfileId", ct),
            "VocabularyProgress" => DirectOwnersAsync<VocabularyProgress>(db, ids, "UserId", ct),
            "ResourceVocabularyMapping" => MappingOwnersAsync(db, ids, ct),
            "VocabularyLearningContext" => ContextOwnersAsync(db, ids, ct),
            "VocabularyWord" => GetStoredWordOwnersAsync(db, ids, ct),
            "PhraseConstituent" => PhraseOwnersAsync(db, ids, ct),
            _ => Task.FromResult(new Dictionary<string, string?>(StringComparer.Ordinal))
        };

    private static async Task<Dictionary<string, string?>> DirectOwnersAsync<TEntity>(
        ApplicationDbContext db, string[] ids, string ownerProperty, CancellationToken ct)
        where TEntity : class
    {
        var rows = await db.Set<TEntity>().AsNoTracking()
            .Where(row => ids.Contains(EF.Property<string>(row, "Id")))
            .Select(row => new StoredRowOwner(
                EF.Property<string>(row, "Id"), EF.Property<string?>(row, ownerProperty)))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.Id, row => row.Owner, StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, string?>> MappingOwnersAsync(
        ApplicationDbContext db, string[] ids, CancellationToken ct)
    {
        var rows = await (
            from mapping in db.ResourceVocabularyMappings.AsNoTracking()
            join resource in db.LearningResources.AsNoTracking()
                on mapping.ResourceId equals resource.Id into resources
            from resource in resources.DefaultIfEmpty()
            where ids.Contains(mapping.Id)
            select new StoredRowOwner(mapping.Id,
                resource == null ? null : resource.UserProfileId))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.Id, row => row.Owner, StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, string?>> ContextOwnersAsync(
        ApplicationDbContext db, string[] ids, CancellationToken ct)
    {
        var rows = await (
            from context in db.VocabularyLearningContexts.AsNoTracking()
            join progress in db.VocabularyProgresses.AsNoTracking()
                on context.VocabularyProgressId equals progress.Id into progresses
            from progress in progresses.DefaultIfEmpty()
            where ids.Contains(context.Id)
            select new StoredRowOwner(context.Id,
                progress == null ? null : progress.UserId))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.Id, row => row.Owner, StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, string?>> PhraseOwnersAsync(
        ApplicationDbContext db, string[] ids, CancellationToken ct)
    {
        var rows = await db.PhraseConstituents.AsNoTracking()
            .Where(phrase => ids.Contains(phrase.Id))
            .Select(phrase => new { phrase.Id, phrase.PhraseWordId })
            .ToListAsync(ct);
        var owners = await GetStoredWordOwnersAsync(
            db, rows.Select(row => row.PhraseWordId).Distinct(StringComparer.Ordinal).ToArray(), ct);
        return rows.ToDictionary(
            row => row.Id, row => owners.GetValueOrDefault(row.PhraseWordId), StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, string?>> GetStoredWordOwnersAsync(
        ApplicationDbContext db, string[] ids, CancellationToken ct)
    {
        var stored = await db.VocabularyWords.AsNoTracking()
            .Where(word => ids.Contains(word.Id))
            .Select(word => new { word.Id, word.UserProfileId })
            .ToListAsync(ct);
        if (stored.Count == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        var recorded = stored.Where(word => word.UserProfileId != null)
            .ToDictionary(word => word.Id, word => word.UserProfileId, StringComparer.Ordinal);
        var existing = stored.Where(word => word.UserProfileId == null)
            .Select(word => word.Id).ToList();
        if (existing.Count == 0)
            return recorded;

        var mappingOwners = await (
            from mapping in db.ResourceVocabularyMappings.AsNoTracking()
            join resource in db.LearningResources.AsNoTracking()
                on mapping.ResourceId equals resource.Id into resources
            from resource in resources.DefaultIfEmpty()
            where existing.Contains(mapping.VocabularyWordId)
            select new WordOwner(mapping.VocabularyWordId,
                resource == null ? null : resource.UserProfileId))
            .ToListAsync(ct);
        var progressOwners = await db.VocabularyProgresses.AsNoTracking()
            .Where(progress => existing.Contains(progress.VocabularyWordId))
            .Select(progress => new WordOwner(progress.VocabularyWordId, progress.UserId))
            .ToListAsync(ct);
        var ownersByWord = mappingOwners.Concat(progressOwners)
            .GroupBy(reference => reference.WordId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(reference => reference.Owner).Distinct().ToArray(),
                StringComparer.Ordinal);
        foreach (var id in existing)
            recorded[id] = ownersByWord.TryGetValue(id, out var owners) && owners.Length == 1
                ? owners[0] : null;
        return recorded;
    }

    private sealed record StoredRowOwner(string Id, string? Owner);
    private sealed record WordOwner(string WordId, string? Owner);

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
        var items = changes.Items.Where(item => IsVisible(item, owner, visible)).ToArray();
        return new SyncChangeSet(changes.SourceAnchor, changes.TargetAnchor, items);
    }

    private static async Task<VisibleKeys> GetVisibleKeysAsync(
        ApplicationDbContext db, string owner, CancellationToken ct)
    {
        var resourceIds = (await db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == owner)
            .Select(resource => resource.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var wordIds = (await db.ResourceVocabularyMappings.AsNoTracking()
            .Where(mapping => resourceIds.Contains(mapping.ResourceId))
            .Select(mapping => mapping.VocabularyWordId).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var progressIds = (await db.VocabularyProgresses.AsNoTracking()
            .Where(progress => progress.UserId == owner
                && wordIds.Contains(progress.VocabularyWordId))
            .Select(progress => progress.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return new VisibleKeys(resourceIds, wordIds, progressIds);
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
            "VocabularyWord" => keys.WordIds.Contains(id),
            "ResourceVocabularyMapping" => keys.ResourceIds.Contains(
                Value(item, "ResourceId") ?? string.Empty),
            "VocabularyLearningContext" => keys.ProgressIds.Contains(
                Value(item, "VocabularyProgressId") ?? string.Empty),
            "PhraseConstituent" => keys.WordIds.Contains(
                Value(item, "PhraseWordId") ?? string.Empty),
            "LearningResource" or "SkillProfile" or "Conversation"
                or "ConversationChunk" or "MonitoredChannel" or "VideoImport"
                or "DailyPlan" or "DailyPlanCompletion" or "UserActivity"
                or "DiaryEntry" or "NumberAttempt" or "NumberMasteryProgress"
                or "WordAssociationScore" => Value(item, "UserProfileId") == owner,
            _ => false,
        };
    }

    private sealed record VisibleKeys(
        HashSet<string> ResourceIds, HashSet<string> WordIds, HashSet<string> ProgressIds);

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
