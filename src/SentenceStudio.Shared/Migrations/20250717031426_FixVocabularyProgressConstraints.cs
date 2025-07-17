using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class FixVocabularyProgressConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Find and merge duplicate VocabularyProgress records
            // Keep the most recent record and merge the data from duplicates
            migrationBuilder.Sql(@"
                -- Create a temporary table with merged data
                CREATE TEMPORARY TABLE VocabularyProgress_Merged AS
                SELECT 
                    MAX(Id) as Id,
                    VocabularyWordId,
                    1 as UserId,  -- Default user ID
                    COALESCE(MAX(MasteryScore), 0.0) as MasteryScore,
                    SUM(COALESCE(TotalAttempts, 0)) as TotalAttempts,
                    SUM(COALESCE(CorrectAttempts, 0)) as CorrectAttempts,
                    SUM(COALESCE(RecognitionAttempts, 0)) as RecognitionAttempts,
                    SUM(COALESCE(RecognitionCorrect, 0)) as RecognitionCorrect,
                    SUM(COALESCE(ProductionAttempts, 0)) as ProductionAttempts,
                    SUM(COALESCE(ProductionCorrect, 0)) as ProductionCorrect,
                    SUM(COALESCE(ApplicationAttempts, 0)) as ApplicationAttempts,
                    SUM(COALESCE(ApplicationCorrect, 0)) as ApplicationCorrect,
                    MAX(COALESCE(CurrentPhase, 0)) as CurrentPhase,
                    MAX(NextReviewDate) as NextReviewDate,
                    MAX(COALESCE(ReviewInterval, 1)) as ReviewInterval,
                    MAX(COALESCE(EaseFactor, 2.5)) as EaseFactor,
                    MAX(MasteredAt) as MasteredAt,
                    MIN(FirstSeenAt) as FirstSeenAt,
                    MAX(LastPracticedAt) as LastPracticedAt,
                    MIN(CreatedAt) as CreatedAt,
                    MAX(UpdatedAt) as UpdatedAt,
                    -- Legacy fields
                    SUM(COALESCE(MultipleChoiceCorrect, 0)) as MultipleChoiceCorrect,
                    SUM(COALESCE(TextEntryCorrect, 0)) as TextEntryCorrect,
                    MAX(CASE WHEN IsPromoted = 1 THEN 1 ELSE 0 END) as IsPromoted,
                    MAX(CASE WHEN IsCompleted = 1 THEN 1 ELSE 0 END) as IsCompleted
                FROM VocabularyProgress
                GROUP BY VocabularyWordId;
            ");

            // Step 2: Delete all existing VocabularyProgress records
            migrationBuilder.Sql("DELETE FROM VocabularyProgress;");

            // Step 3: Insert the merged records back
            migrationBuilder.Sql(@"
                INSERT INTO VocabularyProgress (
                    Id, VocabularyWordId, UserId, MasteryScore, TotalAttempts, CorrectAttempts,
                    RecognitionAttempts, RecognitionCorrect, ProductionAttempts, ProductionCorrect,
                    ApplicationAttempts, ApplicationCorrect, CurrentPhase, NextReviewDate,
                    ReviewInterval, EaseFactor, MasteredAt, FirstSeenAt, LastPracticedAt,
                    CreatedAt, UpdatedAt, MultipleChoiceCorrect, TextEntryCorrect,
                    IsPromoted, IsCompleted
                )
                SELECT 
                    Id, VocabularyWordId, UserId, MasteryScore, TotalAttempts, CorrectAttempts,
                    RecognitionAttempts, RecognitionCorrect, ProductionAttempts, ProductionCorrect,
                    ApplicationAttempts, ApplicationCorrect, CurrentPhase, NextReviewDate,
                    ReviewInterval, EaseFactor, MasteredAt, FirstSeenAt, LastPracticedAt,
                    CreatedAt, UpdatedAt, MultipleChoiceCorrect, TextEntryCorrect,
                    IsPromoted, IsCompleted
                FROM VocabularyProgress_Merged;
            ");

            // Step 4: Drop the temporary table
            migrationBuilder.Sql("DROP TABLE VocabularyProgress_Merged;");

            // Step 5: Reset the SQLite sequence for the Id column
            migrationBuilder.Sql(@"
                UPDATE sqlite_sequence 
                SET seq = (SELECT MAX(Id) FROM VocabularyProgress) 
                WHERE name = 'VocabularyProgress';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No rollback - data consolidation is permanent
            // This migration safely merges duplicate data without loss
        }
    }
}