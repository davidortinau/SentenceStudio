using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularyDuplicateLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ResourceVocabularyMapping_ResourceId",
                table: "ResourceVocabularyMapping");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceVocabularyMapping_ResourceId_VocabularyWordId",
                table: "ResourceVocabularyMapping",
                columns: new[] { "ResourceId", "VocabularyWordId" });

            migrationBuilder.CreateIndex(
                name: "IX_LearningResource_UserProfileId",
                table: "LearningResource",
                column: "UserProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ResourceVocabularyMapping_ResourceId_VocabularyWordId",
                table: "ResourceVocabularyMapping");

            migrationBuilder.DropIndex(
                name: "IX_LearningResource_UserProfileId",
                table: "LearningResource");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceVocabularyMapping_ResourceId",
                table: "ResourceVocabularyMapping",
                column: "ResourceId");
        }
    }
}
