using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>One approved fact chosen for the current turn.</summary>
/// <param name="FactId">The fact identifier, for the used-stamp and for audit.</param>
/// <param name="Kind">Which closed kind it is.</param>
/// <param name="Scope">Language-scoped or global.</param>
/// <param name="TargetLanguageCode">The scoped language, or null when global.</param>
/// <param name="Value">
/// The exact normalized line the learner approved. A value, never an instruction: the formatter is
/// the only thing allowed to decide how it is presented.
/// </param>
/// <param name="Provenance">How the fact came to exist.</param>
/// <param name="EstimatedTokens">The formatter's estimate for this item alone.</param>
public sealed record CoachMemoryContextItem(
    string FactId,
    CoachMemoryKind Kind,
    CoachMemoryScope Scope,
    string? TargetLanguageCode,
    string Value,
    CoachMemoryProvenance Provenance,
    int EstimatedTokens);

/// <summary>Why a selection returned what it did.</summary>
public enum CoachMemoryContextOutcome
{
    /// <summary>Facts were selected.</summary>
    Selected = 0,

    /// <summary>The owner holds no eligible facts.</summary>
    Empty = 1,

    /// <summary>The memory feature is switched off.</summary>
    Disabled = 2,

    /// <summary>Selection is paused. The learner's facts are untouched.</summary>
    Paused = 3,

    /// <summary>No owner authority was in scope.</summary>
    NoOwner = 4,

    /// <summary>The store could not be reached. The turn proceeds without memory.</summary>
    StoreUnavailable = 5
}

/// <summary>The facts chosen for one turn.</summary>
/// <param name="Items">The chosen facts, in the order they should be presented.</param>
/// <param name="EstimatedTokens">The estimate for the whole block, header included.</param>
/// <param name="Outcome">Why the list looks the way it does.</param>
public sealed record CoachMemoryContextResult(
    IReadOnlyList<CoachMemoryContextItem> Items,
    int EstimatedTokens,
    CoachMemoryContextOutcome Outcome)
{
    /// <summary>An empty selection carrying a reason.</summary>
    public static CoachMemoryContextResult Empty(CoachMemoryContextOutcome outcome) =>
        new(Array.Empty<CoachMemoryContextItem>(), 0, outcome);
}

/// <summary>What the caller knows about the current turn.</summary>
/// <param name="Owner">The learner.</param>
/// <param name="TargetLanguageCode">
/// The language the learner is studying right now. Facts scoped to another language are not
/// eligible; when this is null, only explicitly global facts are.
/// </param>
/// <param name="Category">The closed classification of the turn.</param>
/// <param name="ExcludedKinds">
/// Kinds the current request or the learner's live app settings already decide. Current data always
/// outranks memory, so anything listed here is dropped before ranking rather than argued with in
/// the prompt.
/// </param>
public sealed record CoachMemoryContextRequest(
    CoachOwner Owner,
    string? TargetLanguageCode,
    CoachMemoryTurnCategory Category,
    IReadOnlyCollection<CoachMemoryKind>? ExcludedKinds = null);

/// <summary>
/// Chooses which approved facts belong in the current turn.
/// </summary>
/// <remarks>
/// Deterministic by construction. The input is a closed category, not the learner's text, so the
/// same owner and the same category always produce the same list. There is no similarity search,
/// no embedding, and no model involvement in selection.
/// </remarks>
public interface ICoachMemoryContextSelector
{
    /// <summary>Selects the facts for one turn.</summary>
    Task<CoachMemoryContextResult> SelectAsync(
        CoachMemoryContextRequest request,
        CancellationToken cancellationToken = default);
}
