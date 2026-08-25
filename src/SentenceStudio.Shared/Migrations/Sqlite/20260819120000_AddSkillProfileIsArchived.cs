using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <summary>
    /// Gives <c>SkillProfile</c> the archive flag that replaces deleting a skill.
    /// </summary>
    /// <remarks>
    /// The SQLite half of the dual-provider pair, hand-written because <c>dotnet ef</c> only ever
    /// scaffolds the active provider. The <c>[DbContext]</c> and <c>[Migration]</c> attributes
    /// are load-bearing rather than decorative: without them EF never discovers this migration and
    /// <c>MigrateAsync</c> skips it in silence, so the column would be missing on every device
    /// while the server looked fine.
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
                type: "INTEGER",
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
