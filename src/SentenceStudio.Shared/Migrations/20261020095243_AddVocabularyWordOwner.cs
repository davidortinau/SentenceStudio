using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularyWordOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserProfileId",
                table: "VocabularyWord",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWord_UserProfileId",
                table: "VocabularyWord",
                column: "UserProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VocabularyWord_UserProfileId",
                table: "VocabularyWord");

            migrationBuilder.DropColumn(
                name: "UserProfileId",
                table: "VocabularyWord");
        }
    }
}
