using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260927100455_AddStreamHistoryOwnerScope")]
public partial class AddStreamHistoryOwnerScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "UserProfileId",
            table: "StreamHistory",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_StreamHistory_UserProfileId_CreatedAt",
            table: "StreamHistory",
            columns: new[] { "UserProfileId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_StreamHistory_UserProfileId_CreatedAt",
            table: "StreamHistory");

        migrationBuilder.DropColumn(
            name: "UserProfileId",
            table: "StreamHistory");
    }
}
