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

    /// <summary>
    /// One lookup per learner, language, direction and normalized text (Captain 2026-11-04,
    /// decision 4 B). "lk:" and the first 16 bytes, as lower-case hex, of the SHA-256 of the
    /// UTF-8 text <c>owner U+001F language U+001F direction U+001F text</c>, where language is
    /// trimmed and lower-cased (invariant), direction is "ToNative" or "ToTarget" and text is
    /// <see cref="NormalizeLookupText"/>. Every client (including the Flutter port) must build
    /// it the same way (docs/sync-all-tables-plan.md, "Lookup history").
    /// </summary>
    public static string VocabularyLookup(
        string userId, string? language,
        SentenceStudio.Shared.Models.VocabularyLookupDirection direction, string text)
    {
        Require(userId, nameof(userId));
        var normalized = NormalizeLookupText(text);
        Require(normalized, nameof(text));
        return HashedId("lk:", userId,
            (language ?? string.Empty).Trim().ToLowerInvariant(), direction.ToString(), normalized);
    }

    /// <summary>
    /// The text a lookup is keyed by: Unicode NFC, trimmed, every run of white space made one
    /// space, lower-cased (invariant), so "  Hello   World " and "hello world" are one lookup.
    /// </summary>
    public static string NormalizeLookupText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var composed = text.Normalize(System.Text.NormalizationForm.FormC).Trim();
        var builder = new System.Text.StringBuilder(composed.Length);
        var inSpace = false;
        foreach (var character in composed)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!inSpace)
                    builder.Append(' ');
                inSpace = true;
                continue;
            }
            builder.Append(character);
            inSpace = false;
        }
        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// One decision per learner, kind, duplicate term and set of copies (docs/duplicate-merge-ai-plan.md).
    /// "dd:" and the first 16 bytes, as lower-case hex, of the SHA-256 of the UTF-8 text
    /// <c>owner U+001F kind U+001F term U+001F copies</c>, where kind is "Merged" or "KeptApart",
    /// term is the group's normalized target term (<see cref="VocabularyDuplicatePolicy.NormalizeTargetTerm"/>)
    /// and copies is <see cref="DuplicateDecisionCopies"/>. Every client (including the Flutter
    /// port) must build it the same way (docs/sync-all-tables-plan.md, "Duplicate decisions").
    /// </summary>
    public static string VocabularyDuplicateDecision(
        string userId, SentenceStudio.Shared.Models.VocabularyDuplicateDecisionKind kind,
        string termKey, IEnumerable<string> copyIds)
    {
        Require(userId, nameof(userId));
        Require(termKey, nameof(termKey));
        var copies = DuplicateDecisionCopies(copyIds);
        Require(copies, nameof(copyIds));
        return HashedId("dd:", userId, kind.ToString(), termKey, copies);
    }

    /// <summary>
    /// A decision's copies as stored and hashed: the distinct, trimmed word IDs, ordinal-sorted,
    /// joined with ",".
    /// </summary>
    public static string DuplicateDecisionCopies(IEnumerable<string> copyIds) =>
        string.Join(',', copyIds.Select(id => id?.Trim() ?? string.Empty).Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>
    /// One smart resource (Daily Review, New Words and the rest) per learner and type.
    /// "sr:" and the first 16 bytes, as lower-case hex, of the SHA-256 of the UTF-8 text
    /// <c>owner U+001F type</c>, where type is the stored <c>SmartResourceType</c> ("DailyReview",
    /// "NewWords", "Struggling", "Phrases", "Sentences"). Every client that creates smart
    /// resources (including the Flutter port) must build it the same way
    /// (docs/sync-all-tables-plan.md, "Smart resources"). Lists made before 2026-11-16 keep the
    /// GUID they were made with.
    /// </summary>
    public static string SmartResource(string userId, string smartResourceType)
    {
        Require(userId, nameof(userId));
        Require(smartResourceType, nameof(smartResourceType));
        return HashedId("sr:", userId, smartResourceType);
    }

    /// <summary>
    /// <paramref name="prefix"/> and the first 16 bytes, as lower-case hex, of the SHA-256 of the
    /// UTF-8 text of <paramref name="parts"/> joined with U+001F.
    /// </summary>
    private static string HashedId(string prefix, params string[] parts)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        return prefix + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A synced row ID needs every part of its key.", name);
    }
}
