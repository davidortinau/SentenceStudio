namespace SentenceStudio.Services.Vocabulary;

/// <summary>
/// Configuration for the opt-in part-of-speech backfill.
/// </summary>
/// <remarks>
/// <para>
/// Every default is the safe one: disabled, with an empty allowlist. The backfill reads real
/// learner vocabulary and sends part of it to a model, so it must be turned on deliberately and
/// pointed at named profiles. There is no "all users" mode and no way to express one — an empty
/// <see cref="UserProfileIds"/> means nobody, never everybody.
/// </para>
/// <para>
/// Bound from the <c>VocabularyPartOfSpeechBackfill</c> section; environment variables use the
/// <c>VocabularyPartOfSpeechBackfill__*</c> form.
/// </para>
/// </remarks>
public sealed class VocabularyPartOfSpeechBackfillOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "VocabularyPartOfSpeechBackfill";

    /// <summary>Smallest accepted batch size.</summary>
    public const int MinBatchSize = 1;

    /// <summary>Largest accepted batch size. A bigger batch means a longer prompt and a costlier rejection.</summary>
    public const int MaxBatchSize = 100;

    /// <summary>Default batch size.</summary>
    public const int DefaultBatchSize = 40;

    /// <summary>Default ceiling on words classified in one run.</summary>
    public const int DefaultMaxWords = 500;

    /// <summary>Master switch. False means the service returns without touching the database.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The trusted profiles to backfill. Required: with an empty list the service refuses to run
    /// and issues no query at all.
    /// </summary>
    public IList<string> UserProfileIds { get; set; } = new List<string>();

    /// <summary>Words sent to the classifier per model call. Clamped to <see cref="MinBatchSize"/>..<see cref="MaxBatchSize"/>.</summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>Ceiling on words classified in a single run, across all listed profiles.</summary>
    public int MaxWords { get; set; } = DefaultMaxWords;

    /// <summary>The batch size actually used, clamped into the accepted range.</summary>
    public int EffectiveBatchSize => Math.Clamp(BatchSize, MinBatchSize, MaxBatchSize);

    /// <summary>The run ceiling actually used. A value below one does no work rather than meaning "unlimited".</summary>
    public int EffectiveMaxWords => Math.Max(0, MaxWords);

    /// <summary>
    /// The allowlist, trimmed and deduplicated, with blanks dropped. A blank entry can never
    /// become an empty-string user id that matches unowned rows.
    /// </summary>
    public IReadOnlyList<string> NormalizedUserProfileIds()
    {
        if (UserProfileIds is null || UserProfileIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(UserProfileIds.Count);

        foreach (var id in UserProfileIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var trimmed = id.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        // Deterministic order so a run's batching is reproducible.
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>True only when the feature is on and at least one real profile is named.</summary>
    public bool CanRun() => Enabled && NormalizedUserProfileIds().Count > 0;
}
