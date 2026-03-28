using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddMissingVocabularyWordLanguageColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This migration exists to advance the snapshot for the SQLite provider.
            //
            // The VocabularyWord.Language column was part of InitialSqlite, but
            // legacy databases that had InitialSqlite *seeded* (not actually run)
            // are missing it. SyncService.InitializeDatabaseAsync now patches
            // missing columns BEFORE MigrateAsync runs, so by the time this
            // migration executes the column already exists.
            //
            // On fresh installs InitialSqlite creates the column directly, so
            // this is also a safe no-op there.
            //
            // We intentionally leave Up() empty to avoid "duplicate column"
            // errors on databases that already have the column.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op — column removal is handled by reverting InitialSqlite
            // if a full rollback is needed.
        }
    }
}
