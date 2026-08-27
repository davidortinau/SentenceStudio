namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// Hard bounds for learner memory. Every one of these is enforced before anything is encrypted or
/// stored, so a bound can never be exceeded by a row that already exists.
/// </summary>
public static class CoachMemoryLimits
{
    /// <summary>Identifier column width, matching the history tables.</summary>
    public const int IdMaxLength = 64;

    /// <summary>Owner column width, matching <c>UserProfile.Id</c>.</summary>
    public const int UserProfileIdMaxLength = 64;

    /// <summary>Tenant hint width. Metadata only; never queried, never keyed.</summary>
    public const int TenantIdMaxLength = 64;

    /// <summary>BCP-47 tag width.</summary>
    public const int LanguageCodeMaxLength = 16;

    /// <summary>Derived scope key width: <c>global</c> or <c>lang:{tag}</c>.</summary>
    public const int ScopeKeyMaxLength = 32;

    /// <summary>A study goal is one short line, not a paragraph.</summary>
    public const int StudyGoalMaxLength = 160;

    /// <summary>
    /// The longest committed learner message the caller may hand in for evidence verification.
    /// The message itself is never stored; this only bounds the work done to verify a span.
    /// </summary>
    public const int EvidenceSourceMaxLength = 8_000;

    /// <summary>An evidence span must be a real quotation, not a single character.</summary>
    public const int EvidenceSpanMinLength = 3;

    /// <summary>An evidence span longer than this is a transcript, not a citation.</summary>
    public const int EvidenceSpanMaxLength = 400;

    /// <summary>Default page size for the memory list.</summary>
    public const int PageSizeDefault = 20;

    /// <summary>Largest page a caller may request.</summary>
    public const int PageSizeMax = 50;

    /// <summary>How many active facts one owner may hold. Past this, new approvals are refused.</summary>
    public const int ActiveFactsMax = 32;

    /// <summary>How many undecided candidates one owner may hold. Past this, new candidates are refused.</summary>
    public const int CandidatesMax = 16;

    /// <summary>The most facts the selector will ever place in one prompt.</summary>
    public const int ContextFactsMax = 8;

    /// <summary>The estimated token ceiling for the whole memory block.</summary>
    public const int ContextTokensMax = 512;

    /// <summary>Ciphertext column width. Generous, because protection expands the payload.</summary>
    public const int ProtectedValueMaxLength = 4_000;
}

/// <summary>
/// Versions that travel with a stored memory row.
/// </summary>
/// <remarks>
/// The value version labels the JSON shape inside the ciphertext. The protection version is owned
/// by <c>ICoachContentProtector</c> and is copied onto the row so a key-ring roll can be detected
/// without decrypting first.
/// </remarks>
public static class CoachMemorySchema
{
    /// <summary>The current typed-value JSON shape.</summary>
    public const int ValueVersion = 1;

    /// <summary>The scope key used for facts the learner marked global.</summary>
    public const string GlobalScopeKey = "global";

    /// <summary>Builds the durable scope key for a language-scoped fact.</summary>
    public static string LanguageScopeKey(string languageCode) => $"lang:{languageCode}";
}
