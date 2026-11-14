using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite;

/// <summary>
/// Hand-written SQLite twin of the PostgreSQL migration with the same id: the duplicate decision
/// table (docs/duplicate-merge-ai-plan.md, Captain 2026-11-14). A new, empty table; no existing
/// row is touched. The inline attributes are mandatory: without [Migration], EF never applies it
/// on devices.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261114203658_AddVocabularyDuplicateDecision")]
public partial class AddVocabularyDuplicateDecision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VocabularyDuplicateDecision",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserProfileId = table.Column<string>(type: "TEXT", nullable: false),
                TermKey = table.Column<string>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                CopyIds = table.Column<string>(type: "TEXT", nullable: false),
                KeeperId = table.Column<string>(type: "TEXT", nullable: true),
                DecidedBy = table.Column<string>(type: "TEXT", nullable: false),
                Reason = table.Column<string>(type: "TEXT", nullable: true),
                Before = table.Column<string>(type: "TEXT", nullable: true),
                DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UndoneAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VocabularyDuplicateDecision", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VocabularyDuplicateDecision_UserProfileId_TermKey",
            table: "VocabularyDuplicateDecision",
            columns: new[] { "UserProfileId", "TermKey" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "VocabularyDuplicateDecision");
    }
}
