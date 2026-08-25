using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <summary>
    /// Gives <c>SkillProfile</c> the archive flag that replaces deleting a skill.
    /// </summary>
    /// <remarks>
    /// Additive and non-destructive: one non-nullable boolean with a false default, so every
    /// existing skill stays exactly as it is and unarchived. No data is moved and no row is
    /// touched beyond the default fill. The SQLite counterpart under <c>Migrations/Sqlite</c>
    /// carries the same migration id so both providers converge on the same schema version.
    /// </remarks>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260819120000_AddSkillProfileIsArchived")]
    public partial class AddSkillProfileIsArchived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "SkillProfile",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "SkillProfile");
        }
    }
}
