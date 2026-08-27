using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// The outcome of a memory operation. Every store method returns one of these instead of throwing,
/// so a caller cannot accidentally turn a refusal into a 500.
/// </summary>
public enum CoachMemoryStatusCode
{
    /// <summary>The operation completed.</summary>
    Success = 0,

    /// <summary>No owner authority was supplied. Nothing was read or written.</summary>
    NoOwner,

    /// <summary>The row does not exist, or does not belong to this owner. The two are indistinguishable on purpose.</summary>
    NotFound,

    /// <summary>The request was malformed or out of bounds.</summary>
    InvalidRequest,

    /// <summary>The value failed the content policy.</summary>
    ValueRejected,

    /// <summary>The supplied evidence span was not found verbatim in the committed learner message.</summary>
    EvidenceMismatch,

    /// <summary>The expected version did not match, or an active fact already occupies the slot.</summary>
    Conflict,

    /// <summary>The owner is at the candidate or active-fact ceiling.</summary>
    LimitReached,

    /// <summary>The cursor was not readable for this owner.</summary>
    InvalidCursor,

    /// <summary>The memory feature is switched off.</summary>
    Disabled,

    /// <summary>The store could not be reached.</summary>
    Unavailable
}

/// <summary>
/// A fact as the application layer sees it: decrypted, screened, and owner-checked.
/// </summary>
public sealed record CoachMemoryFactRecord(
    string Id,
    CoachMemoryKind Kind,
    CoachMemoryStatus Status,
    CoachMemoryScope Scope,
    string? TargetLanguageCode,
    CoachMemoryStoredValue Value,
    CoachMemoryProvenance Provenance,
    int EvidenceCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ConfirmedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt,
    string? SupersedesId,
    string? SourceConversationId,
    int Version)
{
    /// <summary>Maps to the public DTO. The display text is the same line a prompt would carry.</summary>
    public CoachMemoryFactDto ToDto() => new(
        Id,
        Kind,
        Status,
        Scope,
        TargetLanguageCode,
        Value.ToDto(),
        Value.DisplayText,
        Provenance,
        EvidenceCount,
        CreatedAt,
        UpdatedAt,
        ConfirmedAt,
        LastUsedAt,
        ExpiresAt,
        SupersedesId,
        Version);
}

/// <summary>One fact, or a refusal.</summary>
public sealed record CoachMemoryResult(
    CoachMemoryStatusCode Status,
    CoachMemoryFactRecord? Fact = null,
    CoachMemoryValueRejection Rejection = CoachMemoryValueRejection.None)
{
    /// <summary>True when a fact came back.</summary>
    public bool IsSuccess => Status == CoachMemoryStatusCode.Success && Fact is not null;

    /// <summary>Builds a refusal.</summary>
    public static CoachMemoryResult Failed(
        CoachMemoryStatusCode status,
        CoachMemoryValueRejection rejection = CoachMemoryValueRejection.None) =>
        new(status, null, rejection);
}

/// <summary>One bounded page of facts, or a refusal.</summary>
public sealed record CoachMemoryPage(
    CoachMemoryStatusCode Status,
    IReadOnlyList<CoachMemoryFactRecord> Items,
    string? NextCursor)
{
    /// <summary>Builds an empty page carrying a refusal reason.</summary>
    public static CoachMemoryPage Empty(CoachMemoryStatusCode status) =>
        new(status, Array.Empty<CoachMemoryFactRecord>(), null);
}

/// <summary>The result of forgetting everything for one owner.</summary>
public sealed record CoachMemoryForgetAllResult(CoachMemoryStatusCode Status, int Forgotten)
{
    /// <summary>Builds a refusal.</summary>
    public static CoachMemoryForgetAllResult Failed(CoachMemoryStatusCode status) => new(status, 0);
}

/// <summary>Which slice of an owner's memory to list.</summary>
public enum CoachMemoryListFilter
{
    /// <summary>Approved facts only.</summary>
    Active = 0,

    /// <summary>Undecided candidates, including conflicting ones.</summary>
    Candidates = 1,

    /// <summary>Everything the owner holds, in one list.</summary>
    All = 2
}

/// <summary>
/// Creates a candidate from an explicit learner statement.
/// </summary>
/// <param name="Value">The typed value the learner stated.</param>
/// <param name="Scope">Language-scoped or explicitly global.</param>
/// <param name="TargetLanguageCode">Required when the scope is a language; must be null when global.</param>
/// <param name="LearnerMessageText">
/// The committed learner message, supplied by the trusted application layer. It is used only to
/// verify <paramref name="EvidenceSpan"/> and is never stored.
/// </param>
/// <param name="EvidenceSpan">
/// The exact substring of <paramref name="LearnerMessageText"/> the learner used. Verified, counted,
/// and then discarded.
/// </param>
/// <param name="SourceConversationId">Opaque provenance metadata.</param>
/// <param name="SourceMessageId">Opaque provenance metadata.</param>
/// <param name="ObservedAt">When the learner said it. Defaults to now.</param>
public sealed record CreateCoachMemoryCandidateRequest(
    CoachMemoryStoredValue Value,
    CoachMemoryScope Scope,
    string? TargetLanguageCode,
    string LearnerMessageText,
    string EvidenceSpan,
    string? SourceConversationId = null,
    string? SourceMessageId = null,
    DateTime? ObservedAt = null);
