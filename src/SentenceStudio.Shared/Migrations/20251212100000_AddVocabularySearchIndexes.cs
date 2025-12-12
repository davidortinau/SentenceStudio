using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularySearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // T005/T006: Create indexes for vocabulary search syntax feature
            // These indexes optimize the GitHub-style search queries:
            // - tag:value (Tags column)
            // - lemma:value (Lemma column)
            // - free-text search (TargetLanguageTerm, NativeLanguageTerm)

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS idx_vocabulary_tags ON VocabularyWord(Tags);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS idx_vocabulary_lemma ON VocabularyWord(Lemma);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS idx_vocabulary_target ON VocabularyWord(TargetLanguageTerm);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS idx_vocabulary_native ON VocabularyWord(NativeLanguageTerm);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_vocabulary_tags;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_vocabulary_lemma;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_vocabulary_target;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_vocabulary_native;");
        }
    }
}
