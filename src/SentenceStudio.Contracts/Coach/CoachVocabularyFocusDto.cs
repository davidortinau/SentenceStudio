using SentenceStudio.Contracts.Wire;
namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The vocabulary focus the server selected for a plan, projected for display.
/// </summary>
/// <remarks>
/// <para>
/// Application-owned in both directions. The model never proposes the contents of this shape and
/// never sees it: it may only describe a focus in the learner's words, which the server maps to a
/// canonical code and resolves against the learner's own vocabulary. What comes back here is the
/// resolver's answer, not the model's claim.
/// </para>
/// <para>
/// It carries the learner's own words because a focus the learner cannot see is a focus they
/// cannot check. It carries nothing else: no vocabulary identifiers, no due dates, no mastery or
/// progress, no metadata stamp, and no query. Those stay server-side, where the acceptance path
/// reads them.
/// </para>
/// </remarks>
public sealed class CoachVocabularyFocusDto
{
    /// <summary>
    /// The canonical, server-owned focus code, for example <c>grammar.action-verb</c>. Stable
    /// across languages and safe to branch on.
    /// </summary>
    public required string FocusCode { get; init; }

    /// <summary>The localized label for this focus, for example "action verbs".</summary>
    public required string DisplayLabel { get; init; }

    /// <summary>Owned words that matched the focus before the count bound was applied.</summary>
    public required int EligibleCount { get; init; }

    /// <summary>Words in the selected set. Never larger than <see cref="EligibleCount"/>.</summary>
    public required int SelectedCount { get; init; }

    /// <summary>
    /// The selected words in the server's rank order. The order is meaningful and a client should
    /// preserve it.
    /// </summary>
    public IReadOnlyList<CoachVocabularyFocusWordDto> Words { get; init; } =
        Array.Empty<CoachVocabularyFocusWordDto>();
}

/// <summary>One selected word, for display only.</summary>
/// <remarks>
/// The learner's own vocabulary row, projected down to what a client needs to render and speak it.
/// No identifier, no scheduling, no mastery — nothing that would let a client or a log reconstruct
/// the review queue.
/// </remarks>
public sealed class CoachVocabularyFocusWordDto
{
    /// <summary>The word in the language being studied, in its own script.</summary>
    public required string TargetText { get; init; }

    /// <summary>
    /// The BCP-47 tag for <see cref="TargetText"/>, resolved by the server. A client can rely on
    /// it for font selection and speech.
    /// </summary>
    public required string TargetLanguageTag { get; init; }

    /// <summary>The learner's own translation, when they have one. Null when they do not.</summary>
    public string? DisplayText { get; init; }

    /// <summary>The BCP-47 tag for <see cref="DisplayText"/>. Null when there is no translation.</summary>
    public string? DisplayLanguageTag { get; init; }
}

/// <summary>What a change did to the vocabulary focus.</summary>
/// <remarks>
/// Appended-only: this travels inside receipts, which are stored and replayed.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachVocabularyFocusStatus.Unchanged), WireEnumFallbackKind.SafeZero,
    "Unchanged is \u201cthe change did not touch the focus\u201d, so the client shows no focus claim at "
    + "all. Applied, Cleared and Restored each assert something specific happened to the learner\u2019s "
    + "word list.")]
public enum CoachVocabularyFocusStatus
{
    /// <summary>The change did not touch the focus.</summary>
    Unchanged = 0,

    /// <summary>The change set or replaced the focus. <c>Focus</c> is the one now in force.</summary>
    Applied,

    /// <summary>The change removed the focus. <c>Focus</c> is null, and that is the answer.</summary>
    Cleared,

    /// <summary>An undo put an earlier focus back. <c>Focus</c> is the restored one.</summary>
    Restored
}

/// <summary>
/// What one change did to the vocabulary focus, and the focus in force afterwards.
/// </summary>
/// <remarks>
/// <para>
/// A nullable focus alone cannot express this. "No focus" and "the focus was cleared" look
/// identical, so a client would have to diff against the active constraints to tell them apart —
/// and would show the previous word list next to a change that removed it. The status says which
/// happened, and <see cref="Focus"/> is always the state <b>after</b> the operation this receipt
/// describes, never a stale earlier one.
/// </para>
/// </remarks>
public sealed class CoachVocabularyFocusChangeDto
{
    public required CoachVocabularyFocusStatus Status { get; init; }

    /// <summary>
    /// The focus in force after this change. Null when the status is
    /// <see cref="CoachVocabularyFocusStatus.Cleared"/>, and null when nothing was in force.
    /// </summary>
    public CoachVocabularyFocusDto? Focus { get; init; }

    /// <summary>The unchanged case, carrying whatever remains in force.</summary>
    public static CoachVocabularyFocusChangeDto Unchanged(CoachVocabularyFocusDto? focus) =>
        new() { Status = CoachVocabularyFocusStatus.Unchanged, Focus = focus };
}
