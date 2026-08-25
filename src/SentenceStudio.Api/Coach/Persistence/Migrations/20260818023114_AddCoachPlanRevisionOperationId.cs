using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachPlanRevisionOperationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperationId",
                table: "CoachPlanRevision",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoachPlanRevision_UserProfileId_OperationId",
                table: "CoachPlanRevision",
                columns: new[] { "UserProfileId", "OperationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CoachPlanRevision_UserProfileId_OperationId",
                table: "CoachPlanRevision");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "CoachPlanRevision");
        }
    }
}
