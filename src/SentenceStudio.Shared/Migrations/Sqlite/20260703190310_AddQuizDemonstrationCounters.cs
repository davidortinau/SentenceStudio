using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260703190310_AddQuizDemonstrationCounters")]
    public partial class AddQuizDemonstrationCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuizProductionDemonstrations",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QuizRecognitionDemonstrations",
                table: "VocabularyProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuizProductionDemonstrations",
                table: "VocabularyProgress");

            migrationBuilder.DropColumn(
                name: "QuizRecognitionDemonstrations",
                table: "VocabularyProgress");
        }
    }
}
