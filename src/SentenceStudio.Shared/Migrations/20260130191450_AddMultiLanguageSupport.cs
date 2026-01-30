using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiLanguageSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add TargetLanguages column to UserProfile for multi-language support
            migrationBuilder.AddColumn<string>(
                name: "TargetLanguages",
                table: "UserProfile",
                type: "TEXT",
                nullable: true);
            
            // Migrate existing resources: set Language to "Korean" where null
            // This preserves the assumption that all existing resources are Korean
            migrationBuilder.Sql(
                "UPDATE LearningResource SET Language = 'Korean' WHERE Language IS NULL OR Language = ''");
            
            // Migrate existing user profiles: populate TargetLanguages from TargetLanguage
            migrationBuilder.Sql(
                "UPDATE UserProfile SET TargetLanguages = TargetLanguage WHERE TargetLanguages IS NULL AND TargetLanguage IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetLanguages",
                table: "UserProfile");
        }
    }
}
