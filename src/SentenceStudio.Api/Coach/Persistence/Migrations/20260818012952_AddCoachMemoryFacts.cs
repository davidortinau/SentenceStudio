using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachMemoryFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoachMemoryFact",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ScopeKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProtectedValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ValueVersion = table.Column<int>(type: "integer", nullable: false),
                    ProtectionVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Provenance = table.Column<int>(type: "integer", nullable: false),
                    SourceConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourceMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EvidenceCount = table.Column<int>(type: "integer", nullable: false),
                    EvidenceFirstObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EvidenceLastObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SupersedesId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachMemoryFact", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoachMemoryFact_UserProfileId_SourceConversationId",
                table: "CoachMemoryFact",
                columns: new[] { "UserProfileId", "SourceConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachMemoryFact_UserProfileId_Status_Kind",
                table: "CoachMemoryFact",
                columns: new[] { "UserProfileId", "Status", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachMemoryFact_UserProfileId_UpdatedAt",
                table: "CoachMemoryFact",
                columns: new[] { "UserProfileId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_CoachMemoryFact_UserProfileId_Kind_ScopeKey_Active",
                table: "CoachMemoryFact",
                columns: new[] { "UserProfileId", "Kind", "ScopeKey" },
                unique: true,
                filter: "\"Status\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoachMemoryFact");
        }
    }
}
