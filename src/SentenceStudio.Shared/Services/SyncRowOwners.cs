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
/// its resource, a learning record from its progress row, a phrase part from its phrase word.
/// A word is its recorded owner's only; a word recorded to no one is no one's (never inferred).
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
            or "SceneImage" or "ConversationScenario" or "ExampleSentence"
            or "VocabularyLookup" => "UserProfileId",
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
            "VocabularyLookup" => DirectOwnersAsync<VocabularyLookup>(db, ids, "UserProfileId", ct),
            "VocabularyProgress" => DirectOwnersAsync<VocabularyProgress>(db, ids, "UserId", ct),
            "ResourceVocabularyMapping" => MappingOwnersAsync(db, ids, ct),
            "VocabularyLearningContext" => ContextOwnersAsync(db, ids, ct),
            "VocabularyWord" => GetStoredWordOwnersAsync(db, ids, ct),
            "PhraseConstituent" => PhraseOwnersAsync(db, ids, ct),
            _ => Task.FromResult(new Dictionary<string, string?>(StringComparer.Ordinal))
        };

    /// <summary>
    /// Each existing word's recorded owner (<see cref="VocabularyWord.UserProfileId"/>); null for
    /// a word recorded to no one. One runtime rule everywhere (Captain 2026-11-06): a word is a
    /// learner's only when it is recorded to them. Owners are never inferred from links or
    /// progress at runtime; the one-time backfill (scripts/assign-vocabulary-word-owners.sql)
    /// and the stamping of new words record them.
    /// </summary>
    public static Task<Dictionary<string, string?>> GetStoredWordOwnersAsync(
        ApplicationDbContext db, string[] ids, CancellationToken ct) =>
        DirectOwnersAsync<VocabularyWord>(db, ids, "UserProfileId", ct);

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
}
