using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularyEncodingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VocabularyWord_Lemma",
                table: "VocabularyWord");

            migrationBuilder.DropIndex(
                name: "IX_VocabularyWord_Tags",
                table: "VocabularyWord");

            migrationBuilder.DropIndex(
                name: "IX_ExampleSentence_IsCore",
                table: "ExampleSentence");

            migrationBuilder.DropIndex(
                name: "IX_ExampleSentence_VocabularyWordId_IsCore",
                table: "ExampleSentence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWord_Lemma",
                table: "VocabularyWord",
                column: "Lemma");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWord_Tags",
                table: "VocabularyWord",
                column: "Tags");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_IsCore",
                table: "ExampleSentence",
                column: "IsCore");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_VocabularyWordId_IsCore",
                table: "ExampleSentence",
                columns: new[] { "VocabularyWordId", "IsCore" });
        }
    }
}
