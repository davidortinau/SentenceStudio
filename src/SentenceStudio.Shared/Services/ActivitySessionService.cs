using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public class ActivitySessionService : IActivitySessionService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ActivitySessionService> _logger;

    public ActivitySessionService(
        IServiceProvider serviceProvider,
        ILogger<ActivitySessionService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ActivitySession?> GetResumableAsync(string userId, string activityType, string launchContextKey)
    {
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetResumableAsync called with no userId for activity {ActivityType}; returning null to prevent cross-tenant data leak.", activityType);
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ActivitySessions
            .AsNoTracking()
            .Where(s => s.UserId == userId
                && s.ActivityType == activityType
                && s.LaunchContextKey == launchContextKey
                && s.Status == ActivitySessionStatus.InProgress)
            .OrderByDescending(s => s.UpdatedAt)
            .ThenByDescending(s => s.StartedAt)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<ActivitySession?> SaveSnapshotAsync(string userId, string activityType, string launchContextKey, string stateJson)
    {
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("SaveSnapshotAsync called with no userId for activity {ActivityType}; snapshot was not saved to prevent cross-tenant data leak.", activityType);
            return null;
        }

        var now = DateTime.UtcNow;
        var runId = SyncIds.ActivitySession(userId, activityType, launchContextKey);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var (current, inserting) = await StageSnapshotAsync(
            db, runId, userId, activityType, launchContextKey, stateJson, now);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (inserting)
        {
            // Another writer created the run, or the earlier run's record, first (same ID):
            // save into it instead.
            db.ChangeTracker.Clear();
            (current, _) = await StageSnapshotAsync(
                db, runId, userId, activityType, launchContextKey, stateJson, now);
            await db.SaveChangesAsync();
        }

        return current;
    }

    /// <summary>
    /// Stages the snapshot on the run's row: the in-progress row if one exists (a row saved
    /// before run IDs were shared keeps its own ID until the run ends), otherwise the shared run
    /// row (<see cref="SyncIds.ActivitySession"/>), revived if an earlier run completed or was
    /// abandoned (that run's record is first copied to a row of its own), or inserted. Any older in-progress duplicate is marked abandoned, never deleted.
    /// </summary>
    private static async Task<(ActivitySession Session, bool Inserting)> StageSnapshotAsync(
        ApplicationDbContext db, string runId, string userId, string activityType,
        string launchContextKey, string stateJson, DateTime now)
    {
        var inProgressSessions = await db.ActivitySessions
            .Where(s => s.UserId == userId
                && s.ActivityType == activityType
                && s.LaunchContextKey == launchContextKey
                && s.Status == ActivitySessionStatus.InProgress)
            .OrderByDescending(s => s.UpdatedAt)
            .ThenByDescending(s => s.StartedAt)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        var current = inProgressSessions.FirstOrDefault();
        foreach (var olderSession in inProgressSessions.Skip(1))
        {
            olderSession.Status = ActivitySessionStatus.Abandoned;
            olderSession.UpdatedAt = now;
        }

        if (current is not null)
        {
            current.StateJson = stateJson;
            current.UpdatedAt = now;
            current.CompletedAt = null;
            return (current, false);
        }

        var earlierRun = await db.ActivitySessions.SingleOrDefaultAsync(s => s.Id == runId);
        if (earlierRun is not null)
        {
            if (earlierRun.UserId != userId || earlierRun.ActivityType != activityType
                || earlierRun.LaunchContextKey != launchContextKey)
                throw new InvalidOperationException("An activity run ID belongs to a different run.");
            // The finished run's record is kept under its own ID before the shared row starts
            // the new run (one row per finished run, as before run IDs were shared). The ID is
            // the same on every client, so clients that revive the same run keep one record; a
            // different run that started in the same second keeps its record under a new ID.
            var recordId = SyncIds.ActivitySessionRecord(runId, earlierRun.StartedAt);
            var kept = await db.ActivitySessions.AsNoTracking()
                .Where(s => s.Id == recordId && s.UserId == userId)
                .Select(s => s.StateJson)
                .SingleOrDefaultAsync();
            var keepRecord = kept != earlierRun.StateJson;
            if (keepRecord)
                db.ActivitySessions.Add(new ActivitySession
                {
                    Id = kept is null ? recordId : Guid.NewGuid().ToString(),
                    UserId = earlierRun.UserId,
                    ActivityType = earlierRun.ActivityType,
                    LaunchContextKey = earlierRun.LaunchContextKey,
                    StateJson = earlierRun.StateJson,
                    Status = earlierRun.Status,
                    StartedAt = earlierRun.StartedAt,
                    UpdatedAt = earlierRun.UpdatedAt,
                    CompletedAt = earlierRun.CompletedAt
                });
            earlierRun.Status = ActivitySessionStatus.InProgress;
            earlierRun.StateJson = stateJson;
            earlierRun.StartedAt = now;
            earlierRun.UpdatedAt = now;
            earlierRun.CompletedAt = null;
            // A record inserted meanwhile (by sync) is retried like a run row inserted meanwhile.
            return (earlierRun, keepRecord);
        }

        current = new ActivitySession
        {
            Id = runId,
            UserId = userId,
            ActivityType = activityType,
            LaunchContextKey = launchContextKey,
            StateJson = stateJson,
            Status = ActivitySessionStatus.InProgress,
            StartedAt = now,
            UpdatedAt = now
        };
        db.ActivitySessions.Add(current);
        return (current, true);
    }

    public async Task CompleteAsync(string userId, string activityType, string launchContextKey)
    {
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("CompleteAsync called with no userId for activity {ActivityType}; no activity sessions were completed.", activityType);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sessions = await db.ActivitySessions
            .Where(s => s.UserId == userId
                && s.ActivityType == activityType
                && s.LaunchContextKey == launchContextKey
                && s.Status == ActivitySessionStatus.InProgress)
            .ToListAsync();

        if (sessions.Count == 0)
        {
            _logger.LogWarning("CompleteAsync found no in-progress activity session for user {UserId}, activity {ActivityType}, context {LaunchContextKey}.", userId, activityType, launchContextKey);
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var session in sessions)
        {
            session.Status = ActivitySessionStatus.Completed;
            session.CompletedAt = now;
            session.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
    }

    public async Task AbandonAsync(string userId, string sessionId)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning("AbandonAsync called with no userId or session for activity session {SessionId}; no activity sessions were abandoned.", sessionId);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var session = await db.ActivitySessions
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);
        if (session is null)
        {
            _logger.LogWarning("AbandonAsync called for missing activity session {SessionId} scoped to user {UserId}.", sessionId, userId);
            return;
        }

        session.Status = ActivitySessionStatus.Abandoned;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
