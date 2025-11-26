using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRedundantRouteStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Route",
                table: "DailyPlanCompletion");

            migrationBuilder.DropColumn(
                name: "RouteParametersJson",
                table: "DailyPlanCompletion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}
