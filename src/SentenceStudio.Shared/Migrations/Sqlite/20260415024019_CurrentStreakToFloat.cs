using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class CurrentStreakToFloat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite uses dynamic typing — INTEGER columns already accept REAL values.
            // This migration exists to advance the EF model snapshot so it records
            // CurrentStreak as float/REAL.  No data migration is needed because
            // existing integer values are implicitly valid floats.
            migrationBuilder.AlterColumn<float>(
                name: "CurrentStreak",
                table: "VocabularyProgress",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "CurrentStreak",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(float),
                oldType: "REAL");
        }
    }
}
