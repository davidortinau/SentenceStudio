using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <summary>
    /// Persists the part of speech the extraction pipeline already produced.
    /// Nullable add-column only: every existing row stays null ("never
    /// classified"), which remains a valid state. No backfill and no
    /// destructive DDL here — backfill is a separate, resumable service.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260815221600_AddVocabularyPartOfSpeech")]
    public partial class AddVocabularyPartOfSpeech : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PartOfSpeech",
                table: "VocabularyWord",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PartOfSpeech",
                table: "VocabularyWord");
        }
    }
}
