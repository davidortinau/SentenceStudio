using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Feedback.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFeedbackSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedbackRateWindow",
                columns: table => new
                {
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RecentTicksCsv = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackRateWindow", x => new { x.UserProfileId, x.Kind });
                });

            migrationBuilder.CreateTable(
                name: "FeedbackSubmission",
                columns: table => new
                {
                    Jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ContentDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssueNumber = table.Column<int>(type: "integer", nullable: true),
                    IssueUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IssueTitle = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RouteCategory = table.Column<int>(type: "integer", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TokenExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackSubmission", x => x.Jti);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackRateWindow_UpdatedAtUtc",
                table: "FeedbackRateWindow",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackSubmission_Status",
                table: "FeedbackSubmission",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackSubmission_TokenExpiresAtUtc",
                table: "FeedbackSubmission",
                column: "TokenExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackSubmission_UserProfileId",
                table: "FeedbackSubmission",
                column: "UserProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedbackRateWindow");

            migrationBuilder.DropTable(
                name: "FeedbackSubmission");
        }
    }
}
