using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <summary>
    /// Phase 2b of syncing every learner table (Captain 2026-11-03, docs/sync-all-tables-plan.md).
    /// SQLite counterpart of the PostgreSQL migration of the same id: activity resume state gets
    /// text IDs. Per learner + activity + launch, the in-progress row (else the most recently
    /// updated) gets the shared run ID (SyncIds.ActivitySession, keep the formats identical);
    /// other runs keep their history under new GUIDs. Native answer receipts get "receipt:" IDs
    /// and their link to their quiz row is re-pointed. Saved MAUI quizzes (a bare snapshot) are
    /// wrapped in the one saved format (docs/activity-session-format.md); their plan date is the
    /// UTC day of their last save (SQLite has no time zones). An older in-progress duplicate (a
    /// database created without the launch index) is first marked abandoned. SQLite cannot change a
    /// column type, so the table is rebuilt: new table, copy every row, drop the old one, rename.
    /// No row is lost.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261103151517_SyncTextIdsPhase2b")]
    public partial class SyncTextIdsPhase2b : Migration
    {
        private const string NewGuid =
            "lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || "
            + "substr(lower(hex(randomblob(2))), 2) || '-' || substr('89ab', 1 + (abs(random()) % 4), 1) || "
            + "substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6)))";

        private const string ReceiptTypes =
            "('VocabularyReview.NativeAttemptReceipt.v1', 'VocabularyReview.NativeSentenceReceipt.v1')";

        private const string CreateTable = """
            CREATE TABLE "ActivitySession_new" (
                "Id" {0} NOT NULL CONSTRAINT "PK_ActivitySession" PRIMARY KEY{1},
                "UserId" TEXT NOT NULL,
                "ActivityType" TEXT NOT NULL,
                "LaunchContextKey" TEXT NOT NULL,
                "StateJson" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL
            );
            """;

        private const string ReplaceTable = """
            DROP TABLE "ActivitySession";
            ALTER TABLE "ActivitySession_new" RENAME TO "ActivitySession";
            CREATE INDEX "IX_ActivitySession_UserId_ActivityType_Status" ON "ActivitySession" ("UserId", "ActivityType", "Status");
            CREATE UNIQUE INDEX "IX_ActivitySession_UserId_LaunchContextKey" ON "ActivitySession" ("UserId", "ActivityType", "LaunchContextKey") WHERE "Status" = 'InProgress';
            DROP TABLE "MapActivitySession";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                -- A database created without the launch index can hold in-progress duplicates;
                -- keep the newest, mark the rest abandoned (as the app does). Never deleted.
                UPDATE "ActivitySession" AS s SET "Status" = 'Abandoned',
                    "UpdatedAt" = strftime('%Y-%m-%d %H:%M:%f', 'now')
                WHERE s."Status" = 'InProgress' AND EXISTS (
                    SELECT 1 FROM "ActivitySession" n
                    WHERE n."Status" = 'InProgress' AND n."UserId" = s."UserId"
                        AND n."ActivityType" = s."ActivityType" AND n."LaunchContextKey" = s."LaunchContextKey"
                        AND (n."UpdatedAt", n."StartedAt", n."Id") > (s."UpdatedAt", s."StartedAt", s."Id"));

                CREATE TEMP TABLE "MapActivitySession" AS
                    SELECT s."Id" AS "OldId",
                        CASE
                            WHEN s."ActivityType" IN {{ReceiptTypes}}
                                THEN 'receipt:' || lower(hex(randomblob(16)))
                            WHEN s."Id" = (
                                SELECT r."Id" FROM "ActivitySession" r
                                WHERE r."UserId" = s."UserId" AND r."ActivityType" = s."ActivityType"
                                    AND r."LaunchContextKey" = s."LaunchContextKey"
                                ORDER BY (r."Status" = 'InProgress') DESC, r."UpdatedAt" DESC, r."StartedAt" DESC, r."Id" DESC
                                LIMIT 1)
                                THEN 'as:' || s."UserId" || ':' || s."ActivityType" || ':' || s."LaunchContextKey"
                            ELSE {{NewGuid}}
                        END AS "NewId"
                    FROM "ActivitySession" s;

                {{string.Format(CreateTable, "TEXT", "")}}
                INSERT INTO "ActivitySession_new" ("Id", "UserId", "ActivityType", "LaunchContextKey", "StateJson", "Status", "StartedAt", "UpdatedAt", "CompletedAt")
                    SELECT m."NewId", s."UserId", s."ActivityType", s."LaunchContextKey",
                        CASE
                            -- A receipt whose quiz row is gone keeps its old number, as text.
                            WHEN s."ActivityType" IN {{ReceiptTypes}} AND json_valid(s."StateJson")
                                AND json_type(s."StateJson", '$.sourceSessionId') = 'integer'
                                THEN json_set(s."StateJson", '$.sourceSessionId', COALESCE(
                                    (SELECT src."NewId" FROM "MapActivitySession" src
                                     WHERE src."OldId" = json_extract(s."StateJson", '$.sourceSessionId')),
                                    CAST(json_extract(s."StateJson", '$.sourceSessionId') AS TEXT)))
                            WHEN s."ActivityType" = 'VocabularyReview' AND json_valid(s."StateJson")
                                AND json_type(s."StateJson") = 'object'
                                AND json_type(s."StateJson", '$.snapshot') IS NULL
                                THEN json_object(
                                    'version', 1,
                                    'userId', s."UserId",
                                    'planDate', date(s."UpdatedAt"),
                                    'launchContextKey', s."LaunchContextKey",
                                    'turnId', lower(hex(randomblob(16))),
                                    'displayDirection', '',
                                    'snapshot', json(s."StateJson"))
                            ELSE s."StateJson"
                        END,
                        s."Status", s."StartedAt", s."UpdatedAt", s."CompletedAt"
                    FROM "ActivitySession" s JOIN "MapActivitySession" m ON m."OldId" = s."Id";
                {{ReplaceTable}}
                """);
        }

        /// <summary>
        /// Restores number IDs (renumbered 1..n in start order), re-points receipts, and unwraps
        /// saved states no native app saved (no "savedBy" and no plan row, which a native app
        /// always records) back to the bare snapshot. Text IDs
        /// cannot become their original numbers, which were never kept. No row is lost.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                CREATE TEMP TABLE "MapActivitySession" AS
                    SELECT "Id" AS "OldId", row_number() OVER (ORDER BY "StartedAt", "Id") AS "NewId"
                    FROM "ActivitySession";

                {{string.Format(CreateTable, "INTEGER", " AUTOINCREMENT")}}
                INSERT INTO "ActivitySession_new" ("Id", "UserId", "ActivityType", "LaunchContextKey", "StateJson", "Status", "StartedAt", "UpdatedAt", "CompletedAt")
                    SELECT m."NewId", s."UserId", s."ActivityType", s."LaunchContextKey",
                        CASE
                            WHEN s."ActivityType" IN {{ReceiptTypes}} AND json_valid(s."StateJson")
                                AND (SELECT src."NewId" FROM "MapActivitySession" src
                                     WHERE src."OldId" = json_extract(s."StateJson", '$.sourceSessionId')) IS NOT NULL
                                THEN json_set(s."StateJson", '$.sourceSessionId',
                                    (SELECT src."NewId" FROM "MapActivitySession" src
                                     WHERE src."OldId" = json_extract(s."StateJson", '$.sourceSessionId')))
                            WHEN s."ActivityType" = 'VocabularyReview' AND json_valid(s."StateJson")
                                AND json_type(s."StateJson", '$.snapshot') = 'object'
                                AND COALESCE(json_extract(s."StateJson", '$.savedBy'), '') = ''
                                AND json_extract(s."StateJson", '$.planItemRowId') IS NULL
                                THEN json_extract(s."StateJson", '$.snapshot')
                            ELSE s."StateJson"
                        END,
                        s."Status", s."StartedAt", s."UpdatedAt", s."CompletedAt"
                    FROM "ActivitySession" s JOIN "MapActivitySession" m ON m."OldId" = s."Id";
                {{ReplaceTable}}
                """);
        }
    }
}
