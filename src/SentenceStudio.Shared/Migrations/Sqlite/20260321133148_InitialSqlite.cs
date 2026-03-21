using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserProfileId = table.Column<string>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Challenge",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentenceText = table.Column<string>(type: "TEXT", nullable: true),
                    RecommendedTranslation = table.Column<string>(type: "TEXT", nullable: true),
                    VocabularyWord = table.Column<string>(type: "TEXT", nullable: true),
                    VocabularyWordAsUsed = table.Column<string>(type: "TEXT", nullable: true),
                    VocabularyWordGuesses = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Challenge", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConversationScenario",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NameKorean = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PersonaName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PersonaDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SituationDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ConversationType = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestionBank = table.Column<string>(type: "TEXT", nullable: true),
                    IsPredefined = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationScenario", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyPlanCompletion",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PlanItemId = table.Column<string>(type: "TEXT", nullable: false),
                    ActivityType = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: true),
                    SkillId = table.Column<string>(type: "TEXT", nullable: true),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MinutesSpent = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimatedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    TitleKey = table.Column<string>(type: "TEXT", nullable: false),
                    DescriptionKey = table.Column<string>(type: "TEXT", nullable: false),
                    Rationale = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyPlanCompletion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GradeResponse",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fluency = table.Column<double>(type: "REAL", nullable: false),
                    FluencyExplanation = table.Column<string>(type: "TEXT", nullable: true),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    AccuracyExplanation = table.Column<string>(type: "TEXT", nullable: true),
                    RecommendedTranslation = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChallengeID = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GradeResponse", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LearningResource",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SkillID = table.Column<string>(type: "TEXT", nullable: true),
                    OldVocabularyListID = table.Column<string>(type: "TEXT", nullable: true),
                    IsSmartResource = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    MediaType = table.Column<string>(type: "TEXT", nullable: true),
                    MediaUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Transcript = table.Column<string>(type: "TEXT", nullable: true),
                    Translation = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    SmartResourceType = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearningResource", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MinimalPairSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    PlannedTrialCount = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinimalPairSession", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SceneImage",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    IsSelected = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneImage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SkillProfile",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillProfile", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Story",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListID = table.Column<string>(type: "TEXT", nullable: false),
                    SkillID = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Story", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StreamHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Phrase = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Duration = table.Column<double>(type: "REAL", nullable: false),
                    VoiceId = table.Column<string>(type: "TEXT", nullable: true),
                    AudioFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserActivity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Activity = table.Column<string>(type: "TEXT", nullable: true),
                    Input = table.Column<string>(type: "TEXT", nullable: true),
                    Fluency = table.Column<double>(type: "REAL", nullable: false),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    UserProfileId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActivity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfile",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    NativeLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    TargetLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    TargetLanguages = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    OpenAI_APIKey = table.Column<string>(type: "TEXT", nullable: true),
                    PreferredSessionMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetCEFRLevel = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfile", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VocabularyList",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyList", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VocabularyWord",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NativeLanguageTerm = table.Column<string>(type: "TEXT", nullable: true),
                    TargetLanguageTerm = table.Column<string>(type: "TEXT", nullable: true),
                    Lemma = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    MnemonicText = table.Column<string>(type: "TEXT", nullable: true),
                    MnemonicImageUri = table.Column<string>(type: "TEXT", nullable: true),
                    AudioPronunciationUri = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyWord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WordAssociationScore",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    RoundScore = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalClues = table.Column<int>(type: "INTEGER", nullable: false),
                    WordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WordIds = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordAssociationScore", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Conversation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    ScenarioId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversation_ConversationScenario_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "ConversationScenario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ExampleSentence",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VocabularyWordId = table.Column<string>(type: "TEXT", nullable: false),
                    LearningResourceId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetSentence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    NativeSentence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AudioUri = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsCore = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExampleSentence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExampleSentence_LearningResource_LearningResourceId",
                        column: x => x.LearningResourceId,
                        principalTable: "LearningResource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExampleSentence_VocabularyWord_VocabularyWordId",
                        column: x => x.VocabularyWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MinimalPair",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    VocabularyWordAId = table.Column<string>(type: "TEXT", nullable: false),
                    VocabularyWordBId = table.Column<string>(type: "TEXT", nullable: false),
                    ContrastLabel = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinimalPair", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MinimalPair_VocabularyWord_VocabularyWordAId",
                        column: x => x.VocabularyWordAId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MinimalPair_VocabularyWord_VocabularyWordBId",
                        column: x => x.VocabularyWordBId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResourceVocabularyMapping",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    VocabularyWordId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceVocabularyMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourceVocabularyMapping_LearningResource_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "LearningResource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceVocabularyMapping_VocabularyWord_VocabularyWordId",
                        column: x => x.VocabularyWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VocabularyProgress",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    VocabularyWordId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    MasteryScore = table.Column<float>(type: "REAL", nullable: false),
                    TotalAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CorrectAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentStreak = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductionInStreak = table.Column<int>(type: "INTEGER", nullable: false),
                    RecognitionAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    RecognitionCorrect = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductionAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductionCorrect = table.Column<int>(type: "INTEGER", nullable: false),
                    ApplicationAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ApplicationCorrect = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentPhase = table.Column<int>(type: "INTEGER", nullable: false),
                    NextReviewDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewInterval = table.Column<int>(type: "INTEGER", nullable: false),
                    EaseFactor = table.Column<float>(type: "REAL", nullable: false),
                    MultipleChoiceCorrect = table.Column<int>(type: "INTEGER", nullable: false),
                    TextEntryCorrect = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPromoted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsUserDeclared = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserDeclaredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VerificationState = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPracticedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MasteredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VocabularyProgress_VocabularyWord_VocabularyWordId",
                        column: x => x.VocabularyWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationChunk",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SentTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: true),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    GrammarCorrectionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Comprehension = table.Column<double>(type: "REAL", nullable: false),
                    ComprehensionNotes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationChunk", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationChunk_Conversation_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMemoryState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    SerializedState = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationSummary = table.Column<string>(type: "TEXT", nullable: true),
                    DiscussedVocabulary = table.Column<string>(type: "TEXT", nullable: true),
                    DetectedProficiencyLevel = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMemoryState", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationMemoryState_Conversation_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MinimalPairAttempt",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: false),
                    PairId = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptWordId = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedWordId = table.Column<string>(type: "TEXT", nullable: false),
                    IsCorrect = table.Column<bool>(type: "INTEGER", nullable: false),
                    SequenceNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinimalPairAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MinimalPairAttempt_MinimalPairSession_SessionId",
                        column: x => x.SessionId,
                        principalTable: "MinimalPairSession",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MinimalPairAttempt_MinimalPair_PairId",
                        column: x => x.PairId,
                        principalTable: "MinimalPair",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MinimalPairAttempt_VocabularyWord_PromptWordId",
                        column: x => x.PromptWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MinimalPairAttempt_VocabularyWord_SelectedWordId",
                        column: x => x.SelectedWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VocabularyLearningContext",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    VocabularyProgressId = table.Column<string>(type: "TEXT", nullable: false),
                    LearningResourceId = table.Column<string>(type: "TEXT", nullable: true),
                    Activity = table.Column<string>(type: "TEXT", nullable: false),
                    InputMode = table.Column<string>(type: "TEXT", nullable: false),
                    WasCorrect = table.Column<bool>(type: "INTEGER", nullable: false),
                    DifficultyScore = table.Column<float>(type: "REAL", nullable: false),
                    ResponseTimeMs = table.Column<int>(type: "INTEGER", nullable: false),
                    UserConfidence = table.Column<float>(type: "REAL", nullable: true),
                    ContextType = table.Column<string>(type: "TEXT", nullable: true),
                    UserInput = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedAnswer = table.Column<string>(type: "TEXT", nullable: true),
                    LearnedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CorrectAnswersInContext = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VocabularyLearningContext", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VocabularyLearningContext_LearningResource_LearningResourceId",
                        column: x => x.LearningResourceId,
                        principalTable: "LearningResource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VocabularyLearningContext_VocabularyProgress_VocabularyProgressId",
                        column: x => x.VocabularyProgressId,
                        principalTable: "VocabularyProgress",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversation_ScenarioId",
                table: "Conversation",
                column: "ScenarioId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationChunk_ConversationId",
                table: "ConversationChunk",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMemoryState_ConversationId",
                table: "ConversationMemoryState",
                column: "ConversationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_LearningResourceId",
                table: "ExampleSentence",
                column: "LearningResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_VocabularyWordId",
                table: "ExampleSentence",
                column: "VocabularyWordId");

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPair_UserId_VocabularyWordAId_VocabularyWordBId",
                table: "MinimalPair",
                columns: new[] { "UserId", "VocabularyWordAId", "VocabularyWordBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPair_VocabularyWordAId",
                table: "MinimalPair",
                column: "VocabularyWordAId");

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPair_VocabularyWordBId",
                table: "MinimalPair",
                column: "VocabularyWordBId");

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPairAttempt_PairId_CreatedAt",
                table: "MinimalPairAttempt",
                columns: new[] { "PairId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPairAttempt_PromptWordId",
                table: "MinimalPairAttempt",
                column: "PromptWordId");

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPairAttempt_SelectedWordId",
                table: "MinimalPairAttempt",
                column: "SelectedWordId");

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPairAttempt_SessionId_SequenceNumber",
                table: "MinimalPairAttempt",
                columns: new[] { "SessionId", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_Token",
                table: "RefreshTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceVocabularyMapping_ResourceId",
                table: "ResourceVocabularyMapping",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceVocabularyMapping_VocabularyWordId",
                table: "ResourceVocabularyMapping",
                column: "VocabularyWordId");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyLearningContext_LearningResourceId",
                table: "VocabularyLearningContext",
                column: "LearningResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyLearningContext_VocabularyProgressId",
                table: "VocabularyLearningContext",
                column: "VocabularyProgressId");

            migrationBuilder.CreateIndex(
                name: "IX_VocabularyProgress_VocabularyWordId_UserId",
                table: "VocabularyProgress",
                columns: new[] { "VocabularyWordId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "Challenge");

            migrationBuilder.DropTable(
                name: "ConversationChunk");

            migrationBuilder.DropTable(
                name: "ConversationMemoryState");

            migrationBuilder.DropTable(
                name: "DailyPlanCompletion");

            migrationBuilder.DropTable(
                name: "ExampleSentence");

            migrationBuilder.DropTable(
                name: "GradeResponse");

            migrationBuilder.DropTable(
                name: "MinimalPairAttempt");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "ResourceVocabularyMapping");

            migrationBuilder.DropTable(
                name: "SceneImage");

            migrationBuilder.DropTable(
                name: "SkillProfile");

            migrationBuilder.DropTable(
                name: "Story");

            migrationBuilder.DropTable(
                name: "StreamHistory");

            migrationBuilder.DropTable(
                name: "UserActivity");

            migrationBuilder.DropTable(
                name: "UserProfile");

            migrationBuilder.DropTable(
                name: "VocabularyLearningContext");

            migrationBuilder.DropTable(
                name: "VocabularyList");

            migrationBuilder.DropTable(
                name: "WordAssociationScore");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "Conversation");

            migrationBuilder.DropTable(
                name: "MinimalPairSession");

            migrationBuilder.DropTable(
                name: "MinimalPair");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "LearningResource");

            migrationBuilder.DropTable(
                name: "VocabularyProgress");

            migrationBuilder.DropTable(
                name: "ConversationScenario");

            migrationBuilder.DropTable(
                name: "VocabularyWord");
        }
    }
}
