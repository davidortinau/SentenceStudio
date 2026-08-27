namespace SentenceStudio.Contracts.Feedback;

public sealed class FeedbackRequest
{
    public string Description { get; set; } = string.Empty;
    public string? FeedbackType { get; set; }
    public ClientMetadata? ClientMetadata { get; set; }
}

/// <summary>
/// The content-free context the client may attach to a feedback report.
/// </summary>
/// <remarks>
/// <para>
/// Every member is either a closed enum or a shape-validated version string, because all of it is
/// copied into a <em>public</em> GitHub issue. There is deliberately no free-text member: the
/// previous <c>CurrentRoute</c> string carried entity ids, query strings, and search text straight
/// into public disclosure, and no server-side scrubber can be trusted to have thought of every
/// route a future feature will add.
/// </para>
/// <para>
/// The server does not trust these values either. An out-of-range enum ordinal, or a version
/// string that does not match the accepted shape, is normalised to the unknown value before the
/// preview is signed — see <c>FeedbackClientMetadataNormalizer</c>.
/// </para>
/// </remarks>
public sealed class ClientMetadata
{
    /// <summary>
    /// The client's informational version. Shape-validated and truncated server-side; anything
    /// that is not a plain dotted version (with an optional pre-release tag) becomes unknown.
    /// </summary>
    public string? AppVersion { get; set; }

    /// <summary>Which kind of host the client runs in.</summary>
    public FeedbackPlatform Platform { get; set; } = FeedbackPlatform.Unknown;

    /// <summary>Which part of the app the learner was in. Closed set; never a route string.</summary>
    public FeedbackRouteCategory RouteCategory { get; set; } = FeedbackRouteCategory.Unknown;

    public DateTime? Timestamp { get; set; }
}
