using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260928050905_ArchiveDisplacedPlanItems")]
public partial class ArchiveDisplacedPlanItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsArchived",
            table: "DailyPlanCompletion",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.DropIndex(
            name: "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId",
            table: "DailyPlanCompletion");

        migrationBuilder.CreateIndex(
            name: "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId",
            table: "DailyPlanCompletion",
            columns: new[] { "UserProfileId", "Date", "PlanItemId" },
            unique: true,
            filter: "\"IsArchived\" = 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adding the predecessor's TargetModel would let EF generate a
        // DropColumn table rebuild, but dropping this CoreSync-synced table
        // also removes its change-tracking triggers. Fail closed even if that
        // target model is added later; a trigger-preserving rollback needs
        // separate validation on a provisioned scratch backup.
        throw new NotSupportedException(
            "Cannot roll back SQLite plan archiving safely: EF's table rebuild removes CoreSync change-tracking triggers.");
    }
}
