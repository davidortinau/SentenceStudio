using System.Security.Cryptography;
using System.Text;
using CoreSync.PostgreSQL;
using Npgsql;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Sync;

/// <summary>
/// Persists a native store's first authenticated owner in CoreSync's existing
/// anchor table. Native cursors use a separate, owner-derived store ID so
/// legacy clients continue using their original, unmodified cursors.
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
                && !await ExistingStoreBelongsToOwnerAsync(
                    connection, transaction, storeId, owner, ct))
            {
                logger.LogWarning(
                    "Native CoreSync refused an unbound store without exclusive stored-owner provenance.");
                throw new BadHttpRequestException(
                    "Native sync cannot verify the existing store owner.",
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

    private async Task<bool> ExistingStoreBelongsToOwnerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid storeId, string owner, CancellationToken ct)
    {
        var idsByTable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT DISTINCT tbl, pk_text
                FROM __core_sync_ct WHERE src = @store
                """;
            command.Parameters.AddWithValue("store", storeId.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    return false;
                var table = reader.GetString(0);
                var id = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(id))
                    return false;
                if (!idsByTable.TryGetValue(table, out var ids))
                    idsByTable[table] = ids = new HashSet<string>(StringComparer.Ordinal);
                ids.Add(id);
            }
        }

        if (idsByTable.Count == 0)
            return false;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var (table, ids) in idsByTable)
        {
            var stored = await NativeOwnerSyncProvider.GetStoredOwnersAsync(
                db, table, ids.ToArray(), ct);
            if (ids.Any(id => !stored.TryGetValue(id, out var rowOwner)
                || !string.Equals(rowOwner, owner, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }
}
