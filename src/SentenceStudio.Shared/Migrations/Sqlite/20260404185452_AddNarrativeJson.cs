using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260404185452_AddNarrativeJson")]
public partial class AddNarrativeJson : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The SQLite plan-table rebuild already creates this column. Older
        // databases may also have it from SyncService's legacy schema patch.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Rolling back this metadata marker must not remove existing narratives.
    }
}
