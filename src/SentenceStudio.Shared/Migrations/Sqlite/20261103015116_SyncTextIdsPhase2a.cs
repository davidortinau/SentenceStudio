using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <summary>
    /// Phase 2a of syncing every learner table (Captain 2026-11-02, docs/sync-all-tables-plan.md).
    /// SQLite counterpart of the PostgreSQL migration of the same id: number IDs become text IDs
    /// that are unique on every store. Old numbers are NOT reused as text (each device numbered
    /// its rows from 1). Minimal Pairs and conversation memories get IDs built from what they
    /// describe (SyncIds, keep the formats identical); every other row gets a new GUID. SQLite
    /// cannot change a column type, so each table is rebuilt: new table, copy every row, drop the
    /// old one, rename. Children are dropped before parents so no cascade runs. No row is lost.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261103015116_SyncTextIdsPhase2a")]
    public partial class SyncTextIdsPhase2a : Migration
    {
        private const string NewGuid =
            "lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || "
            + "substr(lower(hex(randomblob(2))), 2) || '-' || substr('89ab', 1 + (abs(random()) % 4), 1) || "
            + "substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6)))";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                CREATE TEMP TABLE "MapMinimalPairSession" AS
                    SELECT "Id" AS "OldId", {NewGuid} AS "NewId" FROM "MinimalPairSession";

                CREATE TABLE "MinimalPair_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_MinimalPair" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "VocabularyWordAId" TEXT NOT NULL,
                    "VocabularyWordBId" TEXT NOT NULL,
                    "ContrastLabel" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_MinimalPair_VocabularyWord_VocabularyWordAId" FOREIGN KEY ("VocabularyWordAId") REFERENCES "VocabularyWord" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_MinimalPair_VocabularyWord_VocabularyWordBId" FOREIGN KEY ("VocabularyWordBId") REFERENCES "VocabularyWord" ("Id") ON DELETE CASCADE
                );
                INSERT INTO "MinimalPair_new" ("Id", "UserId", "VocabularyWordAId", "VocabularyWordBId", "ContrastLabel", "CreatedAt", "UpdatedAt")
                    SELECT 'mp:' || "UserId" || ':' || "VocabularyWordAId" || ':' || "VocabularyWordBId",
                        "UserId", "VocabularyWordAId", "VocabularyWordBId", "ContrastLabel", "CreatedAt", "UpdatedAt"
                    FROM "MinimalPair";

                CREATE TABLE "MinimalPairSession_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_MinimalPairSession" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Mode" TEXT NOT NULL,
                    "PlannedTrialCount" INTEGER NULL,
                    "StartedAt" TEXT NOT NULL,
                    "EndedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                INSERT INTO "MinimalPairSession_new" ("Id", "UserId", "Mode", "PlannedTrialCount", "StartedAt", "EndedAt", "CreatedAt", "UpdatedAt")
                    SELECT m."NewId", s."UserId", s."Mode", s."PlannedTrialCount", s."StartedAt", s."EndedAt", s."CreatedAt", s."UpdatedAt"
                    FROM "MinimalPairSession" s JOIN "MapMinimalPairSession" m ON m."OldId" = s."Id";

                CREATE TABLE "MinimalPairAttempt_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_MinimalPairAttempt" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "SessionId" TEXT NOT NULL,
                    "PairId" TEXT NOT NULL,
                    "PromptWordId" TEXT NOT NULL,
                    "SelectedWordId" TEXT NOT NULL,
                    "IsCorrect" INTEGER NOT NULL,
                    "SequenceNumber" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_MinimalPairAttempt_MinimalPairSession_SessionId" FOREIGN KEY ("SessionId") REFERENCES "MinimalPairSession_new" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_MinimalPairAttempt_MinimalPair_PairId" FOREIGN KEY ("PairId") REFERENCES "MinimalPair_new" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_MinimalPairAttempt_VocabularyWord_PromptWordId" FOREIGN KEY ("PromptWordId") REFERENCES "VocabularyWord" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_MinimalPairAttempt_VocabularyWord_SelectedWordId" FOREIGN KEY ("SelectedWordId") REFERENCES "VocabularyWord" ("Id") ON DELETE RESTRICT
                );
                INSERT INTO "MinimalPairAttempt_new" ("Id", "UserId", "SessionId", "PairId", "PromptWordId", "SelectedWordId", "IsCorrect", "SequenceNumber", "CreatedAt")
                    SELECT {NewGuid}, a."UserId", ms."NewId",
                        'mp:' || p."UserId" || ':' || p."VocabularyWordAId" || ':' || p."VocabularyWordBId",
                        a."PromptWordId", a."SelectedWordId", a."IsCorrect", a."SequenceNumber", a."CreatedAt"
                    FROM "MinimalPairAttempt" a
                    JOIN "MapMinimalPairSession" ms ON ms."OldId" = a."SessionId"
                    JOIN "MinimalPair" p ON p."Id" = a."PairId";

                DROP TABLE "MinimalPairAttempt";
                DROP TABLE "MinimalPair";
                DROP TABLE "MinimalPairSession";
                ALTER TABLE "MinimalPair_new" RENAME TO "MinimalPair";
                ALTER TABLE "MinimalPairSession_new" RENAME TO "MinimalPairSession";
                ALTER TABLE "MinimalPairAttempt_new" RENAME TO "MinimalPairAttempt";
                CREATE UNIQUE INDEX "IX_MinimalPair_UserId_VocabularyWordAId_VocabularyWordBId" ON "MinimalPair" ("UserId", "VocabularyWordAId", "VocabularyWordBId");
                CREATE INDEX "IX_MinimalPair_VocabularyWordAId" ON "MinimalPair" ("VocabularyWordAId");
                CREATE INDEX "IX_MinimalPair_VocabularyWordBId" ON "MinimalPair" ("VocabularyWordBId");
                CREATE INDEX "IX_MinimalPairAttempt_PairId_CreatedAt" ON "MinimalPairAttempt" ("PairId", "CreatedAt");
                CREATE INDEX "IX_MinimalPairAttempt_PromptWordId" ON "MinimalPairAttempt" ("PromptWordId");
                CREATE INDEX "IX_MinimalPairAttempt_SelectedWordId" ON "MinimalPairAttempt" ("SelectedWordId");
                CREATE INDEX "IX_MinimalPairAttempt_SessionId_SequenceNumber" ON "MinimalPairAttempt" ("SessionId", "SequenceNumber");
                DROP TABLE "MapMinimalPairSession";

                CREATE TABLE "StreamHistory_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_StreamHistory" PRIMARY KEY,
                    "Phrase" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "Duration" REAL NOT NULL,
                    "VoiceId" TEXT NULL,
                    "AudioFilePath" TEXT NULL,
                    "FileName" TEXT NULL,
                    "Title" TEXT NULL,
                    "Source" TEXT NULL,
                    "SourceUrl" TEXT NULL,
                    "UserProfileId" TEXT NULL
                );
                INSERT INTO "StreamHistory_new" ("Id", "Phrase", "CreatedAt", "UpdatedAt", "Duration", "VoiceId", "AudioFilePath", "FileName", "Title", "Source", "SourceUrl", "UserProfileId")
                    SELECT {NewGuid}, "Phrase", "CreatedAt", "UpdatedAt", "Duration", "VoiceId", "AudioFilePath", "FileName", "Title", "Source", "SourceUrl", "UserProfileId"
                    FROM "StreamHistory";
                DROP TABLE "StreamHistory";
                ALTER TABLE "StreamHistory_new" RENAME TO "StreamHistory";
                CREATE INDEX "IX_StreamHistory_UserProfileId_CreatedAt" ON "StreamHistory" ("UserProfileId", "CreatedAt");

                CREATE TABLE "ConversationMemoryState_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ConversationMemoryState" PRIMARY KEY,
                    "UserProfileId" TEXT NOT NULL DEFAULT '',
                    "ConversationId" TEXT NOT NULL,
                    "SerializedState" TEXT NOT NULL,
                    "ConversationSummary" TEXT NULL,
                    "DiscussedVocabulary" TEXT NULL,
                    "DetectedProficiencyLevel" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_ConversationMemoryState_Conversation_ConversationId" FOREIGN KEY ("ConversationId") REFERENCES "Conversation" ("Id") ON DELETE CASCADE
                );
                INSERT INTO "ConversationMemoryState_new" ("Id", "UserProfileId", "ConversationId", "SerializedState", "ConversationSummary", "DiscussedVocabulary", "DetectedProficiencyLevel", "CreatedAt", "UpdatedAt")
                    SELECT 'cm:' || m."ConversationId", COALESCE(c."UserProfileId", ''), m."ConversationId", m."SerializedState",
                        m."ConversationSummary", m."DiscussedVocabulary", m."DetectedProficiencyLevel", m."CreatedAt", m."UpdatedAt"
                    FROM "ConversationMemoryState" m LEFT JOIN "Conversation" c ON c."Id" = m."ConversationId";
                DROP TABLE "ConversationMemoryState";
                ALTER TABLE "ConversationMemoryState_new" RENAME TO "ConversationMemoryState";
                CREATE UNIQUE INDEX "IX_ConversationMemoryState_ConversationId" ON "ConversationMemoryState" ("ConversationId");
                CREATE INDEX "IX_ConversationMemoryState_UserProfileId" ON "ConversationMemoryState" ("UserProfileId");
                """);
        }

        /// <summary>
        /// Restores number IDs (renumbered 1..n in creation order) and re-links answers. Text IDs
        /// cannot become their original numbers, which were never kept. No row is lost.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "MapMinimalPair" AS
                    SELECT "Id" AS "NewId", row_number() OVER (ORDER BY "CreatedAt", "Id") AS "OldId" FROM "MinimalPair";
                CREATE TEMP TABLE "MapMinimalPairSession" AS
                    SELECT "Id" AS "NewId", row_number() OVER (ORDER BY "CreatedAt", "Id") AS "OldId" FROM "MinimalPairSession";

                CREATE TABLE "MinimalPair_old" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_MinimalPair" PRIMARY KEY AUTOINCREMENT,
                    "UserId" TEXT NOT NULL,
                    "VocabularyWordAId" TEXT NOT NULL,
                    "VocabularyWordBId" TEXT NOT NULL,
                    "ContrastLabel" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_MinimalPair_VocabularyWord_VocabularyWordAId" FOREIGN KEY ("VocabularyWordAId") REFERENCES "VocabularyWord" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_MinimalPair_VocabularyWord_VocabularyWordBId" FOREIGN KEY ("VocabularyWordBId") REFERENCES "VocabularyWord" ("Id") ON DELETE CASCADE
                );
                INSERT INTO "MinimalPair_old" ("Id", "UserId", "VocabularyWordAId", "VocabularyWordBId", "ContrastLabel", "CreatedAt", "UpdatedAt")
                    SELECT m."OldId", p."UserId", p."VocabularyWordAId", p."VocabularyWordBId", p."ContrastLabel", p."CreatedAt", p."UpdatedAt"
                    FROM "MinimalPair" p JOIN "MapMinimalPair" m ON m."NewId" = p."Id";

                CREATE TABLE "MinimalPairSession_old" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_MinimalPairSession" PRIMARY KEY AUTOINCREMENT,
                    "UserId" TEXT NOT NULL,
                    "Mode" TEXT NOT NULL,
                    "PlannedTrialCount" INTEGER NULL,
                    "StartedAt" TEXT NOT NULL,
                    "EndedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                INSERT INTO "MinimalPairSession_old" ("Id", "UserId", "Mode", "PlannedTrialCount", "StartedAt", "EndedAt", "CreatedAt", "UpdatedAt")
                    SELECT m."OldId", s."UserId", s."Mode", s."PlannedTrialCount", s."StartedAt", s."EndedAt", s."CreatedAt", s."UpdatedAt"
                    FROM "MinimalPairSession" s JOIN "MapMinimalPairSession" m ON m."NewId" = s."Id";

                CREATE TABLE "MinimalPairAttempt_old" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_MinimalPairAttempt" PRIMARY KEY AUTOINCREMENT,
                    "UserId" TEXT NOT NULL,
                    "SessionId" INTEGER NOT NULL,
                    "PairId" INTEGER NOT NULL,
                    "PromptWordId" TEXT NOT NULL,
                    "SelectedWordId" TEXT NOT NULL,
                    "IsCorrect" INTEGER NOT NULL,
                    "SequenceNumber" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_MinimalPairAttempt_MinimalPairSession_SessionId" FOREIGN KEY ("SessionId") REFERENCES "MinimalPairSession_old" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_MinimalPairAttempt_MinimalPair_PairId" FOREIGN KEY ("PairId") REFERENCES "MinimalPair_old" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_MinimalPairAttempt_VocabularyWord_PromptWordId" FOREIGN KEY ("PromptWordId") REFERENCES "VocabularyWord" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_MinimalPairAttempt_VocabularyWord_SelectedWordId" FOREIGN KEY ("SelectedWordId") REFERENCES "VocabularyWord" ("Id") ON DELETE RESTRICT
                );
                INSERT INTO "MinimalPairAttempt_old" ("UserId", "SessionId", "PairId", "PromptWordId", "SelectedWordId", "IsCorrect", "SequenceNumber", "CreatedAt")
                    SELECT a."UserId", ms."OldId", mp."OldId", a."PromptWordId", a."SelectedWordId", a."IsCorrect", a."SequenceNumber", a."CreatedAt"
                    FROM "MinimalPairAttempt" a
                    JOIN "MapMinimalPairSession" ms ON ms."NewId" = a."SessionId"
                    JOIN "MapMinimalPair" mp ON mp."NewId" = a."PairId"
                    ORDER BY a."CreatedAt", a."Id";

                DROP TABLE "MinimalPairAttempt";
                DROP TABLE "MinimalPair";
                DROP TABLE "MinimalPairSession";
                ALTER TABLE "MinimalPair_old" RENAME TO "MinimalPair";
                ALTER TABLE "MinimalPairSession_old" RENAME TO "MinimalPairSession";
                ALTER TABLE "MinimalPairAttempt_old" RENAME TO "MinimalPairAttempt";
                CREATE UNIQUE INDEX "IX_MinimalPair_UserId_VocabularyWordAId_VocabularyWordBId" ON "MinimalPair" ("UserId", "VocabularyWordAId", "VocabularyWordBId");
                CREATE INDEX "IX_MinimalPair_VocabularyWordAId" ON "MinimalPair" ("VocabularyWordAId");
                CREATE INDEX "IX_MinimalPair_VocabularyWordBId" ON "MinimalPair" ("VocabularyWordBId");
                CREATE INDEX "IX_MinimalPairAttempt_PairId_CreatedAt" ON "MinimalPairAttempt" ("PairId", "CreatedAt");
                CREATE INDEX "IX_MinimalPairAttempt_PromptWordId" ON "MinimalPairAttempt" ("PromptWordId");
                CREATE INDEX "IX_MinimalPairAttempt_SelectedWordId" ON "MinimalPairAttempt" ("SelectedWordId");
                CREATE INDEX "IX_MinimalPairAttempt_SessionId_SequenceNumber" ON "MinimalPairAttempt" ("SessionId", "SequenceNumber");
                DROP TABLE "MapMinimalPair";
                DROP TABLE "MapMinimalPairSession";

                CREATE TABLE "StreamHistory_old" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_StreamHistory" PRIMARY KEY AUTOINCREMENT,
                    "Phrase" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "Duration" REAL NOT NULL,
                    "VoiceId" TEXT NULL,
                    "AudioFilePath" TEXT NULL,
                    "FileName" TEXT NULL,
                    "Title" TEXT NULL,
                    "Source" TEXT NULL,
                    "SourceUrl" TEXT NULL,
                    "UserProfileId" TEXT NULL
                );
                INSERT INTO "StreamHistory_old" ("Phrase", "CreatedAt", "UpdatedAt", "Duration", "VoiceId", "AudioFilePath", "FileName", "Title", "Source", "SourceUrl", "UserProfileId")
                    SELECT "Phrase", "CreatedAt", "UpdatedAt", "Duration", "VoiceId", "AudioFilePath", "FileName", "Title", "Source", "SourceUrl", "UserProfileId"
                    FROM "StreamHistory" ORDER BY "CreatedAt", "Id";
                DROP TABLE "StreamHistory";
                ALTER TABLE "StreamHistory_old" RENAME TO "StreamHistory";
                CREATE INDEX "IX_StreamHistory_UserProfileId_CreatedAt" ON "StreamHistory" ("UserProfileId", "CreatedAt");

                CREATE TABLE "ConversationMemoryState_old" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_ConversationMemoryState" PRIMARY KEY AUTOINCREMENT,
                    "ConversationId" TEXT NOT NULL,
                    "SerializedState" TEXT NOT NULL,
                    "ConversationSummary" TEXT NULL,
                    "DiscussedVocabulary" TEXT NULL,
                    "DetectedProficiencyLevel" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_ConversationMemoryState_Conversation_ConversationId" FOREIGN KEY ("ConversationId") REFERENCES "Conversation" ("Id") ON DELETE CASCADE
                );
                INSERT INTO "ConversationMemoryState_old" ("ConversationId", "SerializedState", "ConversationSummary", "DiscussedVocabulary", "DetectedProficiencyLevel", "CreatedAt", "UpdatedAt")
                    SELECT "ConversationId", "SerializedState", "ConversationSummary", "DiscussedVocabulary", "DetectedProficiencyLevel", "CreatedAt", "UpdatedAt"
                    FROM "ConversationMemoryState" ORDER BY "CreatedAt", "Id";
                DROP TABLE "ConversationMemoryState";
                ALTER TABLE "ConversationMemoryState_old" RENAME TO "ConversationMemoryState";
                CREATE UNIQUE INDEX "IX_ConversationMemoryState_ConversationId" ON "ConversationMemoryState" ("ConversationId");
                """);
        }
    }
}
