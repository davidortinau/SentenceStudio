using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <summary>
    /// §14a — device-local turnover fix.
    ///
    /// The <c>Date</c> column on <c>DailyPlan</c> and <c>DailyPlanCompletion</c>
    /// is a user-local calendar day, not an instant. Under the legacy Npgsql
    /// timestamp behavior (<c>Npgsql.EnableLegacyTimestampBehavior=true</c>),
    /// <c>timestamp with time zone</c> values are converted to the .NET host's
    /// local time on read, which shifts the date by ±1 for users in negative-
    /// or positive-offset zones. Switching the column to PostgreSQL <c>date</c>
    /// (which has no time-zone semantics) defeats this entirely.
    ///
    /// PG cannot cast <c>timestamptz → date</c> implicitly, so an explicit
    /// <c>USING ... AT TIME ZONE 'UTC'::date</c> projection is required. Any
    /// historical rows were written through the legacy code path, which always
    /// stored UTC midnight for the user's local calendar day — so projecting
    /// the UTC date is the correct, lossless conversion.
    /// </summary>
    public partial class AlterPlanDatesToPgDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing unique indexes were created while Date was timestamptz.
            // Multiple rows can still exist for the same logical local day if
            // they differ by time-of-day, and those collisions surface only
            // after converting to a plain date. Drop indexes before conversion,
            // dedupe by projected UTC date, then recreate indexes.
            migrationBuilder.Sql(@"
DROP INDEX IF EXISTS ""IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId"";
DROP INDEX IF EXISTS ""IX_DailyPlan_UserProfileId_Date"";");

            migrationBuilder.Sql(@"
WITH ranked AS (
    SELECT ""Id"", ROW_NUMBER() OVER (
        PARTITION BY ""UserProfileId"", (""Date"" AT TIME ZONE 'UTC')::date, ""PlanItemId""
        ORDER BY ""IsCompleted"" DESC,
                 ""UpdatedAt"" DESC,
                 ""CreatedAt"" DESC,
                 ""Id""
    ) AS rn
    FROM ""DailyPlanCompletion""
)
DELETE FROM ""DailyPlanCompletion""
WHERE ""Id"" IN (SELECT ""Id"" FROM ranked WHERE rn > 1);");

            migrationBuilder.Sql(@"
WITH ranked AS (
    SELECT ""Id"", ROW_NUMBER() OVER (
        PARTITION BY ""UserProfileId"", (""Date"" AT TIME ZONE 'UTC')::date
        ORDER BY ""GeneratedAtUtc"" DESC,
                 ""UpdatedAt"" DESC,
                 ""CreatedAt"" DESC,
                 ""Id""
    ) AS rn
    FROM ""DailyPlan""
)
DELETE FROM ""DailyPlan""
WHERE ""Id"" IN (SELECT ""Id"" FROM ranked WHERE rn > 1);");

            // EF emits AlterColumn without a USING clause; PG rejects the
            // cast. Use raw ALTER COLUMN with the projection instead.
            migrationBuilder.Sql(@"
ALTER TABLE ""DailyPlanCompletion""
    ALTER COLUMN ""Date"" TYPE date
    USING (""Date"" AT TIME ZONE 'UTC')::date;");

            migrationBuilder.Sql(@"
ALTER TABLE ""DailyPlan""
    ALTER COLUMN ""Date"" TYPE date
    USING (""Date"" AT TIME ZONE 'UTC')::date;");

            migrationBuilder.CreateIndex(
                name: "IX_DailyPlanCompletion_UserProfileId_Date_PlanItemId",
                table: "DailyPlanCompletion",
                columns: new[] { "UserProfileId", "Date", "PlanItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyPlan_UserProfileId_Date",
                table: "DailyPlan",
                columns: new[] { "UserProfileId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse projection: assume midnight UTC for the previously-stored
            // local calendar day. Equivalent to the legacy write semantics.
            migrationBuilder.Sql(@"
ALTER TABLE ""DailyPlan""
    ALTER COLUMN ""Date"" TYPE timestamp with time zone
    USING (""Date""::timestamp AT TIME ZONE 'UTC');");

            migrationBuilder.Sql(@"
ALTER TABLE ""DailyPlanCompletion""
    ALTER COLUMN ""Date"" TYPE timestamp with time zone
    USING (""Date""::timestamp AT TIME ZONE 'UTC');");
        }
    }
}
