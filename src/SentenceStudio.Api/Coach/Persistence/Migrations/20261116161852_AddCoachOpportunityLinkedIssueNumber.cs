using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachOpportunityLinkedIssueNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LinkedIssueNumber",
                table: "CoachOpportunity",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkedIssueNumber",
                table: "CoachOpportunity");
        }
    }
}
