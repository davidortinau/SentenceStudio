using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddMinimalPairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MinimalPair",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    VocabularyWordAId = table.Column<int>(type: "INTEGER", nullable: false),
                    VocabularyWordBId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContrastLabel = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinimalPair", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MinimalPair_VocabularyWord_VocabularyWordAId",
                        column: x => x.VocabularyWordAId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MinimalPair_VocabularyWord_VocabularyWordBId",
                        column: x => x.VocabularyWordBId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MinimalPairSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    PlannedTrialCount = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinimalPairSession", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MinimalPairAttempt",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: false),
                    PairId = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptWordId = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectedWordId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsCorrect = table.Column<bool>(type: "INTEGER", nullable: false),
                    SequenceNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinimalPairAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MinimalPairAttempt_MinimalPairSession_SessionId",
                        column: x => x.SessionId,
                        principalTable: "MinimalPairSession",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MinimalPairAttempt_MinimalPair_PairId",
                        column: x => x.PairId,
                        principalTable: "MinimalPair",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MinimalPairAttempt_VocabularyWord_PromptWordId",
                        column: x => x.PromptWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MinimalPairAttempt_VocabularyWord_SelectedWordId",
                        column: x => x.SelectedWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPair_UserId_VocabularyWordAId_VocabularyWordBId",
                table: "MinimalPair",
                columns: new[] { "UserId", "VocabularyWordAId", "VocabularyWordBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPair_VocabularyWordAId",
                table: "MinimalPair",
                column: "VocabularyWordAId");

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPair_VocabularyWordBId",
                table: "MinimalPair",
                column: "VocabularyWordBId");

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPairAttempt_PairId_CreatedAt",
                table: "MinimalPairAttempt",
                columns: new[] { "PairId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPairAttempt_PromptWordId",
                table: "MinimalPairAttempt",
                column: "PromptWordId");

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPairAttempt_SelectedWordId",
                table: "MinimalPairAttempt",
                column: "SelectedWordId");

            migrationBuilder.CreateIndex(
                name: "IX_MinimalPairAttempt_SessionId_SequenceNumber",
                table: "MinimalPairAttempt",
                columns: new[] { "SessionId", "SequenceNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MinimalPairAttempt");

            migrationBuilder.DropTable(
                name: "MinimalPairSession");

            migrationBuilder.DropTable(
                name: "MinimalPair");
        }
    }
}
