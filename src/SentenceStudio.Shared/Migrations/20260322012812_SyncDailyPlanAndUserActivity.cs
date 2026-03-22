using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    /// <inheritdoc />
    public partial class SyncDailyPlanAndUserActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL requires dropping IDENTITY before changing column type to text.
            // Use raw SQL to: drop identity, convert int IDs to GUIDs, set type to text.

            // 1. UserActivity: make UserProfileId non-nullable, assign first profile to nulls
            migrationBuilder.Sql(@"
                UPDATE ""UserActivity""
                SET ""UserProfileId"" = (SELECT ""Id"" FROM ""UserProfile"" LIMIT 1)
                WHERE ""UserProfileId"" IS NULL OR ""UserProfileId"" = '';
            ");

            migrationBuilder.AlterColumn<string>(
                name: "UserProfileId",
                table: "UserActivity",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            // 2. UserActivity.Id: drop identity, convert int to GUID text
            migrationBuilder.Sql(@"
                ALTER TABLE ""UserActivity"" ALTER COLUMN ""Id"" DROP IDENTITY IF EXISTS;
                ALTER TABLE ""UserActivity"" ALTER COLUMN ""Id"" SET DATA TYPE text USING gen_random_uuid()::text;
            ");

            // 3. DailyPlanCompletion.Id: drop identity, convert int to GUID text
            migrationBuilder.Sql(@"
                ALTER TABLE ""DailyPlanCompletion"" ALTER COLUMN ""Id"" DROP IDENTITY IF EXISTS;
                ALTER TABLE ""DailyPlanCompletion"" ALTER COLUMN ""Id"" SET DATA TYPE text USING gen_random_uuid()::text;
            ");

            // 4. DailyPlanCompletion: add UserProfileId column, assign first profile
            migrationBuilder.AddColumn<string>(
                name: "UserProfileId",
                table: "DailyPlanCompletion",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(@"
                UPDATE ""DailyPlanCompletion""
                SET ""UserProfileId"" = (SELECT ""Id"" FROM ""UserProfile"" LIMIT 1)
                WHERE ""UserProfileId"" = '';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserProfileId",
                table: "DailyPlanCompletion");

            migrationBuilder.AlterColumn<string>(
                name: "UserProfileId",
                table: "UserActivity",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "UserActivity",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "DailyPlanCompletion",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);
        }
    }
}
