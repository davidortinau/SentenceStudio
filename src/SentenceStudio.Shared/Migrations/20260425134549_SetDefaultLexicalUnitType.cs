using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class SetDefaultLexicalUnitType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Heuristic backfill: classify existing Unknown (0) entries as
            // Word (1) or Phrase (2) based on whether the term contains a space.
            // Captain D1 directive: TRIM first, then check for space.
            migrationBuilder.Sql(
                """
                UPDATE "VocabularyWord"
                SET "LexicalUnitType" = CASE
                    WHEN POSITION(' ' IN TRIM("TargetLanguageTerm")) > 0 THEN 2
                    ELSE 1
                END
                WHERE "LexicalUnitType" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Safe no-op: resetting to Unknown would be data loss (Captain D1).
        }
    }
}
