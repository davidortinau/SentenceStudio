using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite;

/// <summary>
/// Hand-written SQLite twin of the PostgreSQL migration with the same id: the lookup history
/// table (Captain 2026-11-04, decision 4 B). A new, empty table; no existing row is touched.
/// The inline attributes are mandatory: without [Migration], EF never applies it on devices.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261104153858_AddVocabularyLookup")]
public partial class AddVocabularyLookup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VocabularyLookup",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserProfileId = table.Column<string>(type: "TEXT", nullable: false),
                Text = table.Column<string>(type: "TEXT", nullable: false),
                Translation = table.Column<string>(type: "TEXT", nullable: false),
                Language = table.Column<string>(type: "TEXT", nullable: false),
                Direction = table.Column<string>(type: "TEXT", nullable: false),
                Activity = table.Column<string>(type: "TEXT", nullable: false),
                ResourceId = table.Column<string>(type: "TEXT", nullable: true),
                FirstLookedUpAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastLookedUpAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VocabularyLookup", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VocabularyLookup_UserProfileId_LastLookedUpAt",
            table: "VocabularyLookup",
            columns: new[] { "UserProfileId", "LastLookedUpAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "VocabularyLookup");
    }
}
