using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularyEncodingAndExamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add new encoding fields to VocabularyWord table
            migrationBuilder.AddColumn<string>(
                name: "Lemma",
                table: "VocabularyWord",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "VocabularyWord",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MnemonicText",
                table: "VocabularyWord",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MnemonicImageUri",
                table: "VocabularyWord",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioPronunciationUri",
                table: "VocabularyWord",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            // Create indexes for filtering/searching
            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWord_Tags",
                table: "VocabularyWord",
                column: "Tags");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWord_Lemma",
                table: "VocabularyWord",
                column: "Lemma");

            // Create ExampleSentence table
            migrationBuilder.CreateTable(
                name: "ExampleSentence",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VocabularyWordId = table.Column<int>(type: "INTEGER", nullable: false),
                    LearningResourceId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetSentence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    NativeSentence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AudioUri = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsCore = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExampleSentence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExampleSentence_VocabularyWord_VocabularyWordId",
                        column: x => x.VocabularyWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExampleSentence_LearningResource_LearningResourceId",
                        column: x => x.LearningResourceId,
                        principalTable: "LearningResource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            // Create indexes for performance
            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_VocabularyWordId",
                table: "ExampleSentence",
                column: "VocabularyWordId");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_IsCore",
                table: "ExampleSentence",
                column: "IsCore");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_VocabId_IsCore",
                table: "ExampleSentence",
                columns: new[] { "VocabularyWordId", "IsCore" });

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_LearningResourceId",
                table: "ExampleSentence",
                column: "LearningResourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExampleSentence");

            migrationBuilder.DropIndex(
                name: "IX_VocabularyWord_Tags",
                table: "VocabularyWord");

            migrationBuilder.DropIndex(
                name: "IX_VocabularyWord_Lemma",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "Lemma",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "MnemonicText",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "MnemonicImageUri",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "AudioPronunciationUri",
                table: "VocabularyWord");
        }
    }
}
