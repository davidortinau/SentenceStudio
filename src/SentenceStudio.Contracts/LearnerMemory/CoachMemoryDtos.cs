using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.LearnerMemory;

/// <summary>
/// A typed memory value. Exactly one branch is populated, chosen by <see cref="Kind"/>.
/// </summary>
/// <remarks>
/// There is no free-text branch beyond <see cref="StudyGoalText"/>, and that one is bounded and
/// content-screened. A memory value is data the learner can read back verbatim; it is never an
/// instruction, a tool argument, or a route.
/// </remarks>
public sealed class CoachMemoryValueDto
{
    /// <summary>Which branch is populated.</summary>
    public CoachMemoryKind Kind { get; set; }

    /// <summary>The learner's study goal. Populated only for <see cref="CoachMemoryKind.PersistentStudyGoal"/>.</summary>
    public string? StudyGoalText { get; set; }

    /// <summary>Populated only for <see cref="CoachMemoryKind.ExplanationDepth"/>.</summary>
    public CoachMemoryExplanationDepth? ExplanationDepth { get; set; }

    /// <summary>Populated only for <see cref="CoachMemoryKind.CorrectionTiming"/>.</summary>
    public CoachMemoryCorrectionTiming? CorrectionTiming { get; set; }

    /// <summary>
    /// The register the learner wants worked examples written in. Populated only for
    /// <see cref="CoachMemoryKind.ExampleRegister"/>.
    /// </summary>
    /// <remarks>
    /// The name matches the product concept. This type is deliberately outside the
    /// <c>SentenceStudio.Contracts.Coach</c> namespace, so it is not part of the model and tool
    /// output graph the coach embargo scanner guards. It is validated instead by
    /// <c>CoachMemoryContractValidator</c>, which is bounded to the memory CRUD surface.
    /// Renaming a product concept to slip past a scanner would hide the mismatch rather than
    /// fix it.
    /// </remarks>
    public CoachMemoryExampleRegister? ExampleRegister { get; set; }
}

/// <summary>
/// One remembered fact as the learner sees it.
/// </summary>
/// <param name="Id">The fact identifier. Opaque to the client.</param>
/// <param name="Kind">Which closed kind this fact is.</param>
/// <param name="Status">Where the fact sits in its lifecycle.</param>
/// <param name="Scope">Whether the fact is language-scoped or explicitly global.</param>
/// <param name="TargetLanguageCode">The scoped language, or null when the scope is global.</param>
/// <param name="Value">The typed value, exactly as stored.</param>
/// <param name="DisplayText">
/// The normalized single-line rendering of <paramref name="Value"/>. The learner sees the same
/// characters the selector would put in a prompt.
/// </param>
/// <param name="Provenance">How the fact came to exist.</param>
/// <param name="EvidenceCount">How many explicit learner statements support the fact.</param>
/// <param name="CreatedAtUtc">When the candidate was first recorded.</param>
/// <param name="UpdatedAtUtc">When the row last changed.</param>
/// <param name="ConfirmedAtUtc">When the learner approved it, or null.</param>
/// <param name="LastUsedAtUtc">When it was last selected into a prompt, or null.</param>
/// <param name="ExpiresAtUtc">When it stops being eligible, or null when it does not expire.</param>
/// <param name="SupersedesId">The fact this one replaced, or null.</param>
/// <param name="Version">
/// The concurrency version. Every write must echo this back; a mismatch is a conflict, never a
/// silent overwrite.
/// </param>
public sealed record CoachMemoryFactDto(
    string Id,
    CoachMemoryKind Kind,
    CoachMemoryStatus Status,
    CoachMemoryScope Scope,
    string? TargetLanguageCode,
    CoachMemoryValueDto Value,
    string DisplayText,
    CoachMemoryProvenance Provenance,
    int EvidenceCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? ConfirmedAtUtc,
    DateTime? LastUsedAtUtc,
    DateTime? ExpiresAtUtc,
    string? SupersedesId,
    int Version);

/// <summary>One bounded page of facts.</summary>
/// <param name="Items">The facts, newest first.</param>
/// <param name="NextCursor">An opaque owner-bound cursor, or null at the end of the list.</param>
public sealed record CoachMemoryPageDto(IReadOnlyList<CoachMemoryFactDto> Items, string? NextCursor);

/// <summary>Approves a candidate, optionally editing the value first.</summary>
/// <param name="ExpectedVersion">The version the learner saw.</param>
/// <param name="EditedValue">
/// An optional replacement value. When supplied it must be the same <see cref="CoachMemoryKind"/>
/// as the candidate; a kind change is a rejected request, not an implicit new fact.
/// </param>
public sealed record CoachMemoryApproveRequest(int ExpectedVersion, CoachMemoryValueDto? EditedValue = null);

/// <summary>Declines a candidate without remembering anything.</summary>
/// <param name="ExpectedVersion">The version the learner saw.</param>
public sealed record CoachMemoryRejectRequest(int ExpectedVersion);

/// <summary>Edits the value of an active fact.</summary>
/// <param name="ExpectedVersion">The version the learner saw.</param>
/// <param name="Value">The replacement value. Must match the fact's kind.</param>
public sealed record CoachMemoryEditRequest(int ExpectedVersion, CoachMemoryValueDto Value);

/// <summary>The result of forgetting everything.</summary>
/// <param name="Forgotten">How many rows were removed.</param>
public sealed record CoachMemoryForgetAllResponse(int Forgotten);

/// <summary>
/// Content-free problem types for the memory endpoints.
/// </summary>
/// <remarks>
/// A rejected value never echoes the value back. The client maps these to localized copy.
/// </remarks>
public static class CoachMemoryProblemTypes
{
    /// <summary>The request body or path was not usable.</summary>
    public const string InvalidRequest = "https://sentencestudio.app/problems/coach-memory-invalid-request";

    /// <summary>The value failed the content policy for saved preferences.</summary>
    public const string ValueRejected = "https://sentencestudio.app/problems/coach-memory-value-rejected";

    /// <summary>The expected version did not match, or an active fact of the same kind exists.</summary>
    public const string Conflict = "https://sentencestudio.app/problems/coach-memory-conflict";

    /// <summary>The memory store could not be reached.</summary>
    public const string Unavailable = "https://sentencestudio.app/problems/coach-memory-unavailable";
}

/// <summary>
/// Why a value was refused. Content-free by construction: no member names any part of the value.
/// </summary>
/// <remarks>
/// <b>Deliberately carries no wire-tolerance fallback.</b> It never appears on a client DTO — a
/// refused value comes back as an RFC 7807 problem whose type is one of
/// <see cref="CoachMemoryProblemTypes"/>, and this enum stays inside the server's own validation
/// result. Annotating it would mean choosing a fallback, and the only zero member is
/// <see cref="None"/>, which says the value was <em>accepted</em>: precisely the claim a client
/// must never make on a refusal. If this ever does reach a wire DTO, the wire-enum policy test
/// fails and that decision gets made deliberately rather than inherited from here.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachMemoryValueRejection
{
    /// <summary>The value was accepted.</summary>
    None = 0,

    /// <summary>No branch was populated for the declared kind.</summary>
    MissingValue = 1,

    /// <summary>A branch was populated that does not belong to the declared kind.</summary>
    WrongBranch = 2,

    /// <summary>The text was empty after normalization.</summary>
    Empty = 3,

    /// <summary>The text was longer than the kind allows.</summary>
    TooLong = 4,

    /// <summary>The text carried control characters or line breaks.</summary>
    ControlCharacters = 5,

    /// <summary>The text carried a link.</summary>
    Link = 6,

    /// <summary>The text looked like a credential.</summary>
    Secret = 7,

    /// <summary>The text looked like a command or code.</summary>
    Command = 8,

    /// <summary>The text carried a chat role or template marker.</summary>
    RoleMarker = 9,

    /// <summary>The text tried to instruct the model.</summary>
    Instruction = 10,

    /// <summary>The text carried identifying or sensitive personal detail.</summary>
    SensitivePersonalDetail = 11,

    /// <summary>The text looked like an answer to graded material.</summary>
    AssessmentAnswer = 12,

    /// <summary>The declared kind is not supported.</summary>
    UnsupportedKind = 13,

    /// <summary>The scope was inconsistent: a language-scoped fact needs a language, a global one must not carry one.</summary>
    InvalidScope = 14
}
