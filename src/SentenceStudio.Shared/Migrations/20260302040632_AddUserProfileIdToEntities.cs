using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfileIdToEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserProfileId",
                table: "UserActivity",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UserProfileId",
                table: "SkillProfile",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UserProfileId",
                table: "LearningResource",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserProfileId",
                table: "UserActivity");

            migrationBuilder.DropColumn(
                name: "UserProfileId",
                table: "SkillProfile");

            migrationBuilder.DropColumn(
                name: "UserProfileId",
                table: "LearningResource");
        }
    }
}
