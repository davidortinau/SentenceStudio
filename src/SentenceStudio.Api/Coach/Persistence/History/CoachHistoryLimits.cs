namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// The bounds durable history enforces before anything reaches the database.
/// </summary>
/// <remarks>
/// These are storage limits, not product copy limits. They exist so a hostile or buggy caller
/// cannot grow a row, a page, or an encrypted payload without bound. Encryption hides content
/// but not size, so every bound is checked on the plaintext, before protection.
/// </remarks>
public static class CoachHistoryLimits
{
    /// <summary>Maximum length of an opaque identifier column.</summary>
    public const int IdMaxLength = 64;

    /// <summary>Maximum length of the owning user profile id.</summary>
    public const int UserProfileIdMaxLength = 64;

    /// <summary>Maximum length of the forward-compatibility tenant id.</summary>
    public const int TenantIdMaxLength = 64;

    /// <summary>Maximum length of the non-sensitive BCP-47 target-language code.</summary>
    public const int TargetLanguageCodeMaxLength = 16;

    /// <summary>Maximum plaintext title length, checked before protection.</summary>
    public const int TitleMaxLength = 120;

    /// <summary>Default conversation page size.</summary>
    public const int ConversationPageDefault = 20;

    /// <summary>Maximum conversation page size. A larger request is clamped, never honoured.</summary>
    public const int ConversationPageMax = 50;

    /// <summary>Default message page size.</summary>
    public const int MessagePageDefault = 50;

    /// <summary>Maximum message page size. A larger request is clamped, never honoured.</summary>
    public const int MessagePageMax = 100;

    /// <summary>Maximum serialized payload size in bytes, measured before protection.</summary>
    public const int MessagePayloadMaxBytes = 32 * 1024;

    /// <summary>Maximum length of any single visible text field in a payload.</summary>
    public const int TextMaxLength = 8_000;

    /// <summary>Maximum number of answer blocks in one structured payload.</summary>
    public const int AnswerBlockMax = 16;

    /// <summary>Maximum number of spans in one answer block.</summary>
    public const int AnswerSpanMax = 16;

    /// <summary>Maximum length of one answer span.</summary>
    public const int AnswerSpanTextMaxLength = 2_000;

    /// <summary>Maximum number of learner-visible change lines in a suggestion snapshot.</summary>
    public const int SuggestionLineMax = 12;

    /// <summary>Maximum length of one suggestion or receipt line.</summary>
    public const int LineMaxLength = 400;

    /// <summary>Maximum length of a client-supplied idempotency key.</summary>
    public const int IdempotencyKeyMaxLength = 128;

    /// <summary>Maximum length of a content-free operational error code.</summary>
    public const int ErrorCodeMaxLength = 64;

    /// <summary>Maximum length of the worker identity that holds a turn lease.</summary>
    public const int LeaseOwnerMaxLength = 64;

    /// <summary>Maximum length of a stored digest column (hex SHA-256 is 64 characters).</summary>
    public const int DigestMaxLength = 128;
}
