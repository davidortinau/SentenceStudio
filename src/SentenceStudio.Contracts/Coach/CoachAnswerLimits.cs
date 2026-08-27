namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The bounds on a pedagogical answer. Enforced by the server on every turn, before anything is
/// shown or stored.
/// </summary>
/// <remarks>
/// An answer is longer-form than a plan message, so it gets its own budget rather than widening
/// the 400-character plan/status bound that keeps receipts and notices terse. The totals are
/// small on purpose: a coach answer is a short, direct explanation, and a cap that a normal
/// answer never approaches is a cap that catches a runaway.
/// </remarks>
public static class CoachAnswerLimits
{
    /// <summary>Largest number of characters across every span of every block.</summary>
    public const int MaxTotalCharacters = 1600;

    /// <summary>Fewest blocks an answer may contain.</summary>
    public const int MinBlocks = 1;

    /// <summary>Largest number of blocks an answer may contain.</summary>
    public const int MaxBlocks = 8;

    /// <summary>Fewest spans a block may contain.</summary>
    public const int MinSpansPerBlock = 1;

    /// <summary>Largest number of spans a block may contain.</summary>
    public const int MaxSpansPerBlock = 6;

    /// <summary>Largest number of characters in one span.</summary>
    public const int MaxSpanCharacters = 320;

    /// <summary>Largest number of characters in a block label.</summary>
    public const int MaxBlockLabelCharacters = 60;

    /// <summary>Largest number of retrieval-prompt blocks in one answer.</summary>
    public const int MaxRetrievalPrompts = 1;

    /// <summary>Largest number of characters in the flattened plain-text fallback.</summary>
    public const int MaxFallbackCharacters = MaxTotalCharacters;
}
