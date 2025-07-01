using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class UpdateTableNamesToSingular : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Challenges_UserActivities_UserActivityID",
                table: "Challenges");

            migrationBuilder.DropForeignKey(
                name: "FK_ConversationChunks_Conversations_ConversationID",
                table: "ConversationChunks");

            migrationBuilder.DropForeignKey(
                name: "FK_VocabularyWords_Challenges_ChallengeID",
                table: "VocabularyWords");

            migrationBuilder.DropForeignKey(
                name: "FK_VocabularyWords_VocabularyLists_VocabularyListID",
                table: "VocabularyWords");

            migrationBuilder.DropPrimaryKey(
                name: "PK_VocabularyWords",
                table: "VocabularyWords");

            migrationBuilder.DropPrimaryKey(
                name: "PK_VocabularyLists",
                table: "VocabularyLists");

            migrationBuilder.DropPrimaryKey(
                name: "PK_UserProfiles",
                table: "UserProfiles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_UserActivities",
                table: "UserActivities");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StreamHistories",
                table: "StreamHistories");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Stories",
                table: "Stories");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SkillProfiles",
                table: "SkillProfiles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SceneImages",
                table: "SceneImages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ResourceVocabularyMappings",
                table: "ResourceVocabularyMappings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_LearningResources",
                table: "LearningResources");

            migrationBuilder.DropPrimaryKey(
                name: "PK_GradeResponses",
                table: "GradeResponses");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Conversations",
                table: "Conversations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConversationChunks",
                table: "ConversationChunks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Challenges",
                table: "Challenges");

            migrationBuilder.RenameTable(
                name: "VocabularyWords",
                newName: "VocabularyWord");

            migrationBuilder.RenameTable(
                name: "VocabularyLists",
                newName: "VocabularyList");

            migrationBuilder.RenameTable(
                name: "UserProfiles",
                newName: "UserProfile");

            migrationBuilder.RenameTable(
                name: "UserActivities",
                newName: "UserActivity");

            migrationBuilder.RenameTable(
                name: "StreamHistories",
                newName: "StreamHistory");

            migrationBuilder.RenameTable(
                name: "Stories",
                newName: "Story");

            migrationBuilder.RenameTable(
                name: "SkillProfiles",
                newName: "SkillProfile");

            migrationBuilder.RenameTable(
                name: "SceneImages",
                newName: "SceneImage");

            migrationBuilder.RenameTable(
                name: "ResourceVocabularyMappings",
                newName: "ResourceVocabularyMapping");

            migrationBuilder.RenameTable(
                name: "LearningResources",
                newName: "LearningResource");

            migrationBuilder.RenameTable(
                name: "GradeResponses",
                newName: "GradeResponse");

            migrationBuilder.RenameTable(
                name: "Conversations",
                newName: "Conversation");

            migrationBuilder.RenameTable(
                name: "ConversationChunks",
                newName: "ConversationChunk");

            migrationBuilder.RenameTable(
                name: "Challenges",
                newName: "Challenge");

            migrationBuilder.RenameIndex(
                name: "IX_VocabularyWords_VocabularyListID",
                table: "VocabularyWord",
                newName: "IX_VocabularyWord_VocabularyListID");

            migrationBuilder.RenameIndex(
                name: "IX_VocabularyWords_ChallengeID",
                table: "VocabularyWord",
                newName: "IX_VocabularyWord_ChallengeID");

            migrationBuilder.RenameIndex(
                name: "IX_ConversationChunks_ConversationID",
                table: "ConversationChunk",
                newName: "IX_ConversationChunk_ConversationID");

            migrationBuilder.RenameIndex(
                name: "IX_Challenges_UserActivityID",
                table: "Challenge",
                newName: "IX_Challenge_UserActivityID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_VocabularyWord",
                table: "VocabularyWord",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_VocabularyList",
                table: "VocabularyList",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserProfile",
                table: "UserProfile",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserActivity",
                table: "UserActivity",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StreamHistory",
                table: "StreamHistory",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Story",
                table: "Story",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SkillProfile",
                table: "SkillProfile",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SceneImage",
                table: "SceneImage",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ResourceVocabularyMapping",
                table: "ResourceVocabularyMapping",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_LearningResource",
                table: "LearningResource",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_GradeResponse",
                table: "GradeResponse",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Conversation",
                table: "Conversation",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConversationChunk",
                table: "ConversationChunk",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Challenge",
                table: "Challenge",
                column: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_Challenge_UserActivity_UserActivityID",
                table: "Challenge",
                column: "UserActivityID",
                principalTable: "UserActivity",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationChunk_Conversation_ConversationID",
                table: "ConversationChunk",
                column: "ConversationID",
                principalTable: "Conversation",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_VocabularyWord_Challenge_ChallengeID",
                table: "VocabularyWord",
                column: "ChallengeID",
                principalTable: "Challenge",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_VocabularyWord_VocabularyList_VocabularyListID",
                table: "VocabularyWord",
                column: "VocabularyListID",
                principalTable: "VocabularyList",
                principalColumn: "ID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Challenge_UserActivity_UserActivityID",
                table: "Challenge");

            migrationBuilder.DropForeignKey(
                name: "FK_ConversationChunk_Conversation_ConversationID",
                table: "ConversationChunk");

            migrationBuilder.DropForeignKey(
                name: "FK_VocabularyWord_Challenge_ChallengeID",
                table: "VocabularyWord");

            migrationBuilder.DropForeignKey(
                name: "FK_VocabularyWord_VocabularyList_VocabularyListID",
                table: "VocabularyWord");

            migrationBuilder.DropPrimaryKey(
                name: "PK_VocabularyWord",
                table: "VocabularyWord");

            migrationBuilder.DropPrimaryKey(
                name: "PK_VocabularyList",
                table: "VocabularyList");

            migrationBuilder.DropPrimaryKey(
                name: "PK_UserProfile",
                table: "UserProfile");

            migrationBuilder.DropPrimaryKey(
                name: "PK_UserActivity",
                table: "UserActivity");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StreamHistory",
                table: "StreamHistory");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Story",
                table: "Story");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SkillProfile",
                table: "SkillProfile");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SceneImage",
                table: "SceneImage");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ResourceVocabularyMapping",
                table: "ResourceVocabularyMapping");

            migrationBuilder.DropPrimaryKey(
                name: "PK_LearningResource",
                table: "LearningResource");

            migrationBuilder.DropPrimaryKey(
                name: "PK_GradeResponse",
                table: "GradeResponse");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConversationChunk",
                table: "ConversationChunk");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Conversation",
                table: "Conversation");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Challenge",
                table: "Challenge");

            migrationBuilder.RenameTable(
                name: "VocabularyWord",
                newName: "VocabularyWords");

            migrationBuilder.RenameTable(
                name: "VocabularyList",
                newName: "VocabularyLists");

            migrationBuilder.RenameTable(
                name: "UserProfile",
                newName: "UserProfiles");

            migrationBuilder.RenameTable(
                name: "UserActivity",
                newName: "UserActivities");

            migrationBuilder.RenameTable(
                name: "StreamHistory",
                newName: "StreamHistories");

            migrationBuilder.RenameTable(
                name: "Story",
                newName: "Stories");

            migrationBuilder.RenameTable(
                name: "SkillProfile",
                newName: "SkillProfiles");

            migrationBuilder.RenameTable(
                name: "SceneImage",
                newName: "SceneImages");

            migrationBuilder.RenameTable(
                name: "ResourceVocabularyMapping",
                newName: "ResourceVocabularyMappings");

            migrationBuilder.RenameTable(
                name: "LearningResource",
                newName: "LearningResources");

            migrationBuilder.RenameTable(
                name: "GradeResponse",
                newName: "GradeResponses");

            migrationBuilder.RenameTable(
                name: "ConversationChunk",
                newName: "ConversationChunks");

            migrationBuilder.RenameTable(
                name: "Conversation",
                newName: "Conversations");

            migrationBuilder.RenameTable(
                name: "Challenge",
                newName: "Challenges");

            migrationBuilder.RenameIndex(
                name: "IX_VocabularyWord_VocabularyListID",
                table: "VocabularyWords",
                newName: "IX_VocabularyWords_VocabularyListID");

            migrationBuilder.RenameIndex(
                name: "IX_VocabularyWord_ChallengeID",
                table: "VocabularyWords",
                newName: "IX_VocabularyWords_ChallengeID");

            migrationBuilder.RenameIndex(
                name: "IX_ConversationChunk_ConversationID",
                table: "ConversationChunks",
                newName: "IX_ConversationChunks_ConversationID");

            migrationBuilder.RenameIndex(
                name: "IX_Challenge_UserActivityID",
                table: "Challenges",
                newName: "IX_Challenges_UserActivityID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_VocabularyWords",
                table: "VocabularyWords",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_VocabularyLists",
                table: "VocabularyLists",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserProfiles",
                table: "UserProfiles",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserActivities",
                table: "UserActivities",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StreamHistories",
                table: "StreamHistories",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Stories",
                table: "Stories",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SkillProfiles",
                table: "SkillProfiles",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SceneImages",
                table: "SceneImages",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ResourceVocabularyMappings",
                table: "ResourceVocabularyMappings",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_LearningResources",
                table: "LearningResources",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_GradeResponses",
                table: "GradeResponses",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConversationChunks",
                table: "ConversationChunks",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Conversations",
                table: "Conversations",
                column: "ID");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Challenges",
                table: "Challenges",
                column: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_Challenges_UserActivities_UserActivityID",
                table: "Challenges",
                column: "UserActivityID",
                principalTable: "UserActivities",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationChunks_Conversations_ConversationID",
                table: "ConversationChunks",
                column: "ConversationID",
                principalTable: "Conversations",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_VocabularyWords_Challenges_ChallengeID",
                table: "VocabularyWords",
                column: "ChallengeID",
                principalTable: "Challenges",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_VocabularyWords_VocabularyLists_VocabularyListID",
                table: "VocabularyWords",
                column: "VocabularyListID",
                principalTable: "VocabularyLists",
                principalColumn: "ID");
        }
    }
}
