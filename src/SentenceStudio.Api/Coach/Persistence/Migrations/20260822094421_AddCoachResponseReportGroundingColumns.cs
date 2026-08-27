using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachResponseReportGroundingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GroundingAltered",
                table: "CoachResponseReport",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroundingFindingCount",
                table: "CoachResponseReport",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroundingLimitationCode",
                table: "CoachResponseReport",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GroundingRefused",
                table: "CoachResponseReport",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GroundingRepairSuppressed",
                table: "CoachResponseReport",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroundingRuleCodes",
                table: "CoachResponseReport",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroundingShadowLabel",
                table: "CoachResponseReport",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroundingStage",
                table: "CoachResponseReport",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GroundingAltered",
                table: "CoachResponseReport");

            migrationBuilder.DropColumn(
                name: "GroundingFindingCount",
                table: "CoachResponseReport");

            migrationBuilder.DropColumn(
                name: "GroundingLimitationCode",
                table: "CoachResponseReport");

            migrationBuilder.DropColumn(
                name: "GroundingRefused",
                table: "CoachResponseReport");

            migrationBuilder.DropColumn(
                name: "GroundingRepairSuppressed",
                table: "CoachResponseReport");

            migrationBuilder.DropColumn(
                name: "GroundingRuleCodes",
                table: "CoachResponseReport");

            migrationBuilder.DropColumn(
                name: "GroundingShadowLabel",
                table: "CoachResponseReport");

            migrationBuilder.DropColumn(
                name: "GroundingStage",
                table: "CoachResponseReport");
        }
    }
}
