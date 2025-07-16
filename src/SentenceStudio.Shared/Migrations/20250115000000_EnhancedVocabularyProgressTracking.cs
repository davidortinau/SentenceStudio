using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class EnhancedVocabularyProgressTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop existing unique index
            migrationBuilder.DropIndex(
                name: "IX_VocabularyProgress_VocabularyWordId",
                table: "VocabularyProgress");

            // Add new columns to VocabularyProgress
            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<float>(
                name: "MasteryScore",
                table: "VocabularyProgress",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<int>(
                name: "TotalAttempts",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CorrectAttempts",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecognitionAttempts",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecognitionCorrect",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProductionAttempts",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProductionCorrect",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ApplicationAttempts",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ApplicationCorrect",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CurrentPhase",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "NextReviewDate",
                table: "VocabularyProgress",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewInterval",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<float>(
                name: "EaseFactor",
                table: "VocabularyProgress",
                type: "REAL",
                nullable: false,
                defaultValue: 2.5f);

            migrationBuilder.AddColumn<string>(
                name: "MasteredAt",
                table: "VocabularyProgress",
                type: "TEXT",
                nullable: true);

            // Add new columns to VocabularyLearningContext
            migrationBuilder.AddColumn<bool>(
                name: "WasCorrect",
                table: "VocabularyLearningContext",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<float>(
                name: "DifficultyScore",
                table: "VocabularyLearningContext",
                type: "REAL",
                nullable: false,
                defaultValue: 0.5f);

            migrationBuilder.AddColumn<int>(
                name: "ResponseTimeMs",
                table: "VocabularyLearningContext",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "UserConfidence",
                table: "VocabularyLearningContext",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContextType",
                table: "VocabularyLearningContext",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserInput",
                table: "VocabularyLearningContext",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedAnswer",
                table: "VocabularyLearningContext",
                type: "TEXT",
                nullable: true);

            // Create new unique index for VocabularyWordId and UserId
            migrationBuilder.CreateIndex(
                name: "IX_VocabularyProgress_VocabularyWordId_UserId",
                table: "VocabularyProgress",
                columns: new[] { "VocabularyWordId", "UserId" },
                unique: true);

            // Update existing records to populate WasCorrect based on CorrectAnswersInContext
            migrationBuilder.Sql(
                "UPDATE VocabularyLearningContext SET WasCorrect = CASE WHEN CorrectAnswersInContext > 0 THEN 1 ELSE 0 END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the new unique index
            migrationBuilder.DropIndex(
                name: "IX_VocabularyProgress_VocabularyWordId_UserId",
                table: "VocabularyProgress");

            // Remove new columns from VocabularyProgress
            migrationBuilder.DropColumn(
                name: "UserId",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "MasteryScore",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "TotalAttempts",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "CorrectAttempts",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "RecognitionAttempts",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "RecognitionCorrect",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "ProductionAttempts",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "ProductionCorrect",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "ApplicationAttempts",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "ApplicationCorrect",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "CurrentPhase",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "NextReviewDate",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "ReviewInterval",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "EaseFactor",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "MasteredAt",
                table: "VocabularyProgress");

            // Remove new columns from VocabularyLearningContext
            migrationBuilder.DropColumn(
                name: "WasCorrect",
                table: "VocabularyLearningContext");

            migrationBuilder.DropColumn(
                name: "DifficultyScore",
                table: "VocabularyLearningContext");

            migrationBuilder.DropColumn(
                name: "ResponseTimeMs",
                table: "VocabularyLearningContext");

            migrationBuilder.DropColumn(
                name: "UserConfidence",
                table: "VocabularyLearningContext");

            migrationBuilder.DropColumn(
                name: "ContextType",
                table: "VocabularyLearningContext");

            migrationBuilder.DropColumn(
                name: "UserInput",
                table: "VocabularyLearningContext");

            migrationBuilder.DropColumn(
                name: "ExpectedAnswer",
                table: "VocabularyLearningContext");

            // Recreate the original unique index
            migrationBuilder.CreateIndex(
                name: "IX_VocabularyProgress_VocabularyWordId",
                table: "VocabularyProgress",
                column: "VocabularyWordId",
                unique: true);
        }
    }
}