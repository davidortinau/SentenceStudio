using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite;

/// <summary>
/// Hand-written SQLite twin of the PostgreSQL migration with the same id: the learner's Learning
/// mode on the profile (docs/known-words-plan.md, Captain 2026-11-14). One text column with the
/// default "LearnerLed"; no existing value is changed. The inline attributes are mandatory:
/// without [Migration], EF never applies it on devices.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261114233928_AddUserProfileLearningMode")]
public partial class AddUserProfileLearningMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LearningMode",
            table: "UserProfile",
            type: "TEXT",
            nullable: false,
            defaultValue: "LearnerLed");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LearningMode",
            table: "UserProfile");
    }
}
