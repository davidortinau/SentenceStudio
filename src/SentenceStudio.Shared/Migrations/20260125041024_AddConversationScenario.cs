using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationScenario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScenarioId",
                table: "Conversation",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConversationScenario",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NameKorean = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PersonaName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PersonaDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SituationDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ConversationType = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestionBank = table.Column<string>(type: "TEXT", nullable: true),
                    IsPredefined = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationScenario", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversation_ScenarioId",
                table: "Conversation",
                column: "ScenarioId");

            migrationBuilder.AddForeignKey(
                name: "FK_Conversation_ConversationScenario_ScenarioId",
                table: "Conversation",
                column: "ScenarioId",
                principalTable: "ConversationScenario",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversation_ConversationScenario_ScenarioId",
                table: "Conversation");

            migrationBuilder.DropTable(
                name: "ConversationScenario");

            migrationBuilder.DropIndex(
                name: "IX_Conversation_ScenarioId",
                table: "Conversation");

            migrationBuilder.DropColumn(
                name: "ScenarioId",
                table: "Conversation");
        }
    }
}
