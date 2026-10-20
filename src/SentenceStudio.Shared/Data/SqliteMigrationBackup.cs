using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

/// <summary>
/// Backup-then-migrate for on-device SQLite databases. Before an existing database's
/// schema changes, a WAL-consistent copy is written next to it and verified (integrity
/// check and identical row counts in every table). If the copy cannot be made and
/// verified, nothing is migrated and the caller's startup fails closed. A migration that
/// fails afterwards leaves the copy in place for recovery.
/// </summary>
public static class SqliteMigrationBackup
{
    /// <summary>File-name marker for pre-migration backups.</summary>
    public const string Marker = ".premigration-";

    /// <summary>Copies kept after a successful migration (this upgrade and the one before).</summary>
    public const int KeepBackups = 2;

    /// <summary>
    /// Writes a verified backup when <paramref name="databasePath"/> exists and EF has
    /// pending migrations. Returns the backup path, or null when no backup was needed.
    /// Backups go next to the database unless <paramref name="backupDirectory"/> is given.
    /// </summary>
    public static async Task<string?> BackupIfMigrationPendingAsync(
        ApplicationDbContext db, string databasePath, ILogger logger,
        CancellationToken cancellationToken = default, string? backupDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath)
            || new FileInfo(databasePath).Length == 0)
            return null;
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
            return null;

        var directory = backupDirectory ?? Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(directory);
        RemoveInterruptedCopies(databasePath, directory, logger);
        var backupPath = Path.Combine(directory,
            $"{Path.GetFileName(databasePath)}{Marker}{DateTime.UtcNow:yyyyMMddHHmmss}-{pending[^1]}.sqlite");
        try
        {
            WriteVerifiedBackup(databasePath, backupPath);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Pre-migration backup of {Database} failed; {Count} pending migrations were not applied.",
                Path.GetFileName(databasePath), pending.Count);
            throw new InvalidOperationException(
                "The database could not be backed up, so pending migrations were not applied.", ex);
        }
        logger.LogInformation(
            "Backed up {Database} to {Backup} before applying {Count} migrations (through {Target}).",
            Path.GetFileName(databasePath), Path.GetFileName(backupPath), pending.Count, pending[^1]);
        return backupPath;
    }

    /// <summary>Backs up when needed, then applies pending migrations.</summary>
    public static async Task<string?> MigrateWithBackupAsync(
        ApplicationDbContext db, string databasePath, ILogger logger,
        CancellationToken cancellationToken = default, string? backupDirectory = null)
    {
        var backup = await BackupIfMigrationPendingAsync(
            db, databasePath, logger, cancellationToken, backupDirectory);
        try
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception ex) when (backup is not null)
        {
            logger.LogCritical(ex,
                "Migration failed; the pre-migration copy is kept at {Backup}.", Path.GetFileName(backup));
            throw;
        }
        // Only after success, so failed launches never prune away the original copy.
        if (backup is not null)
            PruneBackups(databasePath, KeepBackups, logger, backupDirectory, keepPath: backup);
        return backup;
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> pre-migration copies, and never deletes
    /// <paramref name="keepPath"/> (the copy just made), whatever the device clock says.
    /// Call only after a successful migration.
    /// </summary>
    public static void PruneBackups(
        string databasePath, int keep, ILogger? logger = null, string? backupDirectory = null,
        string? keepPath = null)
    {
        var directory = backupDirectory ?? Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (directory is null || !Directory.Exists(directory))
            return;
        var stale = Directory.GetFiles(directory, Path.GetFileName(databasePath) + Marker + "*.sqlite")
            .Where(path => keepPath is null
                || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(keepPath), StringComparison.Ordinal))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Skip(Math.Max(0, keep - (keepPath is null ? 0 : 1)));
        foreach (var path in stale)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                // An old copy that cannot be removed must not block startup.
                logger?.LogWarning(ex, "Could not remove old pre-migration backup {Backup}.", Path.GetFileName(path));
            }
        }
    }

    // A launch killed mid-copy (e.g. by the iOS watchdog) leaves a .partial and its sidecars.
    private static void RemoveInterruptedCopies(string databasePath, string directory, ILogger logger)
    {
        foreach (var path in Directory.GetFiles(directory, Path.GetFileName(databasePath) + Marker + "*.partial*"))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not remove interrupted copy {Copy}.", Path.GetFileName(path));
            }
        }
    }

    private static void WriteVerifiedBackup(string databasePath, string backupPath)
    {
        var partial = backupPath + ".partial";
        File.Delete(partial);
        try
        {
            using (var source = Open(databasePath, SqliteOpenMode.ReadWrite))
            using (var copy = Open(partial, SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(copy);
                // The copy inherits WAL mode; make it one self-contained file.
                Scalar(copy, "PRAGMA journal_mode=DELETE");
            }

            using (var source = Open(databasePath, SqliteOpenMode.ReadWrite))
            using (var copy = Open(partial, SqliteOpenMode.ReadOnly))
            {
                var integrity = Scalar(copy, "PRAGMA integrity_check");
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Backup integrity check failed: {integrity}");
                var sourceCounts = RowCounts(source);
                var copyCounts = RowCounts(copy);
                if (!sourceCounts.SequenceEqual(copyCounts))
                    throw new InvalidDataException("Backup row counts differ from the database.");
            }
            File.Move(partial, backupPath);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                File.Delete(partial + suffix);
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private static List<(string Table, long Rows)> RowCounts(SqliteConnection connection)
    {
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }
        return tables.Select(table =>
        {
            using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\"";
            return (table, Convert.ToInt64(count.ExecuteScalar()));
        }).ToList();
    }
}
