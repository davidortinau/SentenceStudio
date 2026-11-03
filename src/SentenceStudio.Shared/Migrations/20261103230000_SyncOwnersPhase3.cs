using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class SyncOwnersPhase3 : Migration
    {
        /// <summary>
        /// Phase 3 of syncing every learner table (Captain 2026-11-02 and 2026-11-03,
        /// docs/sync-all-tables-plan.md): scene images, conversation scenarios and example
        /// sentences get text IDs and an owner, so they can sync.
        /// <list type="bullet">
        /// <item>Built-in scenarios get their fixed ID (SyncIds.BuiltInScenario: "scn:" and the
        /// English name in lower case with spaces as hyphens; keep the formats identical). A second
        /// copy of a built-in keeps a new GUID. Every other row gets a new GUID; old numbers are not
        /// reused, because every store numbered its own rows from 1.</item>
        /// <item>A conversation's scenario link follows its scenario and becomes a plain reference
        /// with no database constraint. A link to a scenario that no longer exists keeps its old
        /// number as text, which matches nothing.</item>
        /// <item>An example sentence takes its resource's owner, else its word's owner, when one
        /// is recorded. Scene images and custom scenarios have no record of who made them on the
        /// server, so they stay unowned: never synced, and hidden on the web (D3).</item>
        /// </list>
        /// No row is deleted.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Conversation" DROP CONSTRAINT IF EXISTS "FK_Conversation_ConversationScenario_ScenarioId";

                ALTER TABLE "ConversationScenario" ADD COLUMN "SyncId" text;
                UPDATE "ConversationScenario" s SET "SyncId" = m."NewId"
                FROM (
                    SELECT "Id",
                        CASE WHEN "IsPredefined" AND row_number() OVER (
                                PARTITION BY "IsPredefined", lower(replace(btrim("Name"), ' ', '-'))
                                ORDER BY "Id") = 1
                            THEN 'scn:' || lower(replace(btrim("Name"), ' ', '-'))
                            ELSE gen_random_uuid()::text
                        END AS "NewId"
                    FROM "ConversationScenario") m
                WHERE s."Id" = m."Id";

                ALTER TABLE "Conversation" ALTER COLUMN "ScenarioId" TYPE text USING "ScenarioId"::text;
                UPDATE "Conversation" c SET "ScenarioId" = s."SyncId"
                    FROM "ConversationScenario" s WHERE c."ScenarioId" = s."Id"::text;

                ALTER TABLE "ConversationScenario" ALTER COLUMN "Id" DROP IDENTITY IF EXISTS;
                ALTER TABLE "ConversationScenario" ALTER COLUMN "Id" TYPE text USING "SyncId";
                ALTER TABLE "ConversationScenario" DROP COLUMN "SyncId";
                ALTER TABLE "ConversationScenario" ADD COLUMN "UserProfileId" text;

                ALTER TABLE "SceneImage" ALTER COLUMN "Id" DROP IDENTITY IF EXISTS;
                ALTER TABLE "SceneImage" ALTER COLUMN "Id" TYPE text USING gen_random_uuid()::text;
                ALTER TABLE "SceneImage" ADD COLUMN "UserProfileId" text;

                ALTER TABLE "ExampleSentence" ALTER COLUMN "Id" DROP IDENTITY IF EXISTS;
                ALTER TABLE "ExampleSentence" ALTER COLUMN "Id" TYPE text USING gen_random_uuid()::text;
                ALTER TABLE "ExampleSentence" ADD COLUMN "UserProfileId" text;
                UPDATE "ExampleSentence" e SET "UserProfileId" = r."UserProfileId"
                    FROM "LearningResource" r
                    WHERE e."LearningResourceId" = r."Id" AND COALESCE(r."UserProfileId", '') <> '';
                UPDATE "ExampleSentence" e SET "UserProfileId" = w."UserProfileId"
                    FROM "VocabularyWord" w
                    WHERE e."UserProfileId" IS NULL AND e."VocabularyWordId" = w."Id"
                        AND COALESCE(w."UserProfileId", '') <> '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SceneImage_UserProfileId",
                table: "SceneImage",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationScenario_UserProfileId",
                table: "ConversationScenario",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_UserProfileId",
                table: "ExampleSentence",
                column: "UserProfileId");
        }

        /// <summary>
        /// Restores number IDs (renumbered 1..n in creation order), re-links conversations to
        /// their scenario and restores the constraint, and drops the owner columns. Text IDs cannot
        /// become their original numbers, which were never kept. A conversation linked to a
        /// scenario that no longer exists loses the link (the old constraint would refuse it).
        /// No row is deleted.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_SceneImage_UserProfileId", table: "SceneImage");
            migrationBuilder.DropIndex(name: "IX_ConversationScenario_UserProfileId", table: "ConversationScenario");
            migrationBuilder.DropIndex(name: "IX_ExampleSentence_UserProfileId", table: "ExampleSentence");

            migrationBuilder.Sql("""
                ALTER TABLE "ConversationScenario" ADD COLUMN "OldId" integer;
                UPDATE "ConversationScenario" s SET "OldId" = m."OldId"
                    FROM (SELECT "Id", row_number() OVER (ORDER BY "CreatedAt", "Id") AS "OldId"
                          FROM "ConversationScenario") m
                    WHERE s."Id" = m."Id";
                -- One pass, so a dangling old number can never match a renumbered scenario.
                UPDATE "Conversation" c SET "ScenarioId" = (
                    SELECT s."OldId"::text FROM "ConversationScenario" s WHERE s."Id" = c."ScenarioId")
                    WHERE c."ScenarioId" IS NOT NULL;
                ALTER TABLE "Conversation" ALTER COLUMN "ScenarioId" TYPE integer USING "ScenarioId"::integer;
                ALTER TABLE "ConversationScenario" DROP COLUMN "UserProfileId";
                ALTER TABLE "ConversationScenario" ALTER COLUMN "Id" TYPE integer USING "OldId";
                ALTER TABLE "ConversationScenario" DROP COLUMN "OldId";
                ALTER TABLE "ConversationScenario" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"ConversationScenario"', 'Id'),
                    COALESCE((SELECT MAX("Id") FROM "ConversationScenario"), 0) + 1, false);
                ALTER TABLE "Conversation" ADD CONSTRAINT "FK_Conversation_ConversationScenario_ScenarioId"
                    FOREIGN KEY ("ScenarioId") REFERENCES "ConversationScenario" ("Id") ON DELETE SET NULL;

                ALTER TABLE "SceneImage" DROP COLUMN "UserProfileId";
                ALTER TABLE "SceneImage" ADD COLUMN "OldId" integer;
                UPDATE "SceneImage" s SET "OldId" = m."OldId"
                    FROM (SELECT "Id", row_number() OVER (ORDER BY "Id") AS "OldId" FROM "SceneImage") m
                    WHERE s."Id" = m."Id";
                ALTER TABLE "SceneImage" ALTER COLUMN "Id" TYPE integer USING "OldId";
                ALTER TABLE "SceneImage" DROP COLUMN "OldId";
                ALTER TABLE "SceneImage" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"SceneImage"', 'Id'),
                    COALESCE((SELECT MAX("Id") FROM "SceneImage"), 0) + 1, false);

                ALTER TABLE "ExampleSentence" DROP COLUMN "UserProfileId";
                ALTER TABLE "ExampleSentence" ADD COLUMN "OldId" integer;
                UPDATE "ExampleSentence" s SET "OldId" = m."OldId"
                    FROM (SELECT "Id", row_number() OVER (ORDER BY "CreatedAt", "Id") AS "OldId"
                          FROM "ExampleSentence") m
                    WHERE s."Id" = m."Id";
                ALTER TABLE "ExampleSentence" ALTER COLUMN "Id" TYPE integer USING "OldId";
                ALTER TABLE "ExampleSentence" DROP COLUMN "OldId";
                ALTER TABLE "ExampleSentence" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"ExampleSentence"', 'Id'),
                    COALESCE((SELECT MAX("Id") FROM "ExampleSentence"), 0) + 1, false);
                """);
        }
    }
}
