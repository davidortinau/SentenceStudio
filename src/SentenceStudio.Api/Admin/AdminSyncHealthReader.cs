using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Sync;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Api.Admin;

/// <summary>
/// Reads sync health from the server's own tables. Counts and timestamps only: no row's content is
/// selected, and every per-learner query filters by the learner id it is given.
/// </summary>
internal static class AdminSyncHealthReader
{
    /// <summary>One synced table: how a learner's rows are counted and which column says when one last changed.</summary>
    internal sealed record TableSpec(
        string Name,
        string? NewestField,
        Func<ApplicationDbContext, string, CancellationToken, Task<(long Count, DateTime? Newest)>>? Read);

    // Every table SharedSyncRegistration syncs, in its order. A test pins this list to the registration.
    internal static readonly IReadOnlyList<TableSpec> Tables =
    [
        Direct<LearningResource>("LearningResource", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<VocabularyWord>("VocabularyWord", "UserProfileId", r => r.UpdatedAt, "UpdatedAt"),
        Mapping(),
        NoOwner("Challenge"),
        Direct<Conversation>("Conversation", null, r => r.CreatedAt, "CreatedAt"),
        Direct<ConversationChunk>("ConversationChunk", null, r => r.SentTime, "SentTime"),
        Direct<UserProfile>("UserProfile", null, r => r.CreatedAt, "CreatedAt"),
        Direct<SkillProfile>("SkillProfile", null, r => r.UpdatedAt, "UpdatedAt"),
        NoOwner("VocabularyList"),
        Direct<VocabularyProgress>("VocabularyProgress", null, r => r.UpdatedAt, "UpdatedAt"),
        Context(),
        Direct<MonitoredChannel>("MonitoredChannel", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<VideoImport>("VideoImport", null, r => r.CreatedAt, "CreatedAt"),
        Direct<DailyPlan>("DailyPlan", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<DailyPlanCompletion>("DailyPlanCompletion", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<UserActivity>("UserActivity", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<DiaryEntry>("DiaryEntry", null, r => r.UpdatedAt, "UpdatedAt"),
        Phrase(),
        Direct<NumberAttempt>("NumberAttempt", null, r => r.AttemptedAt, "AttemptedAt"),
        Direct<NumberMasteryProgress>("NumberMasteryProgress", null, r => r.LastReviewed, "LastReviewed"),
        Direct<WordAssociationScore>("WordAssociationScore", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<MinimalPair>("MinimalPair", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<MinimalPairSession>("MinimalPairSession", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<MinimalPairAttempt>("MinimalPairAttempt", null, r => r.CreatedAt, "CreatedAt"),
        Direct<StreamHistory>("StreamHistory", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<ConversationMemoryState>("ConversationMemoryState", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<ActivitySession>("ActivitySession", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<SceneImage>("SceneImage", null, null, null),
        Direct<ConversationScenario>("ConversationScenario", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<ExampleSentence>("ExampleSentence", null, r => r.UpdatedAt, "UpdatedAt"),
        Direct<VocabularyLookup>("VocabularyLookup", null, r => r.LastLookedUpAt, "LastLookedUpAt"),
        Direct<VocabularyDuplicateDecision>("VocabularyDuplicateDecision", null, r => r.DecidedAt, "DecidedAt")
    ];

    /// <param name="ownerColumn">
    /// Null takes the column from <see cref="SyncRowOwners.OwnerColumn"/>, the sync's own rule, so
    /// this page cannot count by a column the sync has stopped using. Only a word names its own:
    /// the sync resolves a word's owner through a rule of its own.
    /// </param>
    private static TableSpec Direct<T>(
        string name, string? ownerColumn, Expression<Func<T, DateTime?>>? newest, string? field)
        where T : class
    {
        var ownerProperty = ownerColumn
            ?? SyncRowOwners.OwnerColumn(name)
            ?? throw new InvalidOperationException($"The sync names no owner column for {name}.");
        return new(name, field, async (db, learnerId, ct) =>
        {
            var rows = db.Set<T>().AsNoTracking()
                .Where(row => EF.Property<string?>(row, ownerProperty) == learnerId);
            return (await rows.LongCountAsync(ct),
                newest is null ? null : await rows.MaxAsync(newest, ct));
        });
    }

    private static TableSpec NoOwner(string name) => new(name, null, null);

    // A word link takes its owner from its resource.
    private static TableSpec Mapping() =>
        new("ResourceVocabularyMapping", null, async (db, learnerId, ct) =>
        {
            var rows = db.ResourceVocabularyMappings.AsNoTracking().Where(m =>
                db.LearningResources.Any(r => r.Id == m.ResourceId && r.UserProfileId == learnerId));
            return (await rows.LongCountAsync(ct), (DateTime?)null);
        });

    // A learning record takes its owner from its progress row.
    private static TableSpec Context() =>
        new("VocabularyLearningContext", "UpdatedAt", async (db, learnerId, ct) =>
        {
            var rows = db.VocabularyLearningContexts.AsNoTracking().Where(c =>
                db.VocabularyProgresses.Any(p => p.Id == c.VocabularyProgressId && p.UserId == learnerId));
            return (await rows.LongCountAsync(ct), await rows.MaxAsync(c => (DateTime?)c.UpdatedAt, ct));
        });

    // A phrase part takes its owner from its phrase word.
    private static TableSpec Phrase() =>
        new("PhraseConstituent", "CreatedAt", async (db, learnerId, ct) =>
        {
            var rows = db.PhraseConstituents.AsNoTracking().Where(p =>
                db.VocabularyWords.Any(w => w.Id == p.PhraseWordId && w.UserProfileId == learnerId));
            return (await rows.LongCountAsync(ct), await rows.MaxAsync(p => (DateTime?)p.CreatedAt, ct));
        });

    public static async Task<IReadOnlyList<AdminSyncTableHealth>> ReadTablesAsync(
        ApplicationDbContext db, string learnerId, CancellationToken ct)
    {
        var result = new List<AdminSyncTableHealth>(Tables.Count);
        foreach (var spec in Tables)
        {
            if (spec.Read is null)
            {
                result.Add(new AdminSyncTableHealth(spec.Name, AdminSyncTableStatus.NoOwner, null, null, null));
                continue;
            }

            var (count, newest) = await spec.Read(db, learnerId, ct);
            result.Add(new AdminSyncTableHealth(
                spec.Name, AdminSyncTableStatus.Counted, count, spec.NewestField, AsUtc(newest)));
        }

        return result;
    }

    /// <summary>
    /// The device stores bound to this learner, from the owner marker rows
    /// (<c>sentencestudio:native-store:v1:&lt;store&gt;:&lt;owner&gt;</c>) and the cursor row each store syncs under.
    /// A store that only ever synced without the owner header has no marker and is not listed.
    /// </summary>
    public static async Task<IReadOnlyList<AdminSyncDevice>> ReadDevicesAsync(
        ApplicationDbContext db, string learnerId, long? serverVersion, CancellationToken ct)
    {
        if (!Guid.TryParse(learnerId, out var ownerId) || !await HasTableAsync(db, "__core_sync_remote_anchor", ct))
        {
            return [];
        }

        var connection = db.Database.GetDbConnection();
        await using var lease = await OpenAsync(connection, ct);

        var suffix = $":{ownerId:N}";
        var prefix = NativeSyncStoreBindings.StoreMarkerPrefix;
        var markers = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM __core_sync_remote_anchor WHERE id LIKE @pattern";
            AddParameter(command, "@pattern", prefix + "%" + suffix);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                markers.Add(reader.GetString(0));
            }
        }

        var devices = new List<AdminSyncDevice>();
        foreach (var marker in markers.Order(StringComparer.Ordinal))
        {
            // The marker is exactly prefix + 32 hex store + ':' + 32 hex owner; anything else is not ours.
            var body = marker[prefix.Length..];
            if (body.Length != 32 + suffix.Length || !body.EndsWith(suffix, StringComparison.Ordinal)
                || !Guid.TryParseExact(body[..32], "N", out var storeId))
            {
                continue;
            }

            var cursorId = NativeSyncStoreBindings.ScopedStoreId(ownerId, storeId).ToString();
            long? downloaded = null;
            long? uploaded = null;
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT local_version, remote_version FROM __core_sync_remote_anchor WHERE id = @id";
            AddParameter(command, "@id", cursorId);
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    downloaded = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                    uploaded = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                }
            }

            long? behind = serverVersion is { } head && downloaded is { } seen ? Math.Max(0, head - seen) : null;
            devices.Add(new AdminSyncDevice(storeId.ToString("N"), downloaded, uploaded, behind));
        }

        return devices;
    }

    /// <summary>The newest and oldest change numbers in CoreSync's change log; null for each when the log is empty or absent.</summary>
    public static async Task<(long? Newest, long? Oldest)> ReadChangeLogRangeAsync(
        ApplicationDbContext db, CancellationToken ct)
    {
        if (!await HasTableAsync(db, "__core_sync_ct", ct))
        {
            return (null, null);
        }

        var connection = db.Database.GetDbConnection();
        await using var lease = await OpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(id), MIN(id) FROM __core_sync_ct";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (null, null);
        }

        return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    public static string ApiVersion() =>
        typeof(AdminSyncHealthReader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AdminSyncHealthReader).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static async Task<bool> HasTableAsync(ApplicationDbContext db, string table, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var lease = await OpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        // The same lookup on both providers. PostgreSQL folds the unquoted name to lower case; the
        // SQLite tables are created upper case and compared without regard to case.
        command.CommandText = db.Database.IsNpgsql()
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND lower(name) = @name";
        AddParameter(command, "@name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    // An aggregate over a timestamptz column can come back as local time (the API runs with
    // Npgsql's legacy timestamp behaviour); one with no kind was stored as UTC.
    internal static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { } v => DateTime.SpecifyKind(v, DateTimeKind.Utc)
    };

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Opens the context's connection if it is closed, and closes it again on dispose only if this call opened it.</summary>
    private static async Task<ConnectionLease> OpenAsync(DbConnection connection, CancellationToken ct)
    {
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct);
        }

        return new ConnectionLease(connection, opened);
    }

    private readonly struct ConnectionLease(DbConnection connection, bool opened) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => opened ? new ValueTask(connection.CloseAsync()) : ValueTask.CompletedTask;
    }
}
