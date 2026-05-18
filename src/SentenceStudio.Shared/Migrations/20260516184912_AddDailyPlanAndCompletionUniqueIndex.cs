using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <summary>
    /// Phase A of the daily-plan server-contract foundation:
    ///   1. Creates the parent <c>DailyPlan</c> table.
    ///   2. Defensive-dedupes <c>DailyPlanCompletion</c> so the new unique
    ///      index can be created without violating an existing duplicate
    ///      (deterministic plan-item ids should prevent dupes, but we
    ///      cannot trust historical user data, especially on devices).
    ///   3. Adds a unique index on
    ///      <c>(UserProfileId, Date, PlanItemId)</c> so HTTP and CoreSync
    ///      writers can no longer create duplicate completion rows for the
    ///      same logical item.
    ///
    /// Phase B (separate follow-up migration on this same branch) drops the
    /// legacy <c>Rationale</c> + <c>NarrativeJson</c> columns once
    /// ProgressService + Blazor consume the parent <c>DailyPlan</c> instead.
    /// </summary>
    public partial class AddDailyPlanAndCompletionUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defensive dedupe: keep the most recently-updated row in each
            // (UserProfileId, Date, PlanItemId) group, preferring completed
            // rows. Window functions are supported in PostgreSQL.
            migrationBuilder.Sql(@"
WITH ranked AS (
    SELECT ""Id"", ROW_NUMBER() OVER (
        PARTITION BY ""UserProfileId"", ""Date"", ""PlanItemId""
        ORDER BY ""IsCompleted"" DESC,
                 ""UpdatedAt"" DESC,
                 ""CreatedAt"" DESC,
                 ""Id""
    ) AS rn
    FROM ""DailyPlanCompletion""
)
DELETE FROM ""DailyPlanCompletion""
WHERE ""Id"" IN (SELECT ""Id"" FROM ranked WHERE rn > 1);");

            migrationBuilder.CreateIndex(
                name: "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId",
                table: "DailyPlanCompletion",
                columns: new[] { "UserProfileId", "Date", "PlanItemId" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "DailyPlan",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserProfileId = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Strategy = table.Column<string>(type: "text", nullable: false),
                    RationaleFacts = table.Column<string>(type: "text", nullable: true),
                    NarrativeFacts = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
