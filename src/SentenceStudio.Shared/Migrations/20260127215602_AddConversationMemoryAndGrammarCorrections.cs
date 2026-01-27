using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationMemoryAndGrammarCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GrammarCorrectionsJson",
                table: "ConversationChunk",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConversationMemoryState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<int>(type: "INTEGER", nullable: false),
                    SerializedState = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationSummary = table.Column<string>(type: "TEXT", nullable: true),
                    DiscussedVocabulary = table.Column<string>(type: "TEXT", nullable: true),
                    DetectedProficiencyLevel = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMemoryState", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationMemoryState_Conversation_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMemoryState_ConversationId",
                table: "ConversationMemoryState",
                column: "ConversationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMemoryState");

            migrationBuilder.DropColumn(
                name: "GrammarCorrectionsJson",
                table: "ConversationChunk");
        }
    }
}
