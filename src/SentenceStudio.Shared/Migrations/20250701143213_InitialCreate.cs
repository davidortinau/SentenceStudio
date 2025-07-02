using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "GradeResponses",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fluency = table.Column<double>(type: "REAL", nullable: false),
                    FluencyExplanation = table.Column<string>(type: "TEXT", nullable: true),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    AccuracyExplanation = table.Column<string>(type: "TEXT", nullable: true),
                    RecommendedTranslation = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChallengeID = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GradeResponses", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "LearningResources",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    MediaType = table.Column<string>(type: "TEXT", nullable: true),
                    MediaUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Transcript = table.Column<string>(type: "TEXT", nullable: true),
                    Translation = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    SkillID = table.Column<int>(type: "INTEGER", nullable: true),
                    OldVocabularyListID = table.Column<int>(type: "INTEGER", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearningResources", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "ResourceVocabularyMappings",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ResourceID = table.Column<int>(type: "INTEGER", nullable: false),
                    VocabularyWordID = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceVocabularyMappings", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "SceneImages",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    IsSelected = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneImages", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "SkillProfiles",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillProfiles", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "Stories",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListID = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillID = table.Column<int>(type: "INTEGER", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stories", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "StreamHistories",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Phrase = table.Column<string>(type: "TEXT", nullable: true),
                    Duration = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: true),
                    VoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    AudioFilePath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamHistories", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "UserActivities",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Activity = table.Column<string>(type: "TEXT", nullable: true),
                    Input = table.Column<string>(type: "TEXT", nullable: true),
                    Fluency = table.Column<double>(type: "REAL", nullable: false),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActivities", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    NativeLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    TargetLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    OpenAI_APIKey = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "VocabularyLists",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyLists", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "ConversationChunks",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationID = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    Participant = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationChunks", x => x.ID);
                    table.ForeignKey(
                        name: "FK_ConversationChunks_Conversations_ConversationID",
                        column: x => x.ConversationID,
                        principalTable: "Conversations",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Challenges",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SentenceText = table.Column<string>(type: "TEXT", nullable: true),
                    RecommendedTranslation = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VocabularyWord = table.Column<string>(type: "TEXT", nullable: true),
                    VocabularyWordAsUsed = table.Column<string>(type: "TEXT", nullable: true),
                    VocabularyWordGuesses = table.Column<string>(type: "TEXT", nullable: true),
                    UserActivityID = table.Column<int>(type: "INTEGER", nullable: true),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Challenges", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Challenges_UserActivities_UserActivityID",
                        column: x => x.UserActivityID,
                        principalTable: "UserActivities",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "VocabularyWords",
                columns: table => new
                {
                    ID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NativeLanguageTerm = table.Column<string>(type: "TEXT", nullable: true),
                    TargetLanguageTerm = table.Column<string>(type: "TEXT", nullable: true),
                    PartOfSpeech = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    ExampleSentence = table.Column<string>(type: "TEXT", nullable: true),
                    ExampleTranslation = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChallengeID = table.Column<int>(type: "INTEGER", nullable: true),
                    VocabularyListID = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyWords", x => x.ID);
                    table.ForeignKey(
                        name: "FK_VocabularyWords_Challenges_ChallengeID",
                        column: x => x.ChallengeID,
                        principalTable: "Challenges",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_VocabularyWords_VocabularyLists_VocabularyListID",
                        column: x => x.VocabularyListID,
                        principalTable: "VocabularyLists",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_UserActivityID",
                table: "Challenges",
                column: "UserActivityID");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationChunks_ConversationID",
                table: "ConversationChunks",
                column: "ConversationID");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWords_ChallengeID",
                table: "VocabularyWords",
                column: "ChallengeID");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyWords_VocabularyListID",
                table: "VocabularyWords",
                column: "VocabularyListID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationChunks");

            migrationBuilder.DropTable(
                name: "GradeResponses");

            migrationBuilder.DropTable(
                name: "LearningResources");

            migrationBuilder.DropTable(
                name: "ResourceVocabularyMappings");

            migrationBuilder.DropTable(
                name: "SceneImages");

            migrationBuilder.DropTable(
                name: "SkillProfiles");

            migrationBuilder.DropTable(
                name: "Stories");

            migrationBuilder.DropTable(
                name: "StreamHistories");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "VocabularyWords");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "Challenges");

            migrationBuilder.DropTable(
                name: "VocabularyLists");

            migrationBuilder.DropTable(
                name: "UserActivities");
        }
    }
}
