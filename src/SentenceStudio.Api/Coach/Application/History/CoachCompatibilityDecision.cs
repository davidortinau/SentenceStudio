namespace SentenceStudio.Api.Coach.Application.History;

/// <summary>
/// The deterministic, model-free decisions the compatibility <c>/sessions</c> routes can make.
/// </summary>
/// <remarks>
/// These are taps, not turns. The learner presses accept, reject, or undo; no text is submitted
/// and no model runs. They still need the durable envelope, because each one can write plan state
/// and produce a receipt the learner is entitled to see in their history afterwards.
/// </remarks>
public enum CoachCompatibilityDecisionKind
{
    /// <summary>The learner accepted the open suggestion.</summary>
    AcceptSuggestion = 0,

    /// <summary>The learner rejected the open suggestion.</summary>
    RejectSuggestion,

    /// <summary>The learner undid the last applied change.</summary>
    Undo
}

/// <summary>One decision taken through a compatibility route.</summary>
/// <param name="Kind">Which decision.</param>
/// <param name="SuggestionId">The suggestion being answered. Null for an undo.</param>
/// <param name="ClientTurnId">
/// The client's retry key, when it sent one. Null means the caller supplied no key, and the
/// decision is treated as a fresh request — the pre-history behaviour, not a silent replay.
/// </param>
public sealed record CoachCompatibilityDecision(
    CoachCompatibilityDecisionKind Kind,
    string? SuggestionId,
    string? ClientTurnId);
