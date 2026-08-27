using System.Text.RegularExpressions;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Feedback;

/// <summary>
/// Reduces whatever the client sent as context to values that are safe to publish.
/// </summary>
/// <remarks>
/// <para>
/// The wire contract is already closed — <see cref="FeedbackRouteCategory"/> and
/// <see cref="FeedbackPlatform"/> are enums, and there is no route string to send. This runs
/// anyway, because "the contract is an enum" is a statement about the C# type and not about the
/// bytes on the wire: <c>{"routeCategory": 4210}</c> deserialises into an
/// <see cref="FeedbackRouteCategory"/> whose value is 4210, and
/// <c>ToString()</c> on it prints <c>4210</c> straight into a public issue body. Every value that
/// reaches the markdown formatter has passed through here first, and every value that is not a
/// declared member becomes the unknown one.
/// </para>
/// <para>
/// The version is the one member that is not an enum, so it is the one that needs a shape. It is
/// matched against a deliberately narrow pattern rather than merely truncated: a client is free to
/// put anything in its informational version, and "truncated to 32 characters" is not a privacy
/// property, it is a length. Anything that is not a dotted numeric version with an optional
/// pre-release tag is replaced wholesale.
/// </para>
/// </remarks>
public static class FeedbackClientMetadataNormalizer
{
    /// <summary>What an unusable or absent version becomes.</summary>
    public const string UnknownVersion = "unknown";

    /// <summary>The longest version string that will ever be published.</summary>
    public const int MaxVersionLength = 32;

    /// <summary>
    /// Up to four dotted numeric components with an optional short pre-release tag. No build
    /// metadata: the <c>+sha</c> suffix .NET appends to an informational version is stripped
    /// before matching, because a commit hash is not needed for triage and lengthens the field
    /// without bound.
    /// </summary>
    private static readonly Regex VersionShape = new(
        @"^[0-9]{1,5}(\.[0-9]{1,5}){0,3}(-[0-9A-Za-z]{1,12}(\.[0-9A-Za-z]{1,12}){0,2})?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The normalised form of <paramref name="metadata"/>. Never null: absent metadata is the same
    /// thing as metadata that said nothing recognisable.
    /// </summary>
    public static NormalizedClientMetadata Normalize(ClientMetadata? metadata)
    {
        if (metadata is null)
        {
            return NormalizedClientMetadata.Empty;
        }

        return new NormalizedClientMetadata(
            NormalizeVersion(metadata.AppVersion),
            NormalizePlatform(metadata.Platform),
            NormalizeRoute(metadata.RouteCategory),
            NormalizeTimestamp(metadata.Timestamp));
    }

    /// <summary>An undeclared ordinal is not a category; it is unknown.</summary>
    public static FeedbackRouteCategory NormalizeRoute(FeedbackRouteCategory value) =>
        Enum.IsDefined(value) ? value : FeedbackRouteCategory.Unknown;

    /// <inheritdoc cref="NormalizeRoute" />
    public static FeedbackPlatform NormalizePlatform(FeedbackPlatform value) =>
        Enum.IsDefined(value) ? value : FeedbackPlatform.Unknown;

    /// <summary>
    /// The version if it has the accepted shape, otherwise <see cref="UnknownVersion"/>.
    /// </summary>
    public static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return UnknownVersion;
        }

        var candidate = value.Trim();

        // .NET's informational version is `1.2.3+<sha>`. The hash is noise for triage and is
        // unbounded in the contract, so it never reaches the shape check or the issue body.
        var plus = candidate.IndexOf('+');
        if (plus >= 0)
        {
            candidate = candidate[..plus];
        }

        if (candidate.Length is 0 or > MaxVersionLength)
        {
            return UnknownVersion;
        }

        try
        {
            return VersionShape.IsMatch(candidate) ? candidate : UnknownVersion;
        }
        catch (RegexMatchTimeoutException)
        {
            return UnknownVersion;
        }
    }

    /// <summary>
    /// The client's clock, clamped to something that cannot be used as a marker.
    /// </summary>
    /// <remarks>
    /// A client-supplied timestamp is not evidence of anything — it is whatever the client said —
    /// and an arbitrary-precision one published verbatim is a 100-nanosecond value unique to that
    /// submission. It is truncated to the minute, which is all a triager ever reads, and dropped
    /// entirely when it is implausible.
    /// </remarks>
    public static DateTime? NormalizeTimestamp(DateTime? value)
    {
        if (value is not { } timestamp)
        {
            return null;
        }

        var utc = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };

        if (utc.Year is < 2020 or > 2100)
        {
            return null;
        }

        return new DateTime(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
    }
}

/// <summary>
/// Client context that has already been reduced to publishable values.
/// </summary>
/// <remarks>
/// A distinct type from <see cref="ClientMetadata"/> on purpose. The formatter and the ledger
/// accept only this one, so "did anybody remember to normalise?" is answered by the compiler
/// rather than by review.
/// </remarks>
public sealed record NormalizedClientMetadata(
    string AppVersion,
    FeedbackPlatform Platform,
    FeedbackRouteCategory RouteCategory,
    DateTime? TimestampUtc)
{
    /// <summary>The value for a request that supplied no usable context.</summary>
    public static NormalizedClientMetadata Empty { get; } = new(
        FeedbackClientMetadataNormalizer.UnknownVersion,
        FeedbackPlatform.Unknown,
        FeedbackRouteCategory.Unknown,
        null);

    /// <summary>True when nothing here is worth publishing.</summary>
    public bool IsEmpty =>
        AppVersion == FeedbackClientMetadataNormalizer.UnknownVersion
        && Platform == FeedbackPlatform.Unknown
        && RouteCategory == FeedbackRouteCategory.Unknown
        && TimestampUtc is null;
}
