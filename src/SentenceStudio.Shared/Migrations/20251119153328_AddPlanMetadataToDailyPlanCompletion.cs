using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanMetadataToDailyPlanCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DescriptionKey",
                table: "DailyPlanCompletion",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "EstimatedMinutes",
                table: "DailyPlanCompletion",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "DailyPlanCompletion",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Route",
                table: "DailyPlanCompletion",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RouteParametersJson",
                table: "DailyPlanCompletion",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TitleKey",
                table: "DailyPlanCompletion",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DescriptionKey",
                table: "DailyPlanCompletion");

            migrationBuilder.DropColumn(
                name: "EstimatedMinutes",
                table: "DailyPlanCompletion");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "DailyPlanCompletion");

            migrationBuilder.DropColumn(
                name: "Route",
                table: "DailyPlanCompletion");

            migrationBuilder.DropColumn(
                name: "RouteParametersJson",
                table: "DailyPlanCompletion");

            migrationBuilder.DropColumn(
                name: "TitleKey",
                table: "DailyPlanCompletion");
        }
    }
}
