using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Feedback;

/// <summary>
/// What a signed preview promises: this exact issue, for this owner, once, before this time.
/// </summary>
/// <remarks>
/// <para>
/// Every field is covered by the signature, including <see cref="Jti"/>. That is the whole point
/// of adding it. Without a nonce the token <em>is</em> its content: two previews of the same
/// description by the same owner in the same second produce byte-identical tokens, so the ledger
/// has nothing to key on and "exactly once" degenerates into "at most one issue per distinct
/// description, forever" — which is both wrong (a learner legitimately re-reporting a recurring
/// bug is silently refused) and insufficient (nothing stops the same token being redeemed twice
/// from two processes). With a nonce, each preview is a distinct redeemable object and the ledger
/// can be keyed on it.
/// </para>
/// <para>
/// The nonce is 128 bits from <see cref="RandomNumberGenerator"/>, not a GUID and not a hash of
/// the content. It is the ledger's primary key, so a predictable value would let one account
/// pre-empt another's submission by claiming its identifier first.
/// </para>
/// </remarks>
/// <param name="Jti">Cryptographically random, unique per preview, covered by the signature.</param>
/// <param name="Title">The issue title, exactly as previewed and exactly as it will be posted.</param>
/// <param name="Body">The issue body, exactly as previewed and exactly as it will be posted.</param>
/// <param name="Labels">The closed label set, exactly as previewed.</param>
/// <param name="FeedbackType">The closed feedback type.</param>
/// <param name="OwnerProfileId">The learner the preview was issued to.</param>
/// <param name="RouteCategory">Normalised route category, carried so the ledger need not re-derive it.</param>
/// <param name="Platform">Normalised platform.</param>
/// <param name="AppVersion">Normalised app version.</param>
/// <param name="Iat">Issued-at, seconds since the epoch.</param>
/// <param name="Exp">Expiry, seconds since the epoch.</param>
public sealed record FeedbackPreviewPayload(
    string Jti,
    string Title,
    string Body,
    string[] Labels,
    string FeedbackType,
    string OwnerProfileId,
    FeedbackRouteCategory RouteCategory,
    FeedbackPlatform Platform,
    string AppVersion,
    long Iat,
    long Exp);

/// <summary>Why a presented token was not accepted. Never carries the token or its content.</summary>
public enum FeedbackTokenRejection
{
    /// <summary>Accepted.</summary>
    None = 0,

    /// <summary>Absent, wrong shape, or the signature did not verify.</summary>
    Invalid = 1,

    /// <summary>Verified, but past its expiry.</summary>
    Expired = 2,

    /// <summary>Verified and live, but its payload carries something the server will not post.</summary>
    PayloadRejected = 3
}

/// <summary>
/// Mints and verifies the HMAC-SHA256 preview token.
/// </summary>
public static class FeedbackPreviewToken
{
    /// <summary>How many random bytes back <see cref="FeedbackPreviewPayload.Jti"/>.</summary>
    public const int JtiByteLength = 16;

    /// <summary>The longest jti the verifier will accept, so a forged-length value cannot bloat a key column.</summary>
    public const int MaxJtiLength = 64;

    /// <summary>The longest title that will ever be posted.</summary>
    public const int MaxTitleLength = 80;

    /// <summary>The longest body that will ever be posted.</summary>
    public const int MaxBodyLength = 60_000;

    internal static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Numeric enums, pinned. A string converter here would make the signed bytes depend on
        // member *names*, so renaming a category would invalidate every live token and — worse —
        // a rename plus a re-add could make two different categories sign identically.
        Converters = { },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>A fresh, unpredictable token identifier.</summary>
    public static string NewJti() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(JtiByteLength));

    /// <summary>
    /// Serialises <paramref name="payload"/> and appends its HMAC, using
    /// <paramref name="key"/>.
    /// </summary>
    public static string Create(FeedbackPreviewPayload payload, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson);
        Span<byte> signature = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, payloadBytes, signature);

        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    /// <summary>
    /// Verifies <paramref name="token"/> and returns its payload, or explains the refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters. The signature is checked before anything is parsed as a domain value, so no
    /// unsigned bytes ever reach a code path that could act on them — and the comparison is
    /// fixed-time, so a caller cannot search for a valid signature one byte at a time.
    /// </para>
    /// <para>
    /// The payload checks after that are not tamper checks; the signature already settled that.
    /// They are the second half of the closed-set rule: a value that this server signed but should
    /// never have signed — an over-long title from a future formatter change, a label the model
    /// talked its way past a filter — must not be posted publicly just because it carries our
    /// signature.
    /// </para>
    /// </remarks>
    public static FeedbackTokenRejection TryValidate(
        string? token,
        ReadOnlySpan<byte> key,
        DateTimeOffset now,
        out FeedbackPreviewPayload? payload)
    {
        payload = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return FeedbackTokenRejection.Invalid;
        }

        var separator = token.IndexOf('.');
        if (separator <= 0 || separator == token.Length - 1)
        {
            return FeedbackTokenRejection.Invalid;
        }

        // A second separator means a shape this verifier does not define. Refusing rather than
        // ignoring the tail keeps the signed bytes and the presented bytes the same object.
        if (token.IndexOf('.', separator + 1) >= 0)
        {
            return FeedbackTokenRejection.Invalid;
        }

        byte[] payloadBytes;
        byte[] providedSignature;
        try
        {
            payloadBytes = Base64UrlDecode(token.AsSpan(0, separator));
            providedSignature = Base64UrlDecode(token.AsSpan(separator + 1));
        }
        catch (FormatException)
        {
            return FeedbackTokenRejection.Invalid;
        }

        if (providedSignature.Length != HMACSHA256.HashSizeInBytes)
        {
            return FeedbackTokenRejection.Invalid;
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, payloadBytes, expected);

        if (!CryptographicOperations.FixedTimeEquals(expected, providedSignature))
        {
            return FeedbackTokenRejection.Invalid;
        }

        FeedbackPreviewPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FeedbackPreviewPayload>(payloadBytes, PayloadJson);
        }
        catch (JsonException)
        {
            return FeedbackTokenRejection.Invalid;
        }

        if (parsed is null)
        {
            return FeedbackTokenRejection.Invalid;
        }

        if (now.ToUnixTimeSeconds() > parsed.Exp)
        {
            return FeedbackTokenRejection.Expired;
        }

        if (!IsPostable(parsed))
        {
            return FeedbackTokenRejection.PayloadRejected;
        }

        payload = parsed;
        return FeedbackTokenRejection.None;
    }

    /// <summary>
    /// A digest over exactly the bytes that will be sent to GitHub.
    /// </summary>
    /// <remarks>
    /// Recorded on the ledger row so the binding between what the learner saw and what was posted
    /// is checkable after the fact, without keeping a copy of the issue text in our database. Two
    /// submissions that produced different bodies cannot share a digest, and a submission whose
    /// body changed between preview and post cannot match the one recorded at claim time.
    /// </remarks>
    public static string ContentDigest(string title, string body, string[] labels, string feedbackType)
    {
        // Length-prefixed, so no combination of field contents can be re-partitioned into a
        // different tuple with the same bytes. Concatenating with a separator would let a title
        // containing that separator impersonate a title/body split.
        var buffer = new StringBuilder();
        Append(buffer, title);
        Append(buffer, body);
        Append(buffer, feedbackType);
        Append(buffer, labels.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var label in labels)
        {
            Append(buffer, label);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(buffer.ToString()));
        return Convert.ToHexStringLower(hash);

        static void Append(StringBuilder target, string value)
        {
            target.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            target.Append(':');
            target.Append(value);
            target.Append('|');
        }
    }

    private static bool IsPostable(FeedbackPreviewPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Jti) || payload.Jti.Length > MaxJtiLength)
        {
            return false;
        }

        if (string.IsNullOrEmpty(payload.OwnerProfileId) || payload.OwnerProfileId.Length > 64)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.Title) || payload.Title.Length > MaxTitleLength)
        {
            return false;
        }

        if (string.IsNullOrEmpty(payload.Body) || payload.Body.Length > MaxBodyLength)
        {
            return false;
        }

        if (payload.FeedbackType is not (FeedbackLabels.Bug or FeedbackLabels.Enhancement))
        {
            return false;
        }

        if (payload.Labels is null or { Length: 0 } || payload.Labels.Length > FeedbackLabels.Allowed.Count)
        {
            return false;
        }

        foreach (var label in payload.Labels)
        {
            if (!FeedbackLabels.IsAllowed(label))
            {
                return false;
            }
        }

        if (!Enum.IsDefined(payload.RouteCategory) || !Enum.IsDefined(payload.Platform))
        {
            return false;
        }

        // Null, not merely long. The record declares AppVersion non-nullable, but that is a
        // compile-time claim about C# and this value arrives from JSON: a payload that simply omits
        // "appVersion" deserialises with it null, and `.Length` on it throws a
        // NullReferenceException out of TryValidate — which catches only JsonException, so the
        // endpoint answers 500 instead of refusing the token. Every other string here is already
        // guarded by an IsNullOrEmpty or a pattern match; this was the one gap.
        if (payload.AppVersion is null
            || payload.AppVersion.Length > FeedbackClientMetadataNormalizer.MaxVersionLength)
        {
            return false;
        }

        return payload.Exp > payload.Iat;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(ReadOnlySpan<char> base64Url)
    {
        var s = new string(base64Url).Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            0 => Convert.FromBase64String(s),
            2 => Convert.FromBase64String(s + "=="),
            3 => Convert.FromBase64String(s + "="),
            _ => throw new FormatException("Invalid base64url length.")
        };
    }
}
