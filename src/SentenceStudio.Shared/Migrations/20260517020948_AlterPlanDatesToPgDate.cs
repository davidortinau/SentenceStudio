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
