using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <summary>
    /// Phase 3 of syncing every learner table (Captain 2026-11-02 and 2026-11-03,
    /// docs/sync-all-tables-plan.md). SQLite counterpart of the PostgreSQL migration of the same id:
    /// scene images, conversation scenarios and example sentences get text IDs and an owner
    /// column, and a conversation's scenario link becomes a plain text reference with no
    /// constraint.
    /// <list type="bullet">
    /// <item>Built-in scenarios get their fixed ID (SyncIds.BuiltInScenario, keep the formats
    /// identical); every other row gets a new GUID. Conversation links follow their scenario; a
    /// link to a missing scenario keeps its old number as text, which matches nothing.</item>
    /// <item>Example sentences take their resource's owner, else their word's owner. Scene images
    /// and custom scenarios stay unowned here; a native per-learner database gives them its
    /// learner when it opens (NativeOwnerStores.AdoptUnownedRowsAsync), the pre-account database
    /// and MAUI keep them unowned and local (D3).</item>
    /// </list>
    /// SQLite cannot change a column type or drop a constraint, so the four tables are rebuilt
    /// (new table, copy every row, drop the old one, rename) with foreign keys off, as EF does
    /// for its own rebuilds: dropping "Conversation" with foreign keys on would delete every
    /// conversation's chunks and memory. Rebuilding "Conversation" drops its sync tracking;
    /// the app tracks it again and re-sends the learner's conversations once (SyncLateTables).
    /// No row is lost.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261103230000_SyncOwnersPhase3")]
    public partial class SyncOwnersPhase3 : Migration
    {
        private const string NewGuid =
            "lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || "
            + "substr(lower(hex(randomblob(2))), 2) || '-' || substr('89ab', 1 + (abs(random()) % 4), 1) || "
            + "substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6)))";

        private const string ScenarioColumns = """
                "Name" TEXT NOT NULL,
                "NameKorean" TEXT NULL,
                "PersonaName" TEXT NOT NULL,
                "PersonaDescription" TEXT NOT NULL,
                "SituationDescription" TEXT NOT NULL,
                "ConversationType" INTEGER NOT NULL,
                "QuestionBank" TEXT NULL,
                "IsPredefined" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            """;

        private const string ScenarioCopy =
            "\"Name\", \"NameKorean\", \"PersonaName\", \"PersonaDescription\", \"SituationDescription\", "
            + "\"ConversationType\", \"QuestionBank\", \"IsPredefined\", \"CreatedAt\", \"UpdatedAt\"";

        private const string SentenceColumns = """
                "VocabularyWordId" TEXT NOT NULL,
                "LearningResourceId" TEXT NULL,
                "TargetSentence" TEXT NOT NULL,
                "NativeSentence" TEXT NULL,
                "AudioUri" TEXT NULL,
                "IsCore" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "Source" INTEGER NOT NULL DEFAULT 0,
                "Status" INTEGER NOT NULL DEFAULT 1,
                "Register" INTEGER NOT NULL DEFAULT 0,
                "DifficultyLevel" INTEGER NULL,
                "IsFlagged" INTEGER NOT NULL DEFAULT 0
            """;

        private const string SentenceConstraints = """
                CONSTRAINT "FK_ExampleSentence_LearningResource_LearningResourceId" FOREIGN KEY ("LearningResourceId") REFERENCES "LearningResource" ("Id") ON DELETE SET NULL,
                CONSTRAINT "FK_ExampleSentence_VocabularyWord_VocabularyWordId" FOREIGN KEY ("VocabularyWordId") REFERENCES "VocabularyWord" ("Id") ON DELETE CASCADE
            """;

        private const string SentenceCopy =
            "\"VocabularyWordId\", \"LearningResourceId\", \"TargetSentence\", \"NativeSentence\", \"AudioUri\", "
            + "\"IsCore\", \"CreatedAt\", \"UpdatedAt\", \"Source\", \"Status\", \"Register\", \"DifficultyLevel\", \"IsFlagged\"";

        private const string ConversationIndexes = """
            CREATE INDEX "IX_Conversation_ScenarioId" ON "Conversation" ("ScenarioId");
            CREATE INDEX "IX_Conversation_UserProfileId" ON "Conversation" ("UserProfileId");
            CREATE INDEX "IX_Conversation_UserProfileId_CreatedAt" ON "Conversation" ("UserProfileId", "CreatedAt");
            """;

        private const string SentenceIndexes = """
            CREATE INDEX "IX_ExampleSentence_LearningResourceId" ON "ExampleSentence" ("LearningResourceId");
            CREATE INDEX "IX_ExampleSentence_VocabularyWordId" ON "ExampleSentence" ("VocabularyWordId");
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            migrationBuilder.Sql($$"""
                -- The first copy of each built-in gets its fixed ID; a second copy, a new GUID.
                CREATE TEMP TABLE "MapConversationScenario" AS
                    SELECT s."Id" AS "OldId",
                        CASE WHEN s."IsPredefined" = 1 AND s."Id" = (
                                SELECT MIN(d."Id") FROM "ConversationScenario" d
                                WHERE d."IsPredefined" = 1
                                    AND lower(replace(trim(d."Name"), ' ', '-'))
                                        = lower(replace(trim(s."Name"), ' ', '-')))
                            THEN 'scn:' || lower(replace(trim(s."Name"), ' ', '-'))
                            ELSE {{NewGuid}}
                        END AS "NewId"
                    FROM "ConversationScenario" s;

                CREATE TABLE "ConversationScenario_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ConversationScenario" PRIMARY KEY,
                {{ScenarioColumns}},
                    "UserProfileId" TEXT NULL
                );
                INSERT INTO "ConversationScenario_new" ("Id", {{ScenarioCopy}}, "UserProfileId")
                    SELECT m."NewId", {{ScenarioCopy}}, NULL
                    FROM "ConversationScenario" s JOIN "MapConversationScenario" m ON m."OldId" = s."Id";
                DROP TABLE "ConversationScenario";
                ALTER TABLE "ConversationScenario_new" RENAME TO "ConversationScenario";
                CREATE INDEX "IX_ConversationScenario_UserProfileId" ON "ConversationScenario" ("UserProfileId");

                CREATE TABLE "Conversation_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Conversation" PRIMARY KEY,
                    "CreatedAt" TEXT NOT NULL,
                    "Language" TEXT NOT NULL,
                    "ScenarioId" TEXT NULL,
                    "UserProfileId" TEXT NULL
                );
                INSERT INTO "Conversation_new" ("Id", "CreatedAt", "Language", "ScenarioId", "UserProfileId")
                    SELECT c."Id", c."CreatedAt", c."Language",
                        COALESCE((SELECT m."NewId" FROM "MapConversationScenario" m WHERE m."OldId" = c."ScenarioId"),
                            CAST(c."ScenarioId" AS TEXT)),
                        c."UserProfileId"
                    FROM "Conversation" c;
                DROP TABLE "Conversation";
                ALTER TABLE "Conversation_new" RENAME TO "Conversation";
                {{ConversationIndexes}}
                DROP TABLE "MapConversationScenario";

                CREATE TABLE "SceneImage_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_SceneImage" PRIMARY KEY,
                    "Url" TEXT NULL,
                    "Description" TEXT NULL,
                    "IsSelected" INTEGER NOT NULL,
                    "UserProfileId" TEXT NULL
                );
                INSERT INTO "SceneImage_new" ("Id", "Url", "Description", "IsSelected", "UserProfileId")
                    SELECT {{NewGuid}}, "Url", "Description", "IsSelected", NULL FROM "SceneImage";
                DROP TABLE "SceneImage";
                ALTER TABLE "SceneImage_new" RENAME TO "SceneImage";
                CREATE INDEX "IX_SceneImage_UserProfileId" ON "SceneImage" ("UserProfileId");

                CREATE TABLE "ExampleSentence_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ExampleSentence" PRIMARY KEY,
                {{SentenceColumns}},
                    "UserProfileId" TEXT NULL,
                {{SentenceConstraints}}
                );
                INSERT INTO "ExampleSentence_new" ("Id", {{SentenceCopy}}, "UserProfileId")
                    SELECT {{NewGuid}}, e."VocabularyWordId", e."LearningResourceId", e."TargetSentence",
                        e."NativeSentence", e."AudioUri", e."IsCore", e."CreatedAt", e."UpdatedAt", e."Source",
                        e."Status", e."Register", e."DifficultyLevel", e."IsFlagged",
                        COALESCE(NULLIF(r."UserProfileId", ''), NULLIF(w."UserProfileId", ''))
                    FROM "ExampleSentence" e
                    LEFT JOIN "LearningResource" r ON r."Id" = e."LearningResourceId"
                    LEFT JOIN "VocabularyWord" w ON w."Id" = e."VocabularyWordId";
                DROP TABLE "ExampleSentence";
                ALTER TABLE "ExampleSentence_new" RENAME TO "ExampleSentence";
                {{SentenceIndexes}}
                CREATE INDEX "IX_ExampleSentence_UserProfileId" ON "ExampleSentence" ("UserProfileId");
                """);

            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
        }

        /// <summary>
        /// Restores number IDs (renumbered 1..n in creation order), re-links conversations and
        /// restores their constraint, and drops the owner columns. Text IDs cannot become their
        /// original numbers, which were never kept. A conversation linked to a scenario that no
        /// longer exists loses the link (the old constraint would refuse it). No row is lost.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            migrationBuilder.Sql($$"""
                CREATE TEMP TABLE "MapConversationScenario" AS
                    SELECT "Id" AS "OldId", row_number() OVER (ORDER BY "CreatedAt", "Id") AS "NewId"
                    FROM "ConversationScenario";

                CREATE TABLE "ConversationScenario_new" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_ConversationScenario" PRIMARY KEY AUTOINCREMENT,
                {{ScenarioColumns}}
                );
                INSERT INTO "ConversationScenario_new" ("Id", {{ScenarioCopy}})
                    SELECT m."NewId", {{ScenarioCopy}}
                    FROM "ConversationScenario" s JOIN "MapConversationScenario" m ON m."OldId" = s."Id";
                DROP TABLE "ConversationScenario";
                ALTER TABLE "ConversationScenario_new" RENAME TO "ConversationScenario";

                CREATE TABLE "Conversation_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Conversation" PRIMARY KEY,
                    "CreatedAt" TEXT NOT NULL,
                    "Language" TEXT NOT NULL,
                    "ScenarioId" INTEGER NULL, "UserProfileId" TEXT NULL,
                    CONSTRAINT "FK_Conversation_ConversationScenario_ScenarioId" FOREIGN KEY ("ScenarioId") REFERENCES "ConversationScenario" ("Id") ON DELETE SET NULL
                );
                INSERT INTO "Conversation_new" ("Id", "CreatedAt", "Language", "ScenarioId", "UserProfileId")
                    SELECT c."Id", c."CreatedAt", c."Language",
                        (SELECT m."NewId" FROM "MapConversationScenario" m WHERE m."OldId" = c."ScenarioId"),
                        c."UserProfileId"
                    FROM "Conversation" c;
                DROP TABLE "Conversation";
                ALTER TABLE "Conversation_new" RENAME TO "Conversation";
                {{ConversationIndexes}}
                DROP TABLE "MapConversationScenario";

                CREATE TABLE "SceneImage_new" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SceneImage" PRIMARY KEY AUTOINCREMENT,
                    "Url" TEXT NULL,
                    "Description" TEXT NULL,
                    "IsSelected" INTEGER NOT NULL
                );
                INSERT INTO "SceneImage_new" ("Id", "Url", "Description", "IsSelected")
                    SELECT row_number() OVER (ORDER BY "Id"), "Url", "Description", "IsSelected" FROM "SceneImage";
                DROP TABLE "SceneImage";
                ALTER TABLE "SceneImage_new" RENAME TO "SceneImage";

                CREATE TABLE "ExampleSentence_new" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_ExampleSentence" PRIMARY KEY AUTOINCREMENT,
                {{SentenceColumns}},
                {{SentenceConstraints}}
                );
                INSERT INTO "ExampleSentence_new" ("Id", {{SentenceCopy}})
                    SELECT row_number() OVER (ORDER BY "CreatedAt", "Id"), {{SentenceCopy}} FROM "ExampleSentence";
                DROP TABLE "ExampleSentence";
                ALTER TABLE "ExampleSentence_new" RENAME TO "ExampleSentence";
                {{SentenceIndexes}}
                """);

            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
        }
    }
}
