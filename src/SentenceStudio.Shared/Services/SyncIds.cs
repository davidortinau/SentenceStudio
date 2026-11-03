namespace SentenceStudio.Services;

/// <summary>
/// IDs for synced rows that must be one per real-world thing on every device and the web
/// (Captain 2026-11-02, docs/sync-all-tables-plan.md). Built from the row's own columns so the
/// same text comes out everywhere, including in the SQL migration that converted existing
/// rows (keep the formats identical to that migration).
/// </summary>
public static class SyncIds
{
    /// <summary>One Minimal Pair per learner and (stored-order) word pair.</summary>
    public static string MinimalPair(string userId, string wordAId, string wordBId)
    {
        Require(userId, nameof(userId));
        Require(wordAId, nameof(wordAId));
        Require(wordBId, nameof(wordBId));
        return $"mp:{userId}:{wordAId}:{wordBId}";
    }

    /// <summary>One memory per conversation.</summary>
    public static string ConversationMemory(string conversationId)
    {
        Require(conversationId, nameof(conversationId));
        return $"cm:{conversationId}";
    }

    /// <summary>
    /// A built-in conversation scenario: the same fixed ID on every client, so built-ins never
    /// sync and a synced conversation's scenario reference means the same scenario everywhere
    /// (phase 3, Captain 2026-11-03). The key is the scenario's English name in lower case with
    /// spaces as hyphens ("First Meeting" is "scn:first-meeting"); keep it identical to the
    /// migration that converted existing rows.
    /// </summary>
    public static string BuiltInScenario(string englishName)
    {
        Require(englishName, nameof(englishName));
        return "scn:" + englishName.Trim().ToLowerInvariant().Replace(' ', '-');
    }

    /// <summary>True for a built-in scenario's fixed ID.</summary>
    public static bool IsBuiltInScenario(string? id) =>
        id?.StartsWith("scn:", StringComparison.Ordinal) == true;

    /// <summary>
    /// One activity run per learner, activity and launch (plan card, focus words, resources,
    /// due-only and skill: <c>VocabQuizSessionSnapshot.BuildLaunchContextKey</c>). Later runs of
    /// the same launch reuse the row. Not for device-only receipts.
    /// </summary>
    public static string ActivitySession(string userId, string activityType, string launchContextKey)
    {
        Require(userId, nameof(userId));
        Require(activityType, nameof(activityType));
        ArgumentNullException.ThrowIfNull(launchContextKey);
        return $"as:{userId}:{activityType}:{launchContextKey}";
    }

    /// <summary>
    /// The row that keeps one run's record once its run row is reused or replaced: the run ID
    /// and the UTC second the run started. Every client derives the same ID for the same run, so
    /// clients that keep the same record make one row (docs/activity-session-format.md).
    /// </summary>
    public static string ActivitySessionRecord(string runId, DateTime startedAt)
    {
        Require(runId, nameof(runId));
        var utc = startedAt.Kind == DateTimeKind.Local
            ? startedAt.ToUniversalTime()
            : DateTime.SpecifyKind(startedAt, DateTimeKind.Utc);
        // Whole seconds: PostgreSQL keeps microseconds and SQLite text keeps ticks.
        return runId + "@" + utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A synced row ID needs every part of its key.", name);
    }
}
