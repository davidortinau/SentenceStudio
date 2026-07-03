using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public interface IActivitySessionService
{
    Task<ActivitySession?> GetResumableAsync(string userId, string activityType, string launchContextKey);
    Task<ActivitySession?> SaveSnapshotAsync(string userId, string activityType, string launchContextKey, string stateJson);
    Task CompleteAsync(string userId, string activityType, string launchContextKey);
    Task AbandonAsync(string userId, int sessionId);
}
