using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachConversationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoachConversation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProtectedTitle = table.Column<string>(type: "text", nullable: false),
                    TitleSource = table.Column<int>(type: "integer", nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    HistoryStartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    MetadataSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    ContentProtectionVersion = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachConversation", x => x.Id);
                    table.UniqueConstraint("AK_CoachConversation_UserProfileId_Id", x => new { x.UserProfileId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "CoachMessage",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "text", nullable: false),
                    ContentSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    ContentProtectionVersion = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachMessage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoachMessage_CoachConversation_UserProfileId_ConversationId",
                        columns: x => new { x.UserProfileId, x.ConversationId },
                        principalTable: "CoachConversation",
                        principalColumns: new[] { "UserProfileId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CoachTurnOperation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKeyDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProtectedRequestDigest = table.Column<string>(type: "text", nullable: false),
                    ContentProtectionVersion = table.Column<int>(type: "integer", nullable: false),
                    BaseConversationVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FencingVersion = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false),
                    LearnerMessageSequence = table.Column<long>(type: "bigint", nullable: true),
                    FirstResponseSequence = table.Column<long>(type: "bigint", nullable: true),
                    LastResponseSequence = table.Column<long>(type: "bigint", nullable: true),
                    ProtectedOutcome = table.Column<string>(type: "text", nullable: true),
                    OutcomeSchemaVersion = table.Column<int>(type: "integer", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachTurnOperation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoachTurnOperation_CoachConversation_UserProfileId_Conversa~",
                        columns: x => new { x.UserProfileId, x.ConversationId },
                        principalTable: "CoachConversation",
                        principalColumns: new[] { "UserProfileId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoachConversation_UserProfileId",
                table: "CoachConversation",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_CoachConversation_UserProfileId_Status",
                table: "CoachConversation",
                columns: new[] { "UserProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachConversation_UserProfileId_UpdatedAt_Id",
                table: "CoachConversation",
                columns: new[] { "UserProfileId", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachMessage_UserProfileId_ConversationId_Sequence",
                table: "CoachMessage",
                columns: new[] { "UserProfileId", "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachMessage_UserProfileId_OperationId",
                table: "CoachMessage",
                columns: new[] { "UserProfileId", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachTurnOperation_LeaseExpiresAt",
                table: "CoachTurnOperation",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_CoachTurnOperation_UserProfileId_ConversationId_KeyDigest",
                table: "CoachTurnOperation",
                columns: new[] { "UserProfileId", "ConversationId", "IdempotencyKeyDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachTurnOperation_UserProfileId_ConversationId_Status",
                table: "CoachTurnOperation",
                columns: new[] { "UserProfileId", "ConversationId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoachMessage");

            migrationBuilder.DropTable(
                name: "CoachTurnOperation");

            migrationBuilder.DropTable(
                name: "CoachConversation");
        }
    }
}
