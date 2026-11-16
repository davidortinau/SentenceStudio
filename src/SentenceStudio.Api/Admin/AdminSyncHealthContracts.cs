namespace SentenceStudio.Api.Admin;

/// <summary>A learner as the admin pages name one: profile id, display name and email. Nothing else.</summary>
public sealed record AdminLearnerSummary(string ProfileId, string? DisplayName, string? Email);

/// <summary>One page of learners, ordered by profile id.</summary>
/// <param name="NextAfter">Pass as <c>after</c> to read the next page; null at the end.</param>
public sealed record AdminLearnerPage(IReadOnlyList<AdminLearnerSummary> Items, string? NextAfter);

/// <summary>Codes for <see cref="AdminSyncTableHealth.Status"/>. Fixed strings, never enum names.</summary>
public static class AdminSyncTableStatus
{
    /// <summary>The table's rows name their owner (or take it from a parent row) and were counted.</summary>
    public const string Counted = "counted";

    /// <summary>The table syncs but its rows carry no owner, so no learner's share can be counted.</summary>
    public const string NoOwner = "no_owner";
}

/// <summary>Codes for what the server does not record. The page says "not recorded" for each.</summary>
public static class AdminSyncNotRecorded
{
    public const string LastSyncTime = "last_sync_time";
    public const string RefusedRows = "refused_rows";
}

/// <summary>One synced table, for one learner.</summary>
/// <param name="Table">The table's name in the sync registration.</param>
/// <param name="Status">A code from <see cref="AdminSyncTableStatus"/>.</param>
/// <param name="RowCount">Rows the learner owns on the server; null when <paramref name="Status"/> is <c>no_owner</c>.</param>
/// <param name="NewestChangeField">The table's own last-changed column the newest value is read from; null when it has none.</param>
/// <param name="NewestChangeUtc">
/// The newest value of that column over the learner's rows, as stored. Null when the learner has no
/// rows or the table has no such column.
/// </param>
public sealed record AdminSyncTableHealth(
    string Table,
    string Status,
    long? RowCount,
    string? NewestChangeField,
    DateTime? NewestChangeUtc);

/// <summary>
/// A device store that has synced for this learner, as far as the server's CoreSync tables show.
/// Versions are positions in CoreSync's change log, not times: the server records no time.
/// </summary>
/// <param name="StoreId">The device's sync store id (a random id, not an account or device name).</param>
/// <param name="DownloadedThroughVersion">The newest server change this store has been sent.</param>
/// <param name="UploadedThroughVersion">The newest of the store's own changes the server has applied.</param>
/// <param name="ChangesBehind">Server changes after <paramref name="DownloadedThroughVersion"/>, for every learner, not only this one.</param>
public sealed record AdminSyncDevice(
    string StoreId,
    long? DownloadedThroughVersion,
    long? UploadedThroughVersion,
    long? ChangesBehind);

/// <summary>Everything the sync health page shows for one learner.</summary>
/// <param name="NewestChangeUtc">The newest of the tables' newest changes.</param>
/// <param name="ServerChangeVersion">The server's current position in the CoreSync change log; null when it has none.</param>
/// <param name="NotRecorded">Codes from <see cref="AdminSyncNotRecorded"/>: what the server keeps no record of.</param>
public sealed record AdminSyncHealthLearner(
    AdminLearnerSummary Learner,
    IReadOnlyList<AdminSyncTableHealth> Tables,
    DateTime? NewestChangeUtc,
    IReadOnlyList<AdminSyncDevice> Devices,
    long? ServerChangeVersion,
    IReadOnlyList<string> NotRecorded);

/// <summary>What is deployed: the API build and the database's migrations.</summary>
/// <param name="LatestMigration">The newest migration applied to the database; null when none is recorded.</param>
/// <param name="ServerChangeVersion">The newest CoreSync change number; null when the change log is empty or absent.</param>
/// <param name="OldestChangeVersion">The oldest change number still kept.</param>
public sealed record AdminSyncHealthApp(
    string ApiVersion,
    string? LatestMigration,
    int AppliedMigrations,
    int PendingMigrations,
    long? ServerChangeVersion,
    long? OldestChangeVersion);

/// <summary>A search of the learner list.</summary>
/// <param name="Q">Part of a name or email. Sent in the body so it is not in a logged URL.</param>
public sealed record AdminLearnerSearchRequest(string? Q);

