using System.Security.Cryptography;
using System.Text;
using CoreSync.PostgreSQL;
using Npgsql;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Sync;

/// <summary>
/// Persists a store's first authenticated owner in CoreSync's existing anchor table, so a
/// second account can never sync through the same device database (native keeps one
/// database per owner; a MAUI install has one, so its second account is refused). Owner-checked
/// cursors use a separate, owner-derived store ID; a store that synced before without the owner
/// header has its old cursor copied once.
/// </summary>
internal sealed class NativeSyncStoreBindings(
    PostgreSQLSyncProvider provider,
    IServiceScopeFactory scopes,
    ILogger<NativeSyncStoreBindings> logger)
{
    private const long BindingLockKey = 0x53534E4154495646L;
    private const string MarkerPrefix = "sentencestudio:native-store:v1:";

    internal async Task<Guid> BindAsync(Guid storeId, string owner, CancellationToken ct = default)
    {
        if (storeId == Guid.Empty || !Guid.TryParse(owner, out var ownerId)
            || ownerId == Guid.Empty)
        {
            logger.LogWarning("Native CoreSync refused an invalid store or owner identifier.");
            throw new BadHttpRequestException(
                "Native sync requires a valid store and owner.",
                StatusCodes.Status403Forbidden);
        }

        var prefix = $"{MarkerPrefix}{storeId:N}:";
        var marker = $"{prefix}{ownerId:N}";
        await using var connection = new NpgsqlConnection(provider.Configuration.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(@key)";
        command.Parameters.AddWithValue("key", BindingLockKey);
        await command.ExecuteNonQueryAsync(ct);

        command.CommandText = """
            SELECT id FROM __core_sync_remote_anchor
            WHERE id LIKE @pattern
            LIMIT 1
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("pattern", prefix + "%");
        var existing = await command.ExecuteScalarAsync(ct) as string;
        if (existing is null)
        {
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM __core_sync_remote_anchor WHERE id = @raw
                )
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("raw", storeId.ToString());
            if (await command.ExecuteScalarAsync(ct) is true
                && !await HistoryAllowsOwnerAsync(
                    connection, transaction, storeId, owner, ct))
            {
                logger.LogWarning(
                    "Native CoreSync refused an unbound store whose upload history names only other owners.");
                throw new BadHttpRequestException(
                    "Native sync store belongs to a different owner.",
                    StatusCodes.Status403Forbidden);
            }

            command.CommandText = "INSERT INTO __core_sync_remote_anchor (id) VALUES (@marker)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("marker", marker);
            await command.ExecuteNonQueryAsync(ct);
        }
        else if (!string.Equals(existing, marker, StringComparison.Ordinal))
        {
            logger.LogWarning("Native CoreSync refused a store bound to another owner.");
            throw new BadHttpRequestException(
                "Native sync store belongs to a different owner.",
                StatusCodes.Status403Forbidden);
        }

        // The review adapter's v1/v2 aliases replayed every row, rewriting
        // mixed SQLite-era local/UTC timestamps. Adopt the pre-adapter cursor
        // once instead; never update the raw cursor or an existing scoped one.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"SentenceStudio.Native.Store.v3:{ownerId:N}:{storeId:N}"));
        var scopedStoreId = new Guid(digest.AsSpan(0, 16));
        command.CommandText = """
            INSERT INTO __core_sync_remote_anchor (id, local_version, remote_version)
            SELECT @scoped, local_version, remote_version
            FROM __core_sync_remote_anchor WHERE id = @raw
            ON CONFLICT (id) DO NOTHING
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("scoped", scopedStoreId.ToString());
        command.Parameters.AddWithValue("raw", storeId.ToString());
        await command.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return scopedStoreId;
    }

    /// <summary>
    /// Whether <paramref name="owner"/> may take over a store that synced before without the
    /// owner header (an installed MAUI app, or a native build from before the header). Such a
    /// store downloaded every learner's rows unfiltered, so its cursor is valid for any owner;
    /// what matters is not handing it to someone else. The claim is refused only when the rows
    /// the store uploaded name owners and none of them is this one. Rows nobody can be proven to
    /// own (no owner, deleted since, legacy tables) prove nothing either way: an old MAUI
    /// database always holds some, and requiring every row to prove the owner left such an
    /// install unable to sync at all (Captain 2026-11-04, decision 1).
    /// </summary>
    private async Task<bool> HistoryAllowsOwnerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid storeId, string owner, CancellationToken ct)
    {
        var idsByTable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT DISTINCT tbl, pk_text
                FROM __core_sync_ct WHERE src = @store AND pk_text IS NOT NULL
                """;
            command.Parameters.AddWithValue("store", storeId.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0))
                    continue;
                var table = reader.GetString(0);
                if (!idsByTable.TryGetValue(table, out var ids))
                    idsByTable[table] = ids = new HashSet<string>(StringComparer.Ordinal);
                ids.Add(reader.GetString(1));
            }
        }

        var named = new HashSet<string>(StringComparer.Ordinal);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var (table, ids) in idsByTable)
        {
            var stored = await SentenceStudio.Services.SyncRowOwners.GetStoredOwnersAsync(
                db, table, ids.ToArray(), ct);
            foreach (var storedOwner in stored.Values)
                if (!string.IsNullOrWhiteSpace(storedOwner))
                    named.Add(storedOwner);
        }
        if (named.Count > 0 && !named.Contains(owner))
            return false;
        logger.LogInformation(
            "Native CoreSync bound a store with earlier headerless history ({Owners} owners named in it).",
            named.Count);
        return true;
    }
}
