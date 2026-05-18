using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <summary>SQLite mirror of the PostgreSQL AddDailyPlanAndCompletionUniqueIndex migration.</summary>
    public partial class AddDailyPlanAndCompletionUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defensive dedupe — SQLite ≥3.25 supports window functions.
            migrationBuilder.Sql(@"
DELETE FROM ""DailyPlanCompletion""
WHERE ""Id"" IN (
    SELECT ""Id"" FROM (
        SELECT ""Id"", ROW_NUMBER() OVER (
            PARTITION BY ""UserProfileId"", ""Date"", ""PlanItemId""
            ORDER BY ""IsCompleted"" DESC,
                     ""UpdatedAt"" DESC,
                     ""CreatedAt"" DESC,
                     ""Id""
        ) AS rn
        FROM ""DailyPlanCompletion""
    ) WHERE rn > 1
);");

            migrationBuilder.CreateIndex(
                name: "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId",
                table: "DailyPlanCompletion",
                columns: new[] { "UserProfileId", "Date", "PlanItemId" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "DailyPlan",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", nullable: false),
                    RationaleFacts = table.Column<string>(type: "TEXT", nullable: true),
                    NarrativeFacts = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyPlan", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyPlan_UserProfileId_Date",
                table: "DailyPlan",
                columns: new[] { "UserProfileId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyPlan");

            migrationBuilder.DropIndex(
                name: "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId",
                table: "DailyPlanCompletion");
        }
    }
}
