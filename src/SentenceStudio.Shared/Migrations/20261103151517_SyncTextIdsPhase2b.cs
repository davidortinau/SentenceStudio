using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class SyncTextIdsPhase2b : Migration
    {
        private const string ReceiptTypes =
            "('VocabularyReview.NativeAttemptReceipt.v1', 'VocabularyReview.NativeSentenceReceipt.v1')";

        /// <summary>
        /// Phase 2b of syncing every learner table (Captain 2026-11-03, docs/sync-all-tables-plan.md):
        /// activity resume state gets text IDs. Per learner + activity + launch, the in-progress
        /// row (else the most recently updated) gets the shared run ID
        /// (SyncIds.ActivitySession, keep the formats identical); other runs keep their history
        /// under new GUIDs. Native answer receipts get "receipt:" IDs and their link to their quiz
        /// row is re-pointed. Saved web/MAUI quizzes (a bare snapshot) are wrapped in the one saved
        /// format (docs/activity-session-format.md). The launch index is rebuilt as the model
        /// defines it (a database created earlier can lack it); an older in-progress duplicate is
        /// first marked abandoned, as the app does. No row is deleted.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                CREATE FUNCTION pg_temp.ss_try_jsonb(value text) RETURNS jsonb AS $fn$
                BEGIN
                    RETURN value::jsonb;
                EXCEPTION WHEN others THEN
                    RETURN NULL;
                END;
                $fn$ LANGUAGE plpgsql;

                UPDATE "ActivitySession" s SET "Status" = 'Abandoned', "UpdatedAt" = now()
                WHERE s."Status" = 'InProgress' AND EXISTS (
                    SELECT 1 FROM "ActivitySession" n
                    WHERE n."Status" = 'InProgress' AND n."UserId" = s."UserId"
                        AND n."ActivityType" = s."ActivityType" AND n."LaunchContextKey" = s."LaunchContextKey"
                        AND (n."UpdatedAt", n."StartedAt", n."Id") > (s."UpdatedAt", s."StartedAt", s."Id"));

                ALTER TABLE "ActivitySession" ADD COLUMN "SyncId" text;
                UPDATE "ActivitySession" s SET "SyncId" = CASE
                    WHEN s."ActivityType" IN {{ReceiptTypes}}
                        THEN 'receipt:' || replace(gen_random_uuid()::text, '-', '')
                    WHEN s."Id" = (
                        SELECT r."Id" FROM "ActivitySession" r
                        WHERE r."UserId" = s."UserId" AND r."ActivityType" = s."ActivityType"
                            AND r."LaunchContextKey" = s."LaunchContextKey"
                        ORDER BY (r."Status" = 'InProgress') DESC, r."UpdatedAt" DESC, r."StartedAt" DESC, r."Id" DESC
                        LIMIT 1)
                        THEN 'as:' || s."UserId" || ':' || s."ActivityType" || ':' || s."LaunchContextKey"
                    ELSE gen_random_uuid()::text
                END;

                -- A receipt whose quiz row is gone keeps its old number, as text.
                UPDATE "ActivitySession" r SET "StateJson" = jsonb_set(
                        pg_temp.ss_try_jsonb(r."StateJson"), '{sourceSessionId}',
                        to_jsonb(COALESCE(
                            (SELECT src."SyncId" FROM "ActivitySession" src
                             WHERE src."Id"::text = pg_temp.ss_try_jsonb(r."StateJson") ->> 'sourceSessionId'),
                            pg_temp.ss_try_jsonb(r."StateJson") ->> 'sourceSessionId')))::text
                WHERE r."ActivityType" IN {{ReceiptTypes}}
                    AND jsonb_typeof(pg_temp.ss_try_jsonb(r."StateJson") -> 'sourceSessionId') = 'number';

                -- planDate is the learner's local day of the last save (UTC when their time zone
                -- is unknown or not a PostgreSQL zone name).
                UPDATE "ActivitySession" s SET "StateJson" = jsonb_build_object(
                        'version', 1,
                        'userId', s."UserId",
                        'planDate', to_char(s."UpdatedAt" AT TIME ZONE COALESCE(
                            (SELECT z.name FROM "UserProfile" p
                             JOIN pg_timezone_names z ON z.name = p."IanaTimeZoneId"
                             WHERE p."Id" = s."UserId" LIMIT 1), 'UTC'), 'YYYY-MM-DD'),
                        'launchContextKey', s."LaunchContextKey",
                        'turnId', replace(gen_random_uuid()::text, '-', ''),
                        'displayDirection', '',
                        'snapshot', pg_temp.ss_try_jsonb(s."StateJson"))::text
                WHERE s."ActivityType" = 'VocabularyReview'
                    AND jsonb_typeof(pg_temp.ss_try_jsonb(s."StateJson")) = 'object'
                    AND pg_temp.ss_try_jsonb(s."StateJson") -> 'snapshot' IS NULL;

                ALTER TABLE "ActivitySession" ALTER COLUMN "Id" DROP IDENTITY IF EXISTS;
                ALTER TABLE "ActivitySession" ALTER COLUMN "Id" DROP DEFAULT;
                ALTER TABLE "ActivitySession" ALTER COLUMN "Id" TYPE text USING "SyncId";
                ALTER TABLE "ActivitySession" DROP COLUMN "SyncId";

                DROP INDEX IF EXISTS "IX_ActivitySession_UserId_LaunchContextKey";
                CREATE UNIQUE INDEX "IX_ActivitySession_UserId_LaunchContextKey"
                    ON "ActivitySession" ("UserId", "ActivityType", "LaunchContextKey")
                    WHERE "Status" = 'InProgress';

                DROP FUNCTION pg_temp.ss_try_jsonb(text);
                """);
        }

        /// <summary>
        /// Restores number IDs (renumbered 1..n in start order), re-points receipts, and unwraps
        /// saved states no native app saved (no "savedBy" and no plan row, which a native app
        /// always records) back to the bare web snapshot. Text IDs
        /// cannot become their original numbers, which were never kept. No row is deleted.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                CREATE FUNCTION pg_temp.ss_try_jsonb(value text) RETURNS jsonb AS $fn$
                BEGIN
                    RETURN value::jsonb;
                EXCEPTION WHEN others THEN
                    RETURN NULL;
                END;
                $fn$ LANGUAGE plpgsql;

                ALTER TABLE "ActivitySession" ADD COLUMN "OldId" integer;
                UPDATE "ActivitySession" s SET "OldId" = n.rn FROM (
                    SELECT "Id", row_number() OVER (ORDER BY "StartedAt", "Id") AS rn FROM "ActivitySession") n
                    WHERE s."Id" = n."Id";

                UPDATE "ActivitySession" r SET "StateJson" = jsonb_set(
                        pg_temp.ss_try_jsonb(r."StateJson"), '{sourceSessionId}', to_jsonb(src."OldId"))::text
                FROM "ActivitySession" src
                WHERE r."ActivityType" IN {{ReceiptTypes}}
                    AND src."Id" = pg_temp.ss_try_jsonb(r."StateJson") ->> 'sourceSessionId';

                UPDATE "ActivitySession" SET "StateJson" = (pg_temp.ss_try_jsonb("StateJson") -> 'snapshot')::text
                WHERE "ActivityType" = 'VocabularyReview'
                    AND jsonb_typeof(pg_temp.ss_try_jsonb("StateJson") -> 'snapshot') = 'object'
                    AND COALESCE(pg_temp.ss_try_jsonb("StateJson") ->> 'savedBy', '') = ''
                    AND pg_temp.ss_try_jsonb("StateJson") ->> 'planItemRowId' IS NULL;

                ALTER TABLE "ActivitySession" ALTER COLUMN "Id" TYPE integer USING "OldId";
                ALTER TABLE "ActivitySession" DROP COLUMN "OldId";
                ALTER TABLE "ActivitySession" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"ActivitySession"', 'Id'), COALESCE(MAX("Id"), 0) + 1, false) FROM "ActivitySession";

                DROP FUNCTION pg_temp.ss_try_jsonb(text);
                """);
        }
    }
}
