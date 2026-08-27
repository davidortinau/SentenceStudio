using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachOpportunities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoachOpportunity",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TurnId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TurnOperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Disposition = table.Column<int>(type: "integer", nullable: false),
                    Surface = table.Column<int>(type: "integer", nullable: false),
                    CapabilityCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ToolName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RiskClass = table.Column<int>(type: "integer", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StopReason = table.Column<int>(type: "integer", nullable: true),
                    OfferLink = table.Column<int>(type: "integer", nullable: false),
                    EvidenceMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EvidenceMessageSequence = table.Column<long>(type: "bigint", nullable: true),
                    EvidenceOfferMessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EvidenceOfferMessageSequence = table.Column<long>(type: "bigint", nullable: true),
                    WriteOperationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RelatedOpportunityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DedupBucketDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    FirstObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewerNoteCode = table.Column<int>(type: "integer", nullable: true),
                    LinkedSpecPath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EvidenceRevealCount = table.Column<int>(type: "integer", nullable: false),
                    EvidenceLastRevealedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoachOpportunity", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoachOpportunity_Kind_CapabilityCode_LastObservedAtUtc",
                table: "CoachOpportunity",
                columns: new[] { "Kind", "CapabilityCode", "LastObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachOpportunity_LastObservedAtUtc",
                table: "CoachOpportunity",
                column: "LastObservedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CoachOpportunity_Status_LastObservedAtUtc",
                table: "CoachOpportunity",
                columns: new[] { "Status", "LastObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachOpportunity_UserProfileId_ConversationId",
                table: "CoachOpportunity",
                columns: new[] { "UserProfileId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoachOpportunity_UserProfileId_Fingerprint_DedupBucketDate",
                table: "CoachOpportunity",
                columns: new[] { "UserProfileId", "Fingerprint", "DedupBucketDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoachOpportunity");
        }
    }
}
