using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using Xunit;

namespace SentenceStudio.SqliteMigrations.Tests;

// Runs against the compiled mobile Shared model, so these are the real SQLite migrations.
public sealed class SqliteMigrationBackupTests : IDisposable
{
    private const string ArchiveMigration = "20260928050905_ArchiveDisplacedPlanItems";
    private const string WordOwnerMigration = "20261020095243_AddVocabularyWordOwner";

    private readonly DirectoryInfo _directory =
        Directory.CreateTempSubdirectory("SentenceStudio.MigrationBackup.");

    private string DatabasePath => Path.Combine(_directory.FullName, "app.sqlite");

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath, Pooling = false
    }.ToString();

    [Fact]
    public async Task ExistingDatabase_IsCopiedAtItsPreviousMigration_ThenMigrated()
    {
        await CreateAtArchiveMigrationWithAWordAsync();

        string? backup;
        await using (var db = CreateContext())
            backup = await SqliteMigrationBackup.MigrateWithBackupAsync(
                db, DatabasePath, NullLogger.Instance);

        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        // One self-contained file: no leftover partial or WAL sidecars.
        Assert.Empty(Directory.GetFiles(_directory.FullName, "*.partial*"));
        Assert.Equal("delete", Scalar(backup, "PRAGMA journal_mode"));
        Assert.Contains(SqliteMigrationBackup.Marker, Path.GetFileName(backup));
        Assert.EndsWith($"-{WordOwnerMigration}.sqlite", backup);
        // The copy is the untouched pre-migration database.
        Assert.Equal(ArchiveMigration, Scalar(backup, "SELECT max(\"MigrationId\") FROM \"__EFMigrationsHistory\""));
        Assert.Equal("1", Scalar(backup, "SELECT count(*) FROM \"VocabularyWord\""));
        Assert.Equal("0", Scalar(backup,
            "SELECT count(*) FROM pragma_table_info('VocabularyWord') WHERE name = 'UserProfileId'"));
        // The live database is migrated and kept its row.
        Assert.Equal(WordOwnerMigration, Scalar(DatabasePath, "SELECT max(\"MigrationId\") FROM \"__EFMigrationsHistory\""));
        Assert.Equal("legacy word", Scalar(DatabasePath, "SELECT \"NativeLanguageTerm\" FROM \"VocabularyWord\""));
        Assert.Equal("1", Scalar(DatabasePath,
            "SELECT count(*) FROM pragma_table_info('VocabularyWord') WHERE name = 'UserProfileId'"));

        // Nothing pending: no further copy.
        await using (var db = CreateContext())
            Assert.Null(await SqliteMigrationBackup.MigrateWithBackupAsync(
                db, DatabasePath, NullLogger.Instance));
        Assert.Single(Backups());
    }

    [Fact]
    public async Task FreshInstall_MigratesWithoutACopy()
    {
        await using (var db = CreateContext())
            Assert.Null(await SqliteMigrationBackup.MigrateWithBackupAsync(
                db, DatabasePath, NullLogger.Instance));

        Assert.Empty(Backups());
        Assert.Equal(WordOwnerMigration, Scalar(DatabasePath, "SELECT max(\"MigrationId\") FROM \"__EFMigrationsHistory\""));
    }

    [Fact]
    public async Task WhenTheCopyCannotBeWritten_NothingIsMigrated()
    {
        await CreateAtArchiveMigrationWithAWordAsync();
        // The database stays healthy; only the backup location refuses writes.
        var readOnlyBackups = Directory.CreateDirectory(Path.Combine(_directory.FullName, "backups")).FullName;
        File.SetUnixFileMode(readOnlyBackups, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await using var db = CreateContext();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteMigrationBackup.MigrateWithBackupAsync(
                    db, DatabasePath, NullLogger.Instance, backupDirectory: readOnlyBackups));
        }
        finally
        {
            File.SetUnixFileMode(readOnlyBackups,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Empty(Directory.GetFiles(readOnlyBackups));
        Assert.Empty(Backups());
        Assert.Equal(ArchiveMigration, Scalar(DatabasePath, "SELECT max(\"MigrationId\") FROM \"__EFMigrationsHistory\""));
        await using var check = CreateContext();
        Assert.Contains(WordOwnerMigration, await check.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public void Prune_KeepsTheNewestThreeCopies()
    {
        File.WriteAllText(DatabasePath, string.Empty);
        foreach (var stamp in new[] { "20260101000000", "20260201000000", "20260301000000",
                     "20260401000000", "20260501000000" })
            File.WriteAllText($"{DatabasePath}{SqliteMigrationBackup.Marker}{stamp}-X.sqlite", stamp);
        File.WriteAllText(Path.Combine(_directory.FullName, "other.sqlite.premigration-20250101000000-X.sqlite"), "");

        SqliteMigrationBackup.PruneBackups(DatabasePath, 3);

        Assert.Equal(
            ["20260301000000", "20260401000000", "20260501000000"],
            Backups().Select(File.ReadAllText).Order());
        Assert.True(File.Exists(Path.Combine(_directory.FullName,
            "other.sqlite.premigration-20250101000000-X.sqlite")));
    }

    [Fact]
    public void Prune_NeverDeletesTheCopyJustMade_EvenWithAClockThatWentBackwards()
    {
        File.WriteAllText(DatabasePath, string.Empty);
        foreach (var stamp in new[] { "20260301000000", "20260401000000", "20260501000000" })
            File.WriteAllText($"{DatabasePath}{SqliteMigrationBackup.Marker}{stamp}-X.sqlite", stamp);
        // The clock went backwards: the copy just made sorts oldest.
        var justMade = $"{DatabasePath}{SqliteMigrationBackup.Marker}20250101000000-X.sqlite";
        File.WriteAllText(justMade, "just made");

        SqliteMigrationBackup.PruneBackups(DatabasePath, 2, keepPath: justMade);

        Assert.Equal(["20260501000000", "just made"], Backups().Select(File.ReadAllText).Order());
    }

    [Fact]
    public async Task InterruptedCopyFromAnEarlierLaunch_IsCleanedUp()
    {
        await CreateAtArchiveMigrationWithAWordAsync();
        var interrupted = $"{DatabasePath}{SqliteMigrationBackup.Marker}20261001000000-X.sqlite.partial";
        File.WriteAllText(interrupted, "killed mid-copy");
        File.WriteAllText(interrupted + "-wal", "");

        await using (var db = CreateContext())
            Assert.NotNull(await SqliteMigrationBackup.MigrateWithBackupAsync(
                db, DatabasePath, NullLogger.Instance));

        Assert.Empty(Directory.GetFiles(_directory.FullName, "*.partial*"));
        Assert.Single(Backups());
    }

    private async Task CreateAtArchiveMigrationWithAWordAsync()
    {
        await using (var db = CreateContext())
            await db.Database.MigrateAsync(ArchiveMigration);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Raw SQL: the current model maps a column the previous schema lacks.
        // Devices run in WAL mode (ApplicationDbContext.EnableWalMode), so test that too.
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            INSERT INTO VocabularyWord (Id, TargetLanguageTerm, NativeLanguageTerm, CreatedAt, UpdatedAt)
            VALUES ('legacy-word', '옛말', 'legacy word', '2026-09-02 10:00:00', '2026-09-02 10:00:00');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private ApplicationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(ConnectionString).Options);

    private string[] Backups() => Directory.GetFiles(_directory.FullName,
        Path.GetFileName(DatabasePath) + SqliteMigrationBackup.Marker + "*.sqlite");

    private static string? Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
