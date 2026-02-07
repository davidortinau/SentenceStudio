using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Conversation",
                type: "TEXT",
                nullable: false,
                defaultValue: "Korean");

            // Backfill existing conversations with "Korean"
            migrationBuilder.Sql(
                "UPDATE Conversation SET Language = 'Korean' WHERE Language IS NULL OR Language = ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "Conversation");
        }
    }
}
