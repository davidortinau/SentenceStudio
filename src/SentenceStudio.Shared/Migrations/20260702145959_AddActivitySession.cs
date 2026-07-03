using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260702145959_AddActivitySession")]
    public partial class AddActivitySession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivitySession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ActivityType = table.Column<string>(type: "text", nullable: false),
                    LaunchContextKey = table.Column<string>(type: "text", nullable: false),
                    StateJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
