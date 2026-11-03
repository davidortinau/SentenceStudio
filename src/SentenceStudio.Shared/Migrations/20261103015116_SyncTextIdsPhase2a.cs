using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class SyncTextIdsPhase2a : Migration
    {
        /// <summary>
        /// Phase 2a of syncing every learner table (Captain 2026-11-02, docs/sync-all-tables-plan.md):
        /// number IDs become text IDs that are unique on every store. Old numbers are NOT reused as
        /// text, because every device numbered its own rows from 1 and a device's "7" would collide
        /// with the server's "7". Minimal Pairs and conversation memories get IDs built from what
        /// they describe (SyncIds, keep the formats identical); every other row gets a new GUID.
        /// Answers follow their session and pair. No row is deleted.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "MinimalPairAttempt" DROP CONSTRAINT "FK_MinimalPairAttempt_MinimalPairSession_SessionId";
                ALTER TABLE "MinimalPairAttempt" DROP CONSTRAINT "FK_MinimalPairAttempt_MinimalPair_PairId";

                ALTER TABLE "MinimalPair" ADD COLUMN "SyncId" text;
                UPDATE "MinimalPair" SET "SyncId" = 'mp:' || "UserId" || ':' || "VocabularyWordAId" || ':' || "VocabularyWordBId";
                ALTER TABLE "MinimalPairSession" ADD COLUMN "SyncId" text;
                UPDATE "MinimalPairSession" SET "SyncId" = gen_random_uuid()::text;

                ALTER TABLE "MinimalPairAttempt" ALTER COLUMN "SessionId" TYPE text USING "SessionId"::text;
                ALTER TABLE "MinimalPairAttempt" ALTER COLUMN "PairId" TYPE text USING "PairId"::text;
                UPDATE "MinimalPairAttempt" a SET "SessionId" = s."SyncId"
                    FROM "MinimalPairSession" s WHERE a."SessionId" = s."Id"::text;
                UPDATE "MinimalPairAttempt" a SET "PairId" = p."SyncId"
                    FROM "MinimalPair" p WHERE a."PairId" = p."Id"::text;

                ALTER TABLE "MinimalPair" ALTER COLUMN "Id" DROP IDENTITY IF EXISTS;
                ALTER TABLE "MinimalPair" ALTER COLUMN "Id" TYPE text USING "SyncId";
                ALTER TABLE "MinimalPair" DROP COLUMN "SyncId";
                ALTER TABLE "MinimalPairSession" ALTER COLUMN "Id" DROP IDENTITY IF EXISTS;
                ALTER TABLE "MinimalPairSession" ALTER COLUMN "Id" TYPE text USING "SyncId";
                ALTER TABLE "MinimalPairSession" DROP COLUMN "SyncId";
                ALTER TABLE "MinimalPairAttempt" ALTER COLUMN "Id" DROP IDENTITY IF EXISTS;
                ALTER TABLE "MinimalPairAttempt" ALTER COLUMN "Id" TYPE text USING gen_random_uuid()::text;

                ALTER TABLE "MinimalPairAttempt" ADD CONSTRAINT "FK_MinimalPairAttempt_MinimalPairSession_SessionId"
                    FOREIGN KEY ("SessionId") REFERENCES "MinimalPairSession" ("Id") ON DELETE CASCADE;
                ALTER TABLE "MinimalPairAttempt" ADD CONSTRAINT "FK_MinimalPairAttempt_MinimalPair_PairId"
                    FOREIGN KEY ("PairId") REFERENCES "MinimalPair" ("Id") ON DELETE CASCADE;

                ALTER TABLE "StreamHistory" ALTER COLUMN "Id" DROP IDENTITY IF EXISTS;
                ALTER TABLE "StreamHistory" ALTER COLUMN "Id" TYPE text USING gen_random_uuid()::text;

                ALTER TABLE "ConversationMemoryState" ADD COLUMN "UserProfileId" text NOT NULL DEFAULT '';
                UPDATE "ConversationMemoryState" m SET "UserProfileId" = c."UserProfileId"
                    FROM "Conversation" c WHERE m."ConversationId" = c."Id" AND c."UserProfileId" IS NOT NULL;
                ALTER TABLE "ConversationMemoryState" ALTER COLUMN "Id" DROP IDENTITY IF EXISTS;
                ALTER TABLE "ConversationMemoryState" ALTER COLUMN "Id" TYPE text USING 'cm:' || "ConversationId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMemoryState_UserProfileId",
                table: "ConversationMemoryState",
                column: "UserProfileId");
        }

        /// <summary>
        /// Restores number IDs (renumbered 1..n in creation order) and re-links answers. Text IDs
        /// cannot become their original numbers, which were never kept. No row is deleted.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationMemoryState_UserProfileId",
                table: "ConversationMemoryState");

            migrationBuilder.Sql("""
                ALTER TABLE "MinimalPairAttempt" DROP CONSTRAINT "FK_MinimalPairAttempt_MinimalPairSession_SessionId";
                ALTER TABLE "MinimalPairAttempt" DROP CONSTRAINT "FK_MinimalPairAttempt_MinimalPair_PairId";

                ALTER TABLE "MinimalPair" ADD COLUMN "OldId" integer;
                UPDATE "MinimalPair" p SET "OldId" = n.rn FROM (
                    SELECT "Id", row_number() OVER (ORDER BY "CreatedAt", "Id") AS rn FROM "MinimalPair") n
                    WHERE p."Id" = n."Id";
                ALTER TABLE "MinimalPairSession" ADD COLUMN "OldId" integer;
                UPDATE "MinimalPairSession" s SET "OldId" = n.rn FROM (
                    SELECT "Id", row_number() OVER (ORDER BY "CreatedAt", "Id") AS rn FROM "MinimalPairSession") n
                    WHERE s."Id" = n."Id";
                ALTER TABLE "MinimalPairAttempt" ADD COLUMN "OldId" integer;
                UPDATE "MinimalPairAttempt" a SET "OldId" = n.rn FROM (
                    SELECT "Id", row_number() OVER (ORDER BY "CreatedAt", "Id") AS rn FROM "MinimalPairAttempt") n
                    WHERE a."Id" = n."Id";

                UPDATE "MinimalPairAttempt" a SET "SessionId" = s."OldId"::text
                    FROM "MinimalPairSession" s WHERE a."SessionId" = s."Id";
                UPDATE "MinimalPairAttempt" a SET "PairId" = p."OldId"::text
                    FROM "MinimalPair" p WHERE a."PairId" = p."Id";
                ALTER TABLE "MinimalPairAttempt" ALTER COLUMN "SessionId" TYPE integer USING "SessionId"::integer;
                ALTER TABLE "MinimalPairAttempt" ALTER COLUMN "PairId" TYPE integer USING "PairId"::integer;

                ALTER TABLE "MinimalPair" ALTER COLUMN "Id" TYPE integer USING "OldId";
                ALTER TABLE "MinimalPair" DROP COLUMN "OldId";
                ALTER TABLE "MinimalPair" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"MinimalPair"', 'Id'), COALESCE(MAX("Id"), 0) + 1, false) FROM "MinimalPair";
                ALTER TABLE "MinimalPairSession" ALTER COLUMN "Id" TYPE integer USING "OldId";
                ALTER TABLE "MinimalPairSession" DROP COLUMN "OldId";
                ALTER TABLE "MinimalPairSession" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"MinimalPairSession"', 'Id'), COALESCE(MAX("Id"), 0) + 1, false) FROM "MinimalPairSession";
                ALTER TABLE "MinimalPairAttempt" ALTER COLUMN "Id" TYPE integer USING "OldId";
                ALTER TABLE "MinimalPairAttempt" DROP COLUMN "OldId";
                ALTER TABLE "MinimalPairAttempt" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"MinimalPairAttempt"', 'Id'), COALESCE(MAX("Id"), 0) + 1, false) FROM "MinimalPairAttempt";

                ALTER TABLE "MinimalPairAttempt" ADD CONSTRAINT "FK_MinimalPairAttempt_MinimalPairSession_SessionId"
                    FOREIGN KEY ("SessionId") REFERENCES "MinimalPairSession" ("Id") ON DELETE CASCADE;
                ALTER TABLE "MinimalPairAttempt" ADD CONSTRAINT "FK_MinimalPairAttempt_MinimalPair_PairId"
                    FOREIGN KEY ("PairId") REFERENCES "MinimalPair" ("Id") ON DELETE CASCADE;

                ALTER TABLE "StreamHistory" ADD COLUMN "OldId" integer;
                UPDATE "StreamHistory" h SET "OldId" = n.rn FROM (
                    SELECT "Id", row_number() OVER (ORDER BY "CreatedAt", "Id") AS rn FROM "StreamHistory") n
                    WHERE h."Id" = n."Id";
                ALTER TABLE "StreamHistory" ALTER COLUMN "Id" TYPE integer USING "OldId";
                ALTER TABLE "StreamHistory" DROP COLUMN "OldId";
                ALTER TABLE "StreamHistory" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"StreamHistory"', 'Id'), COALESCE(MAX("Id"), 0) + 1, false) FROM "StreamHistory";

                ALTER TABLE "ConversationMemoryState" ADD COLUMN "OldId" integer;
                UPDATE "ConversationMemoryState" m SET "OldId" = n.rn FROM (
                    SELECT "Id", row_number() OVER (ORDER BY "CreatedAt", "Id") AS rn FROM "ConversationMemoryState") n
                    WHERE m."Id" = n."Id";
                ALTER TABLE "ConversationMemoryState" ALTER COLUMN "Id" TYPE integer USING "OldId";
                ALTER TABLE "ConversationMemoryState" DROP COLUMN "OldId";
                ALTER TABLE "ConversationMemoryState" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY;
                SELECT setval(pg_get_serial_sequence('"ConversationMemoryState"', 'Id'), COALESCE(MAX("Id"), 0) + 1, false) FROM "ConversationMemoryState";
                ALTER TABLE "ConversationMemoryState" DROP COLUMN "UserProfileId";
                """);
        }
    }
}
