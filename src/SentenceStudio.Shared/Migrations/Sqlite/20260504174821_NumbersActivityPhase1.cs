using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
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
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultSystem = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberContext", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberCounter",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    LanguageCode = table.Column<string>(type: "TEXT", nullable: false),
                    Counter = table.Column<string>(type: "TEXT", nullable: false),
                    Romanization = table.Column<string>(type: "TEXT", nullable: false),
                    MeaningEn = table.Column<string>(type: "TEXT", nullable: false),
                    System = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberCounter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberSubMode",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSubMode", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberMasteryProgress",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    LanguageCode = table.Column<string>(type: "TEXT", nullable: false),
                    ContextCode = table.Column<string>(type: "TEXT", nullable: false),
                    CounterId = table.Column<string>(type: "TEXT", nullable: true),
                    System = table.Column<string>(type: "TEXT", nullable: false),
                    Bucket = table.Column<string>(type: "TEXT", nullable: false),
                    CorrectCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MedianLatencyMs = table.Column<int>(type: "INTEGER", nullable: false),
                    EaseFactor = table.Column<double>(type: "REAL", nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    Repetitions = table.Column<int>(type: "INTEGER", nullable: false),
                    DueDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastReviewed = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberMasteryProgress", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberAttempt",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    LanguageCode = table.Column<string>(type: "TEXT", nullable: false),
                    ContextCode = table.Column<string>(type: "TEXT", nullable: false),
                    SubModeCode = table.Column<string>(type: "TEXT", nullable: false),
                    CounterId = table.Column<string>(type: "TEXT", nullable: true),
                    System = table.Column<string>(type: "TEXT", nullable: false),
                    Bucket = table.Column<string>(type: "TEXT", nullable: false),
                    PromptValue = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectedAnswer = table.Column<string>(type: "TEXT", nullable: false),
                    UserAnswer = table.Column<string>(type: "TEXT", nullable: true),
                    IsCorrect = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorClass = table.Column<string>(type: "TEXT", nullable: true),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
