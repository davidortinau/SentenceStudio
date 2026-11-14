using System.Collections.Concurrent;
using CoreSync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// Keeps a download's foreign-key cascades out of the next upload. CoreSync's SQLite provider marks
/// only the newest change-log entry of each row it applies as the server's (it sets SRC on
/// MAX(ID)), and SQLite logs the rows a delete removes or clears through ON DELETE CASCADE and
/// SET NULL before the deleted row itself. Those entries stayed this device's own changes, and the
/// next upload sent deletes the server never asked for: on 2026-11-13 a duplicate merge on the web
/// moved the Captain's answers to the kept words, and Duotopia's next sync deleted 527 of them
/// (docs/progress-count-repair-2026-11-13.md).
/// <para>
/// <see cref="ApplyAsync"/> lists the rows the download's deletes reach through foreign keys in
/// <c>__SS_DOWNLOAD_CASCADE</c>, and a trigger on the change log marks those rows' entries as the
/// server's as they are written, inside CoreSync's own transaction: the marks commit or roll back
/// with the download and no cancellation can separate them. The list is emptied when the apply
/// returns, and before every upload in case the app stopped first. SQLite has one writer at a
/// time, so the learner's own changes never fall inside the apply.
/// </para>
/// </summary>
public static class SyncDownloadCascades
{
    private const string PlanTable = "__SS_DOWNLOAD_CASCADE";
    private const int ChunkSize = 500;

    private sealed record ForeignKey(string Child, string ChildColumn, string ParentColumn, bool Cascade);

    private sealed record Schema(
        IReadOnlyDictionary<string, List<ForeignKey>> ByParent,
        IReadOnlyDictionary<string, string?> PrimaryKeys);

    /// <summary>The foreign keys of each store, read once: the schema does not change while the app runs.</summary>
    private static readonly ConcurrentDictionary<string, Schema> Schemas = new(StringComparer.Ordinal);

    /// <summary>
    /// Applies a download with the rows its deletes reach marked as the server's. Returns the
    /// apply's anchor; logs how many rows were listed.
    /// </summary>
    public static async Task<SyncAnchor> ApplyAsync(string connectionString, SyncChangeSet download,
        Func<Task<SyncAnchor>> apply, ILogger logger, CancellationToken cancellationToken = default)
    {
        var reached = await ReachAsync(connectionString, download, cancellationToken);
        if (reached.Count == 0)
            return await apply();
        await ListAsync(connectionString, reached, download.SourceAnchor.StoreId, cancellationToken);
        SyncAnchor anchor;
        try
        {
            anchor = await apply();
        }
        finally
        {
            // Not cancellable: a list left behind would mark the learner's next changes to these
            // rows. If it cannot be emptied now, the next upload empties it first.
            try
            {
                await ClearAsync(connectionString, CancellationToken.None);
            }
            catch (SqliteException ex)
            {
                logger.LogWarning(ex, "Sync could not empty its list of download cascades; the next upload does.");
            }
        }
        logger.LogInformation(
            "Sync kept {Count} rows that downloaded deletes removed or cleared out of the next upload.",
            reached.Sum(entry => entry.Value.Count));
        return anchor;
    }

    /// <summary>Empties the list (an app stopped during a download leaves it); call before an upload.</summary>
    public static async Task ClearAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{PlanTable}'
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0)
            return;
        command.CommandText = $"DELETE FROM {PlanTable}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The rows, by table, that the download's deletes remove or clear through foreign keys.</summary>
    public static async Task<IReadOnlyDictionary<string, HashSet<string>>> ReachAsync(
        string connectionString, SyncChangeSet download, CancellationToken cancellationToken = default)
    {
        var deleted = download.Items
            .Where(item => item.ChangeType == ChangeType.Delete && item.Values is not null
                && item.Values.TryGetValue("Id", out var id) && id?.Value is not null)
            .GroupBy(item => item.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.Values["Id"].Value!.ToString()!).ToHashSet(StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);
        var reached = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (deleted.Count == 0)
            return reached;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var schema = Schemas.TryGetValue(connectionString, out var known) ? known
            : Schemas[connectionString] = await ReadSchemaAsync(connection, cancellationToken);

        // A row a cascade removes takes its own children with it; a row SET NULL clears stays.
        // Track removals apart from all reached rows, so a row reached both ways is still followed.
        var removed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Table, HashSet<string> Ids)>(
            deleted.Select(entry => (entry.Key, entry.Value)));
        while (pending.Count > 0)
        {
            var (parent, ids) = pending.Dequeue();
            if (!schema.ByParent.TryGetValue(parent, out var children)
                || schema.PrimaryKeys.GetValueOrDefault(parent) is not { } parentKey)
                continue;
            foreach (var key in children)
            {
                if (schema.PrimaryKeys.GetValueOrDefault(key.Child) is not { } childKey)
                    continue;
                var referenced = string.Equals(key.ParentColumn, parentKey, StringComparison.OrdinalIgnoreCase)
                    ? ids
                    : await ValuesAsync(connection, parent, key.ParentColumn, parentKey, ids, cancellationToken);
                var rows = await ValuesAsync(connection, key.Child, childKey, key.ChildColumn, referenced, cancellationToken);
                if (rows.Count == 0)
                    continue;
                if (!reached.TryGetValue(key.Child, out var all))
                    reached[key.Child] = all = new HashSet<string>(StringComparer.Ordinal);
                all.UnionWith(rows);
                if (!key.Cascade)
                    continue;
                if (!removed.TryGetValue(key.Child, out var gone))
                    removed[key.Child] = gone = new HashSet<string>(StringComparer.Ordinal);
                var newlyRemoved = rows.Where(gone.Add).ToHashSet(StringComparer.Ordinal);
                if (newlyRemoved.Count > 0)
                    pending.Enqueue((key.Child, newlyRemoved));
            }
        }
        return reached;
    }

    /// <summary>Writes the list and makes sure the change log's trigger reads it.</summary>
    private static async Task ListAsync(string connectionString,
        IReadOnlyDictionary<string, HashSet<string>> reached, Guid source, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {PlanTable} (
                    TBL TEXT NOT NULL COLLATE NOCASE, PK TEXT NOT NULL COLLATE NOCASE, SRC TEXT NOT NULL,
                    PRIMARY KEY (TBL, PK)) WITHOUT ROWID;
                CREATE TRIGGER IF NOT EXISTS __ss_download_cascade_mark__
                AFTER INSERT ON __CORE_SYNC_CT
                WHEN NEW.SRC IS NULL AND EXISTS (SELECT 1 FROM {PlanTable}
                    WHERE TBL = NEW.TBL AND PK = COALESCE(NEW.PK_Text, CAST(NEW.PK_Int AS TEXT)))
                BEGIN
                    UPDATE __CORE_SYNC_CT SET SRC = (SELECT SRC FROM {PlanTable}
                        WHERE TBL = NEW.TBL AND PK = COALESCE(NEW.PK_Text, CAST(NEW.PK_Int AS TEXT)))
                    WHERE ID = NEW.ID;
                END;
                DELETE FROM {PlanTable};
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT OR IGNORE INTO {PlanTable} (TBL, PK, SRC) VALUES ($table, $pk, $source)";
            var table = insert.Parameters.Add("$table", SqliteType.Text);
            var pk = insert.Parameters.Add("$pk", SqliteType.Text);
            insert.Parameters.AddWithValue("$source", source.ToString());
            foreach (var (name, ids) in reached)
                foreach (var id in ids)
                {
                    table.Value = name;
                    pk.Value = id;
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Each table's single-column foreign keys that a parent's delete changes, and every table's primary key.</summary>
    private static async Task<Schema> ReadSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'"
                + " AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__CORE_SYNC%' AND name NOT LIKE '__SS_%'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tables.Add(reader.GetString(0));
        }
        var primaryKeys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var byParent = new Dictionary<string, List<ForeignKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var keyColumns = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT name FROM pragma_table_info('{table.Replace("'", "''")}') WHERE pk > 0";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    keyColumns.Add(reader.GetString(0));
            }
            primaryKeys[table] = keyColumns.Count == 1 ? keyColumns[0] : null;

            var columns = new List<(int Id, string Parent, string From, string? To, string OnDelete)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT id, "table", "from", "to", on_delete FROM pragma_foreign_key_list('{table.Replace("'", "''")}')
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    columns.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
            }
            foreach (var key in columns.GroupBy(column => column.Id)
                         .Where(group => group.Count() == 1).Select(group => group.Single()))
            {
                var cascade = string.Equals(key.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase);
                if (!cascade && !key.OnDelete.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!byParent.TryGetValue(key.Parent, out var list))
                    byParent[key.Parent] = list = [];
                list.Add(new ForeignKey(table, key.From, key.To ?? string.Empty, cascade));
            }
        }
        // A key that names no parent column references the parent's primary key.
        foreach (var (parent, list) in byParent)
            for (var index = 0; index < list.Count; index++)
                if (list[index].ParentColumn.Length == 0 && primaryKeys.GetValueOrDefault(parent) is { } parentKey)
                    list[index] = list[index] with { ParentColumn = parentKey };
        return new Schema(byParent, primaryKeys);
    }

    /// <summary>The <paramref name="select"/> values of the rows whose <paramref name="match"/> is one of <paramref name="values"/>.</summary>
    private static async Task<HashSet<string>> ValuesAsync(SqliteConnection connection, string table,
        string select, string match, IReadOnlyCollection<string> values, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in values.Chunk(ChunkSize))
        {
            await using var command = connection.CreateCommand();
            var names = chunk.Select((_, index) => $"$v{index}").ToArray();
            command.CommandText = $"SELECT \"{select}\" FROM \"{table}\" WHERE \"{match}\" IN ({string.Join(", ", names)})"
                + $" AND \"{select}\" IS NOT NULL";
            for (var index = 0; index < chunk.Length; index++)
                command.Parameters.AddWithValue(names[index], chunk[index]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                found.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }
        return found;
    }
}
