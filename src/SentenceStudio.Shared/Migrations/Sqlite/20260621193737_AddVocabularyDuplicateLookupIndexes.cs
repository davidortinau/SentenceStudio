using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260621193737_AddVocabularyDuplicateLookupIndexes")]
public partial class AddVocabularyDuplicateLookupIndexes : Migration
{
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
