using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services;

/// <summary>
/// Who owns a synced row, resolved the same way by the server (PostgreSQL) and by a device
/// (SQLite), so a client can tell before uploading which rows the server treats as another
/// learner's (docs/sync-all-tables-plan.md, "Owner-checked sync for every client").
/// A row either names its owner in a column, or takes it from a parent row: a word link from
/// its resource, a learning record from its progress row, a phrase part from its phrase word,
/// and a word without a recorded owner from the one learner its links and progress name.
/// A row nobody can be proven to own resolves to null; the owner is never guessed.
/// </summary>
public static class SyncRowOwners
{
    /// <summary>The column that names a row's owner, or null when the owner comes from a parent.</summary>
    public static string? OwnerColumn(string table) => table switch
    {
        "UserProfile" => "Id",
        "MinimalPair" or "MinimalPairSession" or "MinimalPairAttempt"
            or "ActivitySession" or "VocabularyProgress" => "UserId",
        "LearningResource" or "SkillProfile" or "Conversation" or "ConversationChunk"
            or "MonitoredChannel" or "VideoImport" or "DailyPlan" or "DailyPlanCompletion"
            or "UserActivity" or "DiaryEntry" or "NumberAttempt" or "NumberMasteryProgress"
            or "WordAssociationScore" or "StreamHistory" or "ConversationMemoryState"
            or "SceneImage" or "ConversationScenario" or "ExampleSentence" => "UserProfileId",
        _ => null
    };

    /// <summary>True for every table whose rows have a provable owner (and so can sync).</summary>
    public static bool HasOwner(string table) =>
        OwnerColumn(table) is not null || table is "ResourceVocabularyMapping"
            or "VocabularyLearningContext" or "VocabularyWord" or "PhraseConstituent";

    /// <summary>
    /// The stored owner of each existing row with one of <paramref name="ids"/>; rows that do
    /// not exist are absent, and rows nobody provably owns map to null.
    /// </summary>
    public static Task<Dictionary<string, string?>> GetStoredOwnersAsync(
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
            "MinimalPair" => DirectOwnersAsync<MinimalPair>(db, ids, "UserId", ct),
            "MinimalPairSession" => DirectOwnersAsync<MinimalPairSession>(db, ids, "UserId", ct),
            "MinimalPairAttempt" => DirectOwnersAsync<MinimalPairAttempt>(db, ids, "UserId", ct),
            "StreamHistory" => DirectOwnersAsync<StreamHistory>(db, ids, "UserProfileId", ct),
            "ConversationMemoryState" => DirectOwnersAsync<ConversationMemoryState>(db, ids, "UserProfileId", ct),
            "ActivitySession" => DirectOwnersAsync<ActivitySession>(db, ids, "UserId", ct),
            "SceneImage" => DirectOwnersAsync<SceneImage>(db, ids, "UserProfileId", ct),
            "ConversationScenario" => DirectOwnersAsync<ConversationScenario>(db, ids, "UserProfileId", ct),
            "ExampleSentence" => DirectOwnersAsync<ExampleSentence>(db, ids, "UserProfileId", ct),
            "VocabularyProgress" => DirectOwnersAsync<VocabularyProgress>(db, ids, "UserId", ct),
            "ResourceVocabularyMapping" => MappingOwnersAsync(db, ids, ct),
            "VocabularyLearningContext" => ContextOwnersAsync(db, ids, ct),
            "VocabularyWord" => GetStoredWordOwnersAsync(db, ids, ct),
            "PhraseConstituent" => PhraseOwnersAsync(db, ids, ct),
            _ => Task.FromResult(new Dictionary<string, string?>(StringComparer.Ordinal))
        };

    /// <summary>
    /// A word's recorded owner, or for a word saved before words had one, the single learner
    /// its resource links and progress rows name (null when they name none or several).
    /// </summary>
    public static async Task<Dictionary<string, string?>> GetStoredWordOwnersAsync(
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

    public static async Task<Dictionary<string, string?>> DirectOwnersAsync<TEntity>(
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

    private sealed record StoredRowOwner(string Id, string? Owner);
    private sealed record WordOwner(string WordId, string? Owner);
}
