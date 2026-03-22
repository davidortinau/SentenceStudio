using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Shared.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class SyncDailyPlanAndUserActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite doesn't support ALTER COLUMN to change type, so we need to recreate tables
            
            // 1. UserActivity table migration
            migrationBuilder.CreateTable(
                name: "UserActivity_new",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Activity = table.Column<string>(type: "TEXT", nullable: true),
                    Input = table.Column<string>(type: "TEXT", nullable: true),
                    Fluency = table.Column<double>(type: "REAL", nullable: false),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    UserProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserActivity_new", x => x.Id);
                });

            // Copy data with GUID conversion - assign first user profile to all records
            migrationBuilder.Sql(@"
                INSERT INTO UserActivity_new (Id, Activity, Input, Fluency, Accuracy, UserProfileId, CreatedAt, UpdatedAt)
                SELECT 
                    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6))) as Id,
                    Activity, 
                    Input, 
                    Fluency, 
                    Accuracy, 
                    COALESCE(UserProfileId, (SELECT Id FROM UserProfile LIMIT 1)) as UserProfileId,
                    CreatedAt, 
                    UpdatedAt
                FROM UserActivity;
            ");

            migrationBuilder.DropTable(name: "UserActivity");
            migrationBuilder.RenameTable(name: "UserActivity_new", newName: "UserActivity");

            // 2. DailyPlanCompletion table migration
            migrationBuilder.CreateTable(
                name: "DailyPlanCompletion_new",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PlanItemId = table.Column<string>(type: "TEXT", nullable: false),
                    ActivityType = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: true),
                    SkillId = table.Column<string>(type: "TEXT", nullable: true),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MinutesSpent = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimatedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    TitleKey = table.Column<string>(type: "TEXT", nullable: false),
                    DescriptionKey = table.Column<string>(type: "TEXT", nullable: false),
                    Rationale = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyPlanCompletion_new", x => x.Id);
                });

            // Copy data with GUID conversion - assign first user profile to all records
            migrationBuilder.Sql(@"
                INSERT INTO DailyPlanCompletion_new (Id, UserProfileId, Date, PlanItemId, ActivityType, ResourceId, SkillId, IsCompleted, CompletedAt, MinutesSpent, EstimatedMinutes, Priority, TitleKey, DescriptionKey, Rationale, CreatedAt, UpdatedAt)
                SELECT 
                    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6))) as Id,
                    (SELECT Id FROM UserProfile LIMIT 1) as UserProfileId,
                    Date, 
                    PlanItemId, 
                    ActivityType, 
                    ResourceId, 
                    SkillId, 
                    IsCompleted, 
                    CompletedAt, 
                    MinutesSpent, 
                    EstimatedMinutes, 
                    Priority, 
                    TitleKey, 
                    DescriptionKey, 
                    Rationale, 
                    CreatedAt, 
                    UpdatedAt
                FROM DailyPlanCompletion;
            ");

            migrationBuilder.DropTable(name: "DailyPlanCompletion");
            migrationBuilder.RenameTable(name: "DailyPlanCompletion_new", newName: "DailyPlanCompletion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Downgrade not supported - data loss would occur
            throw new NotSupportedException("Cannot downgrade from GUID PKs back to int PKs - data would be lost");
        }
    }
}
