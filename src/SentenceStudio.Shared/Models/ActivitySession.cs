namespace SentenceStudio.Shared.Models;

public enum ActivitySessionStatus
{
    InProgress,
    Completed,
    Abandoned
}

/// <summary>
/// Resume state of an activity run, synced across every client (Captain 2026-11-03, phase 2b of
/// docs/sync-all-tables-plan.md). A run's row ID is <see cref="SentenceStudio.Services.SyncIds.ActivitySession"/>
/// and its <see cref="StateJson"/> follows docs/activity-session-format.md. Rows of a
/// device-only type (<see cref="ActivitySessionTypes.IsDeviceOnly"/>) never leave their device.
/// </summary>
public class ActivitySession
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ActivityType { get; set; } = string.Empty;
    public string LaunchContextKey { get; set; } = string.Empty;
    public string StateJson { get; set; } = string.Empty;
    public ActivitySessionStatus Status { get; set; } = ActivitySessionStatus.InProgress;
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Activity types stored in <see cref="ActivitySession"/>.</summary>
public static class ActivitySessionTypes
{
    public const string VocabularyReview = "VocabularyReview";

    /// <summary>A native quiz's per-answer scoring receipt (device-only bookkeeping).</summary>
    public const string NativeAttemptReceipt = "VocabularyReview.NativeAttemptReceipt.v1";

    /// <summary>A native quiz's per-sentence scoring receipt (device-only bookkeeping).</summary>
    public const string NativeSentenceReceipt = "VocabularyReview.NativeSentenceReceipt.v1";

    /// <summary>ID prefix of every device-only receipt row.</summary>
    public const string ReceiptIdPrefix = "receipt:";

    /// <summary>A new receipt row ID.</summary>
    public static string NewReceiptId() => ReceiptIdPrefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Receipts only prove what this device already scored; the scores themselves sync through
    /// the progress tables. They are never uploaded, and the server refuses them.
    /// </summary>
    public static bool IsDeviceOnly(string? activityType, string? id) =>
        activityType is NativeAttemptReceipt or NativeSentenceReceipt
        || id?.StartsWith(ReceiptIdPrefix, StringComparison.Ordinal) == true;
}
