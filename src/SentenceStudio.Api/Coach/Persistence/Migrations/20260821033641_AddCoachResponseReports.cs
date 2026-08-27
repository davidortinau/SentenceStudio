using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachResponseReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoachResponseReport",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CoachMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CoachMessageSequence = table.Column<long>(type: "bigint", nullable: false),
                    RequestMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestMessageSequence = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    ResponseKind = table.Column<int>(type: "integer", nullable: false),
                    TurnOperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TurnStatus = table.Column<int>(type: "integer", nullable: true),
                    StopReason = table.Column<int>(type: "integer", nullable: true),
                    TurnAttemptCount = table.Column<int>(type: "integer", nullable: true),
                    TurnErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InvokedToolNames = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    WriteOperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    WriteStatus = table.Column<int>(type: "integer", nullable: true),
                    WriteFailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OpportunityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachResponseReport", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoachResponseReport_ReportedAtUtc",
                table: "CoachResponseReport",
                column: "ReportedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CoachResponseReport_UserProfileId_CoachMessageId",
                table: "CoachResponseReport",
                columns: new[] { "UserProfileId", "CoachMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachResponseReport_UserProfileId_ConversationId",
                table: "CoachResponseReport",
                columns: new[] { "UserProfileId", "ConversationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoachResponseReport");
        }
    }
}
