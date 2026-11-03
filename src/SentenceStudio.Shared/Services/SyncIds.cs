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

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A synced row ID needs every part of its key.", name);
    }
}
