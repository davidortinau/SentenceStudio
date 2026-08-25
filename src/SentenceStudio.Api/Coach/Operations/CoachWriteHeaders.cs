namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// Header names the write approval routes use.
/// </summary>
/// <remarks>
/// The confirmation secret travels as a header rather than as a field on a request body. That
/// keeps it out of the shared contracts assembly, where the embargo scanner refuses any member
/// naming a credential, and out of request bodies, which are the part of a request most likely to
/// be logged or replayed. The constant lives beside the ledger rather than in the contracts
/// assembly for the same reason.
/// </remarks>
public static class CoachWriteHeaders
{
    /// <summary>The one-use secret that authorises a protected write.</summary>
    public const string Confirmation = "X-Coach-Write-Confirmation";
}
