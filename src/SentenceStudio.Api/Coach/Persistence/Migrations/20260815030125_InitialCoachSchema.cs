using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCoachSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoachPlanRevision",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    IntentKind = table.Column<int>(type: "integer", nullable: false),
                    AcceptedConstraintDeltaJson = table.Column<string>(type: "jsonb", nullable: false),
                    BeforePlanVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AfterPlanVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BeforePlanHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AfterPlanHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BeforePlanSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    AfterPlanSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    PreservedCompletedCount = table.Column<int>(type: "integer", nullable: false),
                    PreservedInProgressCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsUndone = table.Column<bool>(type: "boolean", nullable: false),
                    UndoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UndoneByRevisionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachPlanRevision", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CoachSession",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentImplementation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AgentName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AgentConfigVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SessionSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    ProtectedAgentSession = table.Column<string>(type: "text", nullable: true),
                    ActiveConstraintsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PendingSuggestionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PendingSuggestionDeltaJson = table.Column<string>(type: "jsonb", nullable: true),
                    PendingSuggestionCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TurnCount = table.Column<int>(type: "integer", nullable: false),
                    ClarificationCount = table.Column<int>(type: "integer", nullable: false),
                    RevisionCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StopReason = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachSession", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CoachUsage",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    WeekKey = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RunCount = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachUsage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoachPlanRevision_CreatedAt",
                table: "CoachPlanRevision",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CoachPlanRevision_UserProfileId",
                table: "CoachPlanRevision",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_CoachPlanRevision_UserProfileId_SessionId_RevisionNumber",
                table: "CoachPlanRevision",
                columns: new[] { "UserProfileId", "SessionId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachSession_ExpiresAt",
                table: "CoachSession",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_CoachSession_UserProfileId",
                table: "CoachSession",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_CoachSession_UserProfileId_Status",
                table: "CoachSession",
                columns: new[] { "UserProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachUsage_UserProfileId_LocalDate",
                table: "CoachUsage",
                columns: new[] { "UserProfileId", "LocalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachUsage_UserProfileId_WeekKey",
                table: "CoachUsage",
                columns: new[] { "UserProfileId", "WeekKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoachPlanRevision");

            migrationBuilder.DropTable(
                name: "CoachSession");

            migrationBuilder.DropTable(
                name: "CoachUsage");
        }
    }
}
