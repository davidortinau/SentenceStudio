using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachWriteOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoachWriteAudit",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TurnId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ToolName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RiskClass = table.Column<int>(type: "integer", nullable: false),
                    Event = table.Column<int>(type: "integer", nullable: false),
                    EntityKind = table.Column<int>(type: "integer", nullable: false),
                    EntityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachWriteAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CoachWriteOperation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TurnId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ToolName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RiskClass = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    UndoKind = table.Column<int>(type: "integer", nullable: false),
                    EntityKind = table.Column<int>(type: "integer", nullable: false),
                    EntityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IdempotencyKeyDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ConfirmationDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ConfirmationExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProtectedArguments = table.Column<string>(type: "text", nullable: false),
                    ProtectedPriorState = table.Column<string>(type: "text", nullable: true),
                    ProtectedPreview = table.Column<string>(type: "text", nullable: false),
                    ProtectedReceipt = table.Column<string>(type: "text", nullable: true),
                    ContentProtectionVersion = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UndoExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UndoneAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UndoOperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachWriteOperation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoachWriteOperation_CoachConversation_UserProfileId_Convers~",
                        columns: x => new { x.UserProfileId, x.ConversationId },
                        principalTable: "CoachConversation",
                        principalColumns: new[] { "UserProfileId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoachWriteAudit_UserProfileId_CreatedAtUtc",
                table: "CoachWriteAudit",
                columns: new[] { "UserProfileId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachWriteAudit_UserProfileId_OperationId",
                table: "CoachWriteAudit",
                columns: new[] { "UserProfileId", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachWriteOperation_ExpiresAtUtc",
                table: "CoachWriteOperation",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CoachWriteOperation_UserProfileId_ConversationId_KeyDigest",
                table: "CoachWriteOperation",
                columns: new[] { "UserProfileId", "ConversationId", "IdempotencyKeyDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachWriteOperation_UserProfileId_ConversationId_Status",
                table: "CoachWriteOperation",
                columns: new[] { "UserProfileId", "ConversationId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoachWriteAudit");

            migrationBuilder.DropTable(
                name: "CoachWriteOperation");
        }
    }
}
