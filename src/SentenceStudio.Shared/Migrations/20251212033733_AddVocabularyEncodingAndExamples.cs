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
            migrationBuilder.AddColumn<string>(
                name: "AudioPronunciationUri",
                table: "VocabularyWord",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lemma",
                table: "VocabularyWord",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MnemonicImageUri",
                table: "VocabularyWord",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MnemonicText",
                table: "VocabularyWord",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "VocabularyWord",
                type: "TEXT",
                nullable: true);

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
                        name: "FK_ExampleSentence_LearningResource_LearningResourceId",
                        column: x => x.LearningResourceId,
                        principalTable: "LearningResource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExampleSentence_VocabularyWord_VocabularyWordId",
                        column: x => x.VocabularyWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_LearningResourceId",
                table: "ExampleSentence",
                column: "LearningResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_VocabularyWordId",
                table: "ExampleSentence",
                column: "VocabularyWordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExampleSentence");

            migrationBuilder.DropColumn(
                name: "AudioPronunciationUri",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "Lemma",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "MnemonicImageUri",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "MnemonicText",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "VocabularyWord");
        }
    }
}
