using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveDisplacedPlanItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "DailyPlanCompletion",
                type: "boolean",
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
                filter: "\"IsArchived\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "DailyPlanCompletion" WHERE "IsArchived") THEN
                        RAISE EXCEPTION 'Cannot roll back plan archiving while archived history exists';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId",
                table: "DailyPlanCompletion");

            migrationBuilder.CreateIndex(
                name: "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId",
                table: "DailyPlanCompletion",
                columns: new[] { "UserProfileId", "Date", "PlanItemId" },
                unique: true);

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "DailyPlanCompletion");
        }
    }
}
