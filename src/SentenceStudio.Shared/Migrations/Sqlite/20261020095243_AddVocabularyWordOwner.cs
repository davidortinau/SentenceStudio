using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite;

// Hand-written SQLite twin of the PostgreSQL migration with the same id. The inline
// attributes are mandatory: without [Migration], EF never applies it on devices.
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261020095243_AddVocabularyWordOwner")]
public partial class AddVocabularyWordOwner : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "UserProfileId",
            table: "VocabularyWord",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_VocabularyWord_UserProfileId",
            table: "VocabularyWord",
            column: "UserProfileId");
    }

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
