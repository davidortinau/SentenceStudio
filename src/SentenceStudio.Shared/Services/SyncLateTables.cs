using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.Services;

/// <summary>
/// Tables added to CoreSync after stores already held rows (Captain 2026-11-02,
/// docs/sync-all-tables-plan.md). CoreSync records a change only after a table's tracking
/// triggers exist, and sends a full snapshot only to a store syncing for the first time, so
/// rows written before a table joined sync would never move. The first time a store sets up
/// tracking for one of these tables, its existing rows get a no-op update ("touch"), which
/// records them as changes: a device uploads its own rows, and the server sends its rows to
/// every device of their owner.
/// "First time" is read from the store itself: the table had no CoreSync update trigger
/// before provisioning. No marker table and no schema change are needed.
/// </summary>
public static class SyncLateTables
{
    /// <summary>
    /// Late-joined tables and the column that names each row's owner. Listed parents first:
    /// the server touches tables in this order, so a device downloads a parent before its child.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OwnerColumns { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NumberAttempt"] = "UserProfileId",
            ["NumberMasteryProgress"] = "UserProfileId",
            ["WordAssociationScore"] = "UserProfileId",
            ["MinimalPair"] = "UserId",
            ["MinimalPairSession"] = "UserId",
            ["MinimalPairAttempt"] = "UserId",
            ["StreamHistory"] = "UserProfileId",
            ["ConversationMemoryState"] = "UserProfileId",
        };

    /// <summary>Late tables that this store has not provisioned yet. Call before provisioning.</summary>
    public static async Task<IReadOnlyList<string>> UntrackedAsync(
        ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
            await connection.OpenAsync(cancellationToken);
        try
        {
            var untracked = new List<string>();
            foreach (var table in OwnerColumns.Keys)
            {
                await using var command = connection.CreateCommand();
                if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
                {
                    // CoreSync creates these triggers with unquoted names, which
                    // PostgreSQL folds to lower case.
                    command.CommandText =
                        "SELECT 1 FROM pg_trigger WHERE tgname = lower(@name) LIMIT 1";
                    Add(command, "@name", $"__{table}_ct_update__");
                }
                else
                {
                    command.CommandText =
                        "SELECT 1 FROM sqlite_master WHERE type = 'trigger' AND name = @name LIMIT 1";
                    Add(command, "@name", $"__{table}_ct-UPDATE__");
                }
                if (await command.ExecuteScalarAsync(cancellationToken) is null)
                    untracked.Add(table);
            }
            return untracked;
        }
        finally
        {
            if (opened)
                await connection.CloseAsync();
        }
    }

    /// <summary>
    /// Touches the rows of <paramref name="tables"/> after provisioning. A device passes its
    /// signed-in owner and touches only that owner's rows (an older shared device database
    /// can hold other learners' rows, which the server would refuse). The server passes no
    /// owner and touches every row; each device then downloads only its own owner's rows.
    /// </summary>
    public static async Task<int> TouchAsync(
        ApplicationDbContext db, IReadOnlyList<string> tables, string? owner, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var total = 0;
        foreach (var table in tables)
        {
            if (!OwnerColumns.TryGetValue(table, out var ownerColumn))
                throw new InvalidOperationException($"{table} is not a late-joined sync table.");
            total += owner is null
                ? await db.Database.ExecuteSqlRawAsync(
                    $"UPDATE \"{table}\" SET \"Id\" = \"Id\"", cancellationToken)
                : await db.Database.ExecuteSqlRawAsync(
                    $"UPDATE \"{table}\" SET \"Id\" = \"Id\" WHERE \"{ownerColumn}\" = {{0}}",
                    [owner], cancellationToken);
        }
        if (tables.Count > 0)
            logger.LogInformation(
                "Sync: {Count} existing rows queued for newly synced tables {Tables}",
                total, string.Join(", ", tables));
        return total;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
