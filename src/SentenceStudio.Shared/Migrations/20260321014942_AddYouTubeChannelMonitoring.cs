using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubeChannelMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitoredChannel",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserProfileId = table.Column<string>(type: "text", nullable: true),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChannelUrl = table.Column<string>(type: "text", nullable: true),
                    ChannelName = table.Column<string>(type: "text", nullable: true),
                    ChannelHandle = table.Column<string>(type: "text", nullable: true),
                    YouTubeChannelId = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CheckIntervalHours = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredChannel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VideoImport",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    UserProfileId = table.Column<string>(type: "text", nullable: true),
                    MonitoredChannelId = table.Column<string>(type: "text", nullable: true),
                    LearningResourceId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VideoId = table.Column<string>(type: "text", nullable: true),
                    VideoTitle = table.Column<string>(type: "text", nullable: true),
                    VideoUrl = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    RawTranscript = table.Column<string>(type: "text", nullable: true),
                    CleanedTranscript = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoImport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VideoImport_LearningResource_LearningResourceId",
                        column: x => x.LearningResourceId,
                        principalTable: "LearningResource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VideoImport_MonitoredChannel_MonitoredChannelId",
                        column: x => x.MonitoredChannelId,
                        principalTable: "MonitoredChannel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredChannel_IsActive_LastCheckedAt",
                table: "MonitoredChannel",
                columns: new[] { "IsActive", "LastCheckedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VideoImport_LearningResourceId",
                table: "VideoImport",
                column: "LearningResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_VideoImport_MonitoredChannelId",
                table: "VideoImport",
                column: "MonitoredChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_VideoImport_VideoId",
                table: "VideoImport",
                column: "VideoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VideoImport");

            migrationBuilder.DropTable(
                name: "MonitoredChannel");
        }
    }
}
