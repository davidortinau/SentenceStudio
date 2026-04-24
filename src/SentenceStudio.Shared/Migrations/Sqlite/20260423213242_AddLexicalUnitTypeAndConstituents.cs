using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddLexicalUnitTypeAndConstituents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LexicalUnitType",
                table: "VocabularyWord",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PhraseConstituent",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PhraseWordId = table.Column<string>(type: "TEXT", nullable: false),
                    ConstituentWordId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhraseConstituent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhraseConstituent_VocabularyWord_ConstituentWordId",
                        column: x => x.ConstituentWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PhraseConstituent_VocabularyWord_PhraseWordId",
                        column: x => x.PhraseWordId,
                        principalTable: "VocabularyWord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhraseConstituent_ConstituentWordId",
                table: "PhraseConstituent",
                column: "ConstituentWordId");

            migrationBuilder.CreateIndex(
                name: "IX_PhraseConstituent_PhraseWordId",
                table: "PhraseConstituent",
                column: "PhraseWordId");

            migrationBuilder.CreateIndex(
                name: "IX_PhraseConstituent_PhraseWordId_ConstituentWordId",
                table: "PhraseConstituent",
                columns: new[] { "PhraseWordId", "ConstituentWordId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhraseConstituent");

            migrationBuilder.DropColumn(
                name: "LexicalUnitType",
                table: "VocabularyWord");
        }
    }
}
