namespace SentenceStudio.Api.Coach.Tools.Observation;

/// <summary>
/// How one tool call ended, as a closed set.
/// </summary>
/// <remarks>
/// <para>
/// Stored as an ordinal by W4b's trace section, so members may only be appended — inserting one
/// silently re-labels every row already written.
/// </para>
/// <para>
/// There is deliberately no member for "refused before it ran". A budget refusal is raised by
/// <c>BudgetedAIFunction</c>, which wraps this seam from the outside, so the seam never sees the
/// call at all and produces no observation for it. That is the shipped ordering, and a member here
/// would invite somebody to record the same refusal twice — once as a tool failure and once at the
/// turn boundary from <c>CoachToolCallBudget.Used</c>.
/// </para>
/// </remarks>
public enum CoachToolCallOutcome
{
    /// <summary>Never emitted. Present so a default-constructed observation is visibly incomplete.</summary>
    Unspecified = 0,

    /// <summary>The tool returned an answer.</summary>
    Succeeded = 1,

    /// <summary>
    /// The tool refused in a bounded, typed way — a <c>CoachToolException</c> the model is meant
    /// to read and act on.
    /// </summary>
    Refused = 2,

    /// <summary>
    /// The tool threw something that is not a bounded refusal. The exception object never reaches
    /// this seam; only the fact that one occurred does.
    /// </summary>
    Faulted = 3
}

/// <summary>
/// Which arguments a tool call carried. Presence only — never a value.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of a mask rather than a payload. "The model asked for a page of results" is a
/// fact worth having when reading a turn back; "the model asked for the word 사과" is learner
/// content, and a trace that carried it would be a transcript with extra steps.
/// </para>
/// <para>
/// <see cref="Identifier"/> collapses <c>wordId</c>, <c>skillId</c> and <c>resourceId</c> into one
/// flag on purpose. The fact being recorded is "the caller named a single row", which is the same
/// fact <c>CoachScopeFilters.SingleIdentifier</c> already names, and collapsing it means a fourth
/// id-taking tool needs no change here.
/// </para>
/// <para>
/// <see cref="Unrecognized"/> is the non-vacuity hook. A tool that grows an argument this mask does
/// not know sets it, and <c>CoachToolObservationSeamTests</c> asserts no enabled tool ever does —
/// so the mask cannot silently fall behind the tool set.
/// </para>
/// </remarks>
[Flags]
public enum CoachToolArgumentMask
{
    /// <summary>The call carried no arguments beyond the cancellation token.</summary>
    None = 0,

    /// <summary>A practice window was supplied.</summary>
    Window = 1 << 0,

    /// <summary>A category-tag limit was supplied.</summary>
    MaxCategoryTags = 1 << 1,

    /// <summary>A result limit was supplied.</summary>
    MaxResults = 1 << 2,

    /// <summary>Plan-preview constraints were supplied.</summary>
    Constraints = 1 << 3,

    /// <summary>A search query was supplied. Its text is never recorded.</summary>
    Query = 1 << 4,

    /// <summary>A single row was named by identifier. The identifier itself is never recorded.</summary>
    Identifier = 1 << 5,

    /// <summary>The typed argument object of a write-intent tool was supplied.</summary>
    WriteArguments = 1 << 6,

    /// <summary>
    /// An argument this mask has no member for. Never expected from a registered tool; asserted
    /// absent across the enabled registry.
    /// </summary>
    Unrecognized = 1 << 7
}
