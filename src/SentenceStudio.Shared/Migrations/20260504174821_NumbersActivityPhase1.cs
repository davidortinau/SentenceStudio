using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class NumbersActivityPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NumberContext",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Icon = table.Column<string>(type: "text", nullable: false),
                    DefaultSystem = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberContext", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberCounter",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    LanguageCode = table.Column<string>(type: "text", nullable: false),
                    Counter = table.Column<string>(type: "text", nullable: false),
                    Romanization = table.Column<string>(type: "text", nullable: false),
                    MeaningEn = table.Column<string>(type: "text", nullable: false),
                    System = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberCounter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberSubMode",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Phase = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSubMode", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberMasteryProgress",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserProfileId = table.Column<int>(type: "integer", nullable: false),
                    LanguageCode = table.Column<string>(type: "text", nullable: false),
                    ContextCode = table.Column<string>(type: "text", nullable: false),
                    CounterId = table.Column<string>(type: "text", nullable: true),
                    System = table.Column<string>(type: "text", nullable: false),
                    Bucket = table.Column<string>(type: "text", nullable: false),
                    CorrectCount = table.Column<int>(type: "integer", nullable: false),
                    TotalCount = table.Column<int>(type: "integer", nullable: false),
                    MedianLatencyMs = table.Column<int>(type: "integer", nullable: false),
                    EaseFactor = table.Column<double>(type: "double precision", nullable: false),
                    Interval = table.Column<int>(type: "integer", nullable: false),
                    Repetitions = table.Column<int>(type: "integer", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReviewed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberMasteryProgress", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberAttempt",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserProfileId = table.Column<int>(type: "integer", nullable: false),
                    LanguageCode = table.Column<string>(type: "text", nullable: false),
                    ContextCode = table.Column<string>(type: "text", nullable: false),
                    SubModeCode = table.Column<string>(type: "text", nullable: false),
                    CounterId = table.Column<string>(type: "text", nullable: true),
                    System = table.Column<string>(type: "text", nullable: false),
                    Bucket = table.Column<string>(type: "text", nullable: false),
                    PromptValue = table.Column<string>(type: "text", nullable: false),
                    ExpectedAnswer = table.Column<string>(type: "text", nullable: false),
                    UserAnswer = table.Column<string>(type: "text", nullable: true),
                    IsCorrect = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorClass = table.Column<string>(type: "text", nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberAttempt", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NumberContext_Code",
                table: "NumberContext",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NumberSubMode_Code",
                table: "NumberSubMode",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NumberMasteryProgress_UserProfileId",
                table: "NumberMasteryProgress",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_NumberMasteryProgress_UserProfileId_LanguageCode_ContextCode_CounterId_System_Bucket",
                table: "NumberMasteryProgress",
                columns: new[] { "UserProfileId", "LanguageCode", "ContextCode", "CounterId", "System", "Bucket" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NumberAttempt_UserProfileId",
                table: "NumberAttempt",
                column: "UserProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NumberContext");

            migrationBuilder.DropTable(
                name: "NumberCounter");

            migrationBuilder.DropTable(
                name: "NumberSubMode");

            migrationBuilder.DropTable(
                name: "NumberMasteryProgress");

            migrationBuilder.DropTable(
                name: "NumberAttempt");
        }
    }
}
