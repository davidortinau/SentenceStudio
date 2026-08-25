namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// A coach suggestion that waits for a decision.
/// A suggestion never changes the plan.
/// The plan changes only after a clear acceptance.
/// </summary>
public sealed class PendingCoachSuggestionDto
{
    /// <summary>
    /// The suggestion identifier. Send this value with an acceptance or a rejection.
    /// The server accepts the current suggestion only.
    /// </summary>
    public required string SuggestionId { get; init; }

    /// <summary>The constraint change this suggestion proposes.</summary>
    public required CoachConstraintDeltaDto Delta { get; init; }

    /// <summary>The localized reason for this suggestion.</summary>
    public required string Rationale { get; init; }

    /// <summary>The read-only plan preview for this suggestion.</summary>
    public required CoachPlanDiffDto Preview { get; init; }

    /// <summary>
    /// The vocabulary focus this suggestion would apply, with the exact words the server selected.
    /// Null when the suggestion does not change the focus.
    /// </summary>
    /// <remarks>
    /// The authoritative shape for a client to render. <see cref="Rationale"/> is a deterministic
    /// English fallback built from the same numbers; a localizing client should use this instead.
    /// The words are the frozen selection, so what is shown here is what acceptance applies.
    /// </remarks>
    public CoachVocabularyFocusDto? VocabularyFocus { get; init; }

    /// <summary>The evidence behind this suggestion.</summary>
    public IReadOnlyList<CoachEvidenceDto> Evidence { get; init; } = Array.Empty<CoachEvidenceDto>();

    /// <summary>The localized label of the accept action.</summary>
    public required string AcceptLabel { get; init; }

    /// <summary>The localized label of the reject action.</summary>
    public required string RejectLabel { get; init; }

    /// <summary>The time the server created this suggestion.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The time this suggestion expires. The suggestion expires with the session.</summary>
    public required DateTime ExpiresAtUtc { get; init; }
}
