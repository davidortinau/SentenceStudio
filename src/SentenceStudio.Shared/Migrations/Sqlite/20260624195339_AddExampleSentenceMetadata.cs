using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddExampleSentenceMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "ExampleSentence",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "ExampleSentence",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Register",
                table: "ExampleSentence",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DifficultyLevel",
                table: "ExampleSentence",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFlagged",
                table: "ExampleSentence",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Source", table: "ExampleSentence");
            migrationBuilder.DropColumn(name: "Status", table: "ExampleSentence");
            migrationBuilder.DropColumn(name: "Register", table: "ExampleSentence");
            migrationBuilder.DropColumn(name: "DifficultyLevel", table: "ExampleSentence");
            migrationBuilder.DropColumn(name: "IsFlagged", table: "ExampleSentence");
        }
    }
}
