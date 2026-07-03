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

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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

        var insertingNewSession = current is null;
        if (insertingNewSession)
        {
            current = new ActivitySession
            {
                UserId = userId,
                ActivityType = activityType,
                LaunchContextKey = launchContextKey,
                StateJson = stateJson,
                Status = ActivitySessionStatus.InProgress,
                StartedAt = now,
                UpdatedAt = now
            };
            db.ActivitySessions.Add(current);
        }
        else
        {
            current.StateJson = stateJson;
            current.UpdatedAt = now;
            current.CompletedAt = null;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (insertingNewSession)
        {
            db.Entry(current).State = EntityState.Detached;
            current = await UpdateExistingInProgressSnapshotAsync(db, userId, activityType, launchContextKey, stateJson, now);
            await db.SaveChangesAsync();
        }

        return current;
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

    public async Task AbandonAsync(string userId, int sessionId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("AbandonAsync called with no userId for activity session {SessionId}; no activity sessions were abandoned.", sessionId);
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

    private static async Task<ActivitySession> UpdateExistingInProgressSnapshotAsync(
        ApplicationDbContext db,
        string userId,
        string activityType,
        string launchContextKey,
        string stateJson,
        DateTime now)
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

        var current = inProgressSessions.First();
        current.StateJson = stateJson;
        current.UpdatedAt = now;
        current.CompletedAt = null;

        foreach (var olderSession in inProgressSessions.Skip(1))
        {
            olderSession.Status = ActivitySessionStatus.Abandoned;
            olderSession.UpdatedAt = now;
        }

        return current;
    }
}
