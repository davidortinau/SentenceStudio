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
