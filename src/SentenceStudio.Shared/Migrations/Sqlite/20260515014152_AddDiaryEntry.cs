using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddDiaryEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiaryEntry",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    EntryDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    PromptText = table.Column<string>(type: "TEXT", nullable: true),
                    PromptHint = table.Column<string>(type: "TEXT", nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    WordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WordGoal = table.Column<int>(type: "INTEGER", nullable: false),
                    FeedbackRecommended = table.Column<string>(type: "TEXT", nullable: true),
                    FeedbackNotes = table.Column<string>(type: "TEXT", nullable: true),
                    FeedbackStrengths = table.Column<string>(type: "TEXT", nullable: true),
                    FeedbackAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiaryEntry", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiaryEntry_UserProfileId_EntryDate_Language",
                table: "DiaryEntry",
                columns: new[] { "UserProfileId", "EntryDate", "Language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiaryEntry");
        }
    }
}
