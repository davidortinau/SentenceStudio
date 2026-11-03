using CoreSync;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Sync;

/// <summary>
/// A quiz run has one row per learner, activity and launch on every client
/// (<see cref="SyncIds.ActivitySession"/>), and any client may save it. A device can save, go
/// offline, and upload that save after another client saved a newer one; the newer save must win
/// (phase 2b, docs/activity-session-format.md). So when an upload meets a stored row, the copy
/// saved most recently (<c>UpdatedAt</c>) wins (<see cref="LatestCopyUploadMerge"/>).
/// <para>
/// Two clients can also each hold a different run of the same launch: both started it before
/// they synced, or both had runs of it before runs synced (the migration gave each client's latest
/// the run ID). Then the losing run is not dropped: it is kept as its own record
/// (<see cref="SyncIds.ActivitySessionRecord"/>), marked abandoned if it was in progress, and
/// reaches every client. Only the authenticated owner's rows are read or written.
/// </para>
/// </summary>
internal static class ActivitySessionUploadMerge
{
    public const string Table = "ActivitySession";
    private const string OwnerColumn = "UserId";

    /// <summary>Which copy of each run row wins, and the records of the other runs to keep.</summary>
    public sealed record RunPlan(LatestCopyUploadMerge.Plan Copies, IReadOnlyList<ActivitySession> Records)
    {
        public static RunPlan Empty { get; } =
            new(LatestCopyUploadMerge.Plan.Empty(Table, OwnerColumn), []);
    }

    public static async Task<RunPlan> PrepareAsync(
        ApplicationDbContext db, IReadOnlyList<SyncItem> items, string owner, CancellationToken ct)
    {
        var uploaded = LatestCopyUploadMerge.UploadedById(items, Table);
        if (uploaded.Count == 0)
            return RunPlan.Empty;

        var ids = uploaded.Keys.ToArray();
        var stored = await db.ActivitySessions.AsNoTracking()
            .Where(row => ids.Contains(row.Id) && row.UserId == owner)
            .ToListAsync(ct);
        var copies = LatestCopyUploadMerge.Compare(Table, OwnerColumn, "UpdatedAt", uploaded,
            stored.Select(row => (row.Id, (DateTime?)row.UpdatedAt)));

        var records = new List<ActivitySession>();
        foreach (var row in stored)
        {
            var upload = uploaded[row.Id];
            if (LatestCopyUploadMerge.Timestamp(upload, "StartedAt") is not { } uploadStarted)
                continue;
            var storedRecord = SyncIds.ActivitySessionRecord(row.Id, row.StartedAt);
            var uploadRecord = SyncIds.ActivitySessionRecord(row.Id, uploadStarted);
            // The same run saved twice: the older save is only an earlier position.
            if (storedRecord == uploadRecord)
                continue;
            var replaced = copies.UploadWins.Contains(row.Id)
                ? Record(row, storedRecord)
                : Record(upload, owner, uploadRecord);
            if (replaced is not null)
                records.Add(replaced);
        }
        if (records.Count == 0)
            return new RunPlan(copies, []);

        // A record already stored, or uploaded in this sync, with the same state is the same run
        // kept twice: one row. A different run that started in the same second gets a new ID.
        var recordIds = records.Select(record => record.Id).ToArray();
        var existing = await db.ActivitySessions.AsNoTracking()
            .Where(row => recordIds.Contains(row.Id) && row.UserId == owner)
            .Select(row => new { row.Id, row.StateJson })
            .ToDictionaryAsync(row => row.Id, row => row.StateJson, StringComparer.Ordinal, ct);
        var keep = new List<ActivitySession>();
        foreach (var record in records)
        {
            var kept = existing.GetValueOrDefault(record.Id)
                ?? (uploaded.TryGetValue(record.Id, out var item)
                    ? LatestCopyUploadMerge.Text(item, "StateJson") : null);
            if (kept == record.StateJson)
                continue;
            if (kept is not null)
                record.Id = Guid.NewGuid().ToString();
            keep.Add(record);
        }
        return new RunPlan(copies, keep);
    }

    /// <summary>
    /// Saves the kept records before the upload is applied, so a stored run is copied before an
    /// upload replaces it. They are server changes, so every client downloads them.
    /// </summary>
    public static async Task KeepReplacedRunsAsync(
        ApplicationDbContext db, RunPlan plan, CancellationToken ct)
    {
        if (plan.Records.Count == 0)
            return;
        db.ActivitySessions.AddRange(plan.Records);
        await db.SaveChangesAsync(ct);
    }

    private static ActivitySession Record(ActivitySession run, string id) => new()
    {
        Id = id,
        UserId = run.UserId,
        ActivityType = run.ActivityType,
        LaunchContextKey = run.LaunchContextKey,
        StateJson = run.StateJson,
        Status = Ended(run.Status),
        StartedAt = run.StartedAt,
        UpdatedAt = run.UpdatedAt,
        CompletedAt = run.CompletedAt
    };

    private static ActivitySession? Record(SyncItem upload, string owner, string id)
    {
        string? Text(string column) => LatestCopyUploadMerge.Text(upload, column);
        DateTime? Time(string column) =>
            LatestCopyUploadMerge.Timestamp(upload, column) is { } value
                ? LatestCopyUploadMerge.Utc(value) : null;

        if (Text("UserId") != owner
            || Text("ActivityType") is not { Length: > 0 } activityType
            || ActivitySessionTypes.IsDeviceOnly(activityType, Text("Id"))
            || Text("LaunchContextKey") is not { } launchContextKey
            || Text("StateJson") is not { } stateJson
            || !Enum.TryParse<ActivitySessionStatus>(Text("Status"), out var status)
            || Time("StartedAt") is not { } startedAt
            || Time("UpdatedAt") is not { } updatedAt)
            return null;
        return new ActivitySession
        {
            Id = id,
            UserId = owner,
            ActivityType = activityType,
            LaunchContextKey = launchContextKey,
            StateJson = stateJson,
            Status = Ended(status),
            StartedAt = startedAt,
            UpdatedAt = updatedAt,
            CompletedAt = Time("CompletedAt")
        };
    }

    /// <summary>A replaced run that was still in progress is kept as abandoned (one run in progress per launch).</summary>
    private static ActivitySessionStatus Ended(ActivitySessionStatus status) =>
        status == ActivitySessionStatus.InProgress ? ActivitySessionStatus.Abandoned : status;
}
