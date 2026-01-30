using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationChunkRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add Role column - for fresh databases this will work
            // For upgraded databases that already had the column, this migration may fail
            // but EF tracks it in __EFMigrationsHistory, so it won't be re-run
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "ConversationChunk",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Role",
                table: "ConversationChunk");
        }
    }
}
