using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddStreamHistoryOwnerScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserProfileId",
                table: "StreamHistory",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StreamHistory_UserProfileId_CreatedAt",
                table: "StreamHistory",
                columns: new[] { "UserProfileId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StreamHistory_UserProfileId_CreatedAt",
                table: "StreamHistory");

            migrationBuilder.DropColumn(
                name: "UserProfileId",
                table: "StreamHistory");
        }
    }
}
