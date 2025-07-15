using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularyProgressTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VocabularyProgress",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VocabularyWordId = table.Column<int>(type: "INTEGER", nullable: false),
                    MultipleChoiceCorrect = table.Column<int>(type: "INTEGER", nullable: false),
                    TextEntryCorrect = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPromoted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPracticedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VocabularyProgress_VocabularyWord_VocabularyWordId",
                        column: x => x.VocabularyWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VocabularyLearningContext",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VocabularyProgressId = table.Column<int>(type: "INTEGER", nullable: false),
                    LearningResourceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Activity = table.Column<string>(type: "TEXT", nullable: false),
                    LearnedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CorrectAnswersInContext = table.Column<int>(type: "INTEGER", nullable: false),
                    InputMode = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyLearningContext", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VocabularyLearningContext_LearningResource_LearningResourceId",
                        column: x => x.LearningResourceId,
                        principalTable: "LearningResource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VocabularyLearningContext_VocabularyProgress_VocabularyProgressId",
                        column: x => x.VocabularyProgressId,
                        principalTable: "VocabularyProgress",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyLearningContext_LearningResourceId",
                table: "VocabularyLearningContext",
                column: "LearningResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyLearningContext_VocabularyProgressId",
                table: "VocabularyLearningContext",
                column: "VocabularyProgressId");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyProgress_VocabularyWordId",
                table: "VocabularyProgress",
                column: "VocabularyWordId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VocabularyLearningContext");

            migrationBuilder.DropTable(
                name: "VocabularyProgress");
        }
    }
}
