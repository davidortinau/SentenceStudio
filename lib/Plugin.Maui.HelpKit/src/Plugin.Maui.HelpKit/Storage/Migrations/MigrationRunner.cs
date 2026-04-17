using Microsoft.Extensions.Logging;
using SQLite;

namespace Plugin.Maui.HelpKit.Storage.Migrations;

/// <summary>
/// Applies all pending <see cref="IHelpKitMigration"/>s in version order.
/// Forward-only: if the database reports a version higher than the latest
/// known migration, logs a warning and continues rather than rolling back —
/// user data is never destroyed on a downgrade.
/// </summary>
internal static class MigrationRunner
{
    /// <summary>
    /// The ordered list of known migrations. Add new migrations at the end,
    /// never re-order or remove entries.
    /// </summary>
    private static readonly IReadOnlyList<IHelpKitMigration> s_migrations = new IHelpKitMigration[]
    {
        new V001_InitialSchema(),
    };

    /// <summary>Highest known migration version.</summary>
    public static int LatestVersion => s_migrations.Count == 0 ? 0 : s_migrations[^1].Version;

    /// <summary>
    /// Ensure the database is at or above the latest known schema version.
    /// </summary>
    public static void Apply(SQLiteConnection db, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        // The schema_version table itself must exist before we can read it.
        db.CreateTable<SchemaVersionRow>();

        var currentRow = db.Find<SchemaVersionRow>(1);
        var currentVersion = currentRow?.Version ?? 0;

        if (currentVersion > LatestVersion)
        {
            logger?.LogWarning(
                "HelpKit schema version {Current} is newer than this library knows about ({Latest}). " +
                "Continuing without changes. User data is preserved.",
                currentVersion, LatestVersion);
            return;
        }

        foreach (var migration in s_migrations)
        {
            if (migration.Version <= currentVersion)
                continue;

            logger?.LogInformation("Applying HelpKit migration v{Version}", migration.Version);
            migration.Apply(db);
            currentVersion = migration.Version;

            db.InsertOrReplace(new SchemaVersionRow
            {
                Id = 1,
                Version = currentVersion,
                AppliedAt = DateTime.UtcNow,
            });
        }
    }
}
