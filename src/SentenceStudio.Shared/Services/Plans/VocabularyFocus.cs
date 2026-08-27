using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// A bounded, grounded request for a vocabulary focus set.
/// </summary>
/// <remarks>
/// <para>
/// Every filter is a canonical, closed value: a
/// <see cref="VocabularyPartOfSpeech"/> and/or exact normalized category tags.
/// <see cref="DisplayDescription"/> carries the learner's original wording for
/// display and receipts only — it is <b>never</b> matched against terms or
/// glosses. Free-text substring matching over learner vocabulary would produce
/// silent false positives and is prohibited on this path.
/// </para>
/// <para>
/// No user identity here: scope comes from <c>IUserScopeProvider</c> inside the
/// resolver, so neither a caller nor a model can address another learner's
/// vocabulary.
/// </para>
/// </remarks>
public sealed record VocabularyFocusRequest
{
    public const int MinCount = 5;
    public const int MaxCount = 20;
    public const int DefaultCount = 10;

    /// <summary>The learner's original wording, for display only. Never used for matching.</summary>
    public string? DisplayDescription { get; init; }

    /// <summary>Canonical part-of-speech filter. "Active verbs" resolves to <c>Verb</c>.</summary>
    public VocabularyPartOfSpeech? PartOfSpeech { get; init; }

    /// <summary>Exact normalized category tags (compared whole, case-insensitively).</summary>
    public IReadOnlyList<string> CategoryTags { get; init; } = Array.Empty<string>();

    /// <summary>Requested set size. Valid 5..20; defaults to 10.</summary>
    public int RequestedCount { get; init; } = DefaultCount;

    /// <summary>True when at least one canonical filter is present.</summary>
    public bool HasFilter => PartOfSpeech is not null || CategoryTags.Count > 0;

    public bool TryValidate(out IReadOnlyList<string> errors)
    {
        var found = new List<string>();

        if (!HasFilter)
        {
            found.Add("A vocabulary focus needs a part of speech or at least one category tag.");
        }

        if (RequestedCount < MinCount || RequestedCount > MaxCount)
        {
            found.Add($"{nameof(RequestedCount)} must be between {MinCount} and {MaxCount}; got {RequestedCount}.");
        }

        if (PartOfSpeech is { } pos &&
            (!Enum.IsDefined(pos) || pos is VocabularyPartOfSpeech.Unknown or VocabularyPartOfSpeech.Other))
        {
            found.Add($"{nameof(PartOfSpeech)} must be a concrete part of speech, not Unknown or Other.");
        }

        if (CategoryTags.Any(string.IsNullOrWhiteSpace))
        {
            found.Add("Category tags must be non-blank.");
        }

        errors = found;
        return found.Count == 0;
    }

    /// <summary>Canonical, deduped, lowercase category tags used for matching.</summary>
    public IReadOnlyList<string> NormalizedCategoryTags() =>
        CategoryTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
}

/// <summary>Why a vocabulary focus resolved, or did not.</summary>
public enum VocabularyFocusOutcome
{
    /// <summary>A bounded set of owned words was selected.</summary>
    Success = 0,

    /// <summary>The request itself was not usable. Nothing was queried.</summary>
    InvalidFocus,

    /// <summary>
    /// The learner owns vocabulary, but too little of it is classified for the
    /// requested filter to be trustworthy. Reported with counts so the caller
    /// can explain the gap instead of silently returning a partial set.
    /// </summary>
    MetadataUnavailable,

    /// <summary>Metadata was adequate and nothing matched.</summary>
    NoMatches,

    /// <summary>Fewer than the minimum bound matched. No padding with unrelated words.</summary>
    InsufficientMatches
}

/// <summary>
/// One selected word, projected for display. These are the learner's own
/// vocabulary rows, so term and gloss are owned display data.
/// </summary>
/// <remarks>
/// Shared exposes this for API projection into a learner-facing plan or receipt.
/// It must NOT be handed to an agent tool: due-item terms and glosses are
/// embargoed from model input.
/// </remarks>
public sealed record VocabularyFocusItem
{
    public required string VocabularyWordId { get; init; }
    public string? TargetLanguageTerm { get; init; }
    public string? NativeLanguageTerm { get; init; }
    public VocabularyPartOfSpeech? PartOfSpeech { get; init; }

    /// <summary>Why this word was ranked where it was (bounded, non-free-text).</summary>
    public required VocabularyFocusMatchReason MatchReason { get; init; }
}

/// <summary>Bounded ranking rationale for a selected word.</summary>
public enum VocabularyFocusMatchReason
{
    /// <summary>Scheduled for review on or before today.</summary>
    DueForReview = 0,

    /// <summary>Practiced, but mastery is still low.</summary>
    WeakMastery,

    /// <summary>Owned but never practiced.</summary>
    NeverPracticed,

    /// <summary>Practiced and not due; included for variety, least recently practiced first.</summary>
    LeastRecentlyPracticed
}

/// <summary>The normalized outcome of a vocabulary focus resolution.</summary>
public sealed record VocabularyFocusResult
{
    public required VocabularyFocusOutcome Outcome { get; init; }

    /// <summary>Selected words in final rank order. Empty unless <see cref="Outcome"/> is Success.</summary>
    public IReadOnlyList<VocabularyFocusItem> Items { get; init; } = Array.Empty<VocabularyFocusItem>();

    /// <summary>Learner's original wording, echoed for display.</summary>
    public string? DisplayDescription { get; init; }

    public int RequestedCount { get; init; }

    /// <summary>Distinct vocabulary words the learner owns.</summary>
    public int OwnedCandidateCount { get; init; }

    /// <summary>Owned words carrying a part of speech (the grounding coverage).</summary>
    public int ClassifiedCandidateCount { get; init; }

    /// <summary>Owned words satisfying the filter before the count bound.</summary>
    public int MatchedCount { get; init; }

    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();

    public bool IsSuccess => Outcome == VocabularyFocusOutcome.Success;

    /// <summary>The selected ids, in rank order — the trusted set the planner consumes.</summary>
    public IReadOnlyList<string> SelectedVocabularyWordIds =>
        Items.Select(i => i.VocabularyWordId).ToList();

    public static VocabularyFocusResult Invalid(VocabularyFocusRequest request, IReadOnlyList<string> errors) =>
        new()
        {
            Outcome = VocabularyFocusOutcome.InvalidFocus,
            DisplayDescription = request.DisplayDescription,
            RequestedCount = request.RequestedCount,
            ValidationErrors = errors
        };
}

/// <summary>
/// Resolves a bounded focus request to the learner's own vocabulary word ids.
/// </summary>
public interface IVocabularyFocusResolver
{
    /// <summary>
    /// Resolve for the request-scoped learner. Never queries unfiltered: an
    /// unresolvable scope returns typed no-data.
    /// </summary>
    Task<VocabularyFocusResult> ResolveAsync(VocabularyFocusRequest request, CancellationToken ct = default);
}
