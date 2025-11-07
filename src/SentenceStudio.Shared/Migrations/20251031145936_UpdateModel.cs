using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class UpdateModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ConversationChunk_ConversationId",
                table: "ConversationChunk",
                column: "ConversationId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationChunk_Conversation_ConversationId",
                table: "ConversationChunk",
                column: "ConversationId",
                principalTable: "Conversation",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConversationChunk_Conversation_ConversationId",
                table: "ConversationChunk");

            migrationBuilder.DropIndex(
                name: "IX_ConversationChunk_ConversationId",
                table: "ConversationChunk");
        }
    }
}
