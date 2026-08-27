using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <summary>
    /// Gives the legacy Conversation activity an owner.
    ///
    /// Nullable add-column plus indexes only. Rows written before owner scoping
    /// existed stay null on purpose: there is no trustworthy signal for who they
    /// belong to, and a backfill here would be a guess that hands one learner's
    /// transcript to another. Ownerless rows are simply invisible to every user
    /// (see ConversationRepository) and are reported only as an operator
    /// diagnostic count. No backfill, no raw DDL, no destructive statements.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260817021500_AddConversationOwnerScope")]
    public partial class AddConversationOwnerScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserProfileId",
                table: "Conversation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserProfileId",
                table: "ConversationChunk",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversation_UserProfileId",
                table: "Conversation",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversation_UserProfileId_CreatedAt",
                table: "Conversation",
                columns: new[] { "UserProfileId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationChunk_UserProfileId",
                table: "ConversationChunk",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationChunk_UserProfileId_ConversationId",
                table: "ConversationChunk",
                columns: new[] { "UserProfileId", "ConversationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationChunk_UserProfileId_ConversationId",
                table: "ConversationChunk");

            migrationBuilder.DropIndex(
                name: "IX_ConversationChunk_UserProfileId",
                table: "ConversationChunk");

            migrationBuilder.DropIndex(
                name: "IX_Conversation_UserProfileId_CreatedAt",
                table: "Conversation");

            migrationBuilder.DropIndex(
                name: "IX_Conversation_UserProfileId",
                table: "Conversation");

            migrationBuilder.DropColumn(
                name: "UserProfileId",
                table: "ConversationChunk");

            migrationBuilder.DropColumn(
                name: "UserProfileId",
                table: "Conversation");
        }
    }
}
