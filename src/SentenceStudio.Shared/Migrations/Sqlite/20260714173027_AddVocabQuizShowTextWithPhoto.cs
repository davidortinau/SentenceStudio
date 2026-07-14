using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260714173027_AddVocabQuizShowTextWithPhoto")]
    public partial class AddVocabQuizShowTextWithPhoto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VocabQuizShowTextWithPhoto",
                table: "UserProfile",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VocabQuizShowTextWithPhoto",
                table: "UserProfile");
        }
    }
}
