using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddActivitySession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivitySession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ActivityType = table.Column<string>(type: "TEXT", nullable: false),
                    LaunchContextKey = table.Column<string>(type: "TEXT", nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivitySession", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySession_UserId_ActivityType_Status",
                table: "ActivitySession",
                columns: new[] { "UserId", "ActivityType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySession_UserId_LaunchContextKey",
                table: "ActivitySession",
                columns: new[] { "UserId", "ActivityType", "LaunchContextKey" },
                unique: true,
                filter: "\"Status\" = 'InProgress'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivitySession");
        }
    }
}
