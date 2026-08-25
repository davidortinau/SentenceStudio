using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <summary>
    /// SQLite counterpart of the PostgreSQL AddConversationOwnerScope migration
    /// (identical id so both providers order it the same way).
    ///
    /// The [DbContext] + [Migration] attributes are mandatory: without them EF
    /// never discovers this file and MigrateAsync silently skips it on mobile,
    /// which is exactly how two earlier migrations shipped broken to devices.
    ///
    /// Nullable add-column plus indexes only — legacy rows stay null.
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserProfileId",
                table: "ConversationChunk",
                type: "TEXT",
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
