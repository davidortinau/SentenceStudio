using SQLite;

namespace Plugin.Maui.HelpKit.Storage.Migrations;

/// <summary>
/// A single forward migration for the HelpKit SQLite database. Migrations
/// are identified by a monotonically increasing <see cref="Version"/> and
/// applied in order by <see cref="MigrationRunner"/>.
/// </summary>
/// <remarks>
/// Guidelines:
/// <list type="bullet">
/// <item>Never rewrite an applied migration — add a new one.</item>
/// <item>Never delete user data. Forward-only; downgrade is not supported.</item>
/// <item>Prefer <c>CreateTable</c> over raw SQL; fall back to <c>Execute</c>
/// only for additive column changes that sqlite-net cannot express.</item>
/// </list>
/// </remarks>
internal interface IHelpKitMigration
{
    /// <summary>Monotonically increasing version number (1, 2, 3, ...).</summary>
    int Version { get; }

    /// <summary>Apply this migration against the open connection.</summary>
    void Apply(SQLiteConnection db);
}
