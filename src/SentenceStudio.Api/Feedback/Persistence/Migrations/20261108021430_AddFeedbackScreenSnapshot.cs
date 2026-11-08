using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Feedback.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackScreenSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedbackScreenSnapshot",
                columns: table => new
                {
                    Reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContextJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackScreenSnapshot", x => x.Reference);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackScreenSnapshot_CreatedAtUtc",
                table: "FeedbackScreenSnapshot",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackScreenSnapshot_Jti",
                table: "FeedbackScreenSnapshot",
                column: "Jti",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackScreenSnapshot_UserProfileId",
                table: "FeedbackScreenSnapshot",
                column: "UserProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedbackScreenSnapshot");
        }
    }
}
