using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
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
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserProfileId = table.Column<string>(type: "text", nullable: false),
                    EntryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: true),
                    PromptText = table.Column<string>(type: "text", nullable: true),
                    PromptHint = table.Column<string>(type: "text", nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    WordCount = table.Column<int>(type: "integer", nullable: false),
                    WordGoal = table.Column<int>(type: "integer", nullable: false),
                    FeedbackRecommended = table.Column<string>(type: "text", nullable: true),
                    FeedbackNotes = table.Column<string>(type: "text", nullable: true),
                    FeedbackStrengths = table.Column<string>(type: "text", nullable: true),
                    FeedbackAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
