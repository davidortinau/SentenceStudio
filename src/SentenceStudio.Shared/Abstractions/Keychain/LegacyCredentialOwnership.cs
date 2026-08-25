using System;
using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SentenceStudio.Abstractions.Keychain;

/// <summary>Why a legacy credential triple was, or was not, adopted.</summary>
public enum LegacyOwnershipVerdict
{
    /// <summary>Corroborated as this install's own credentials.</summary>
    Owned = 0,

    /// <summary>Nothing is stored under the bare accounts.</summary>
    Absent = 1,

    /// <summary>Present but not readable without asking the user, so nothing can be corroborated.</summary>
    Unreadable = 2,

    /// <summary>Readable but not a complete, self-consistent credential triple.</summary>
    Incoherent = 3,

    /// <summary>
    /// A complete triple, but its identity does not match this install's active profile. Somebody
    /// else's credentials, sitting in the shared keychain service under the same account names.
    /// </summary>
    ForeignIdentity = 4,

    /// <summary>This install has no active profile to compare against, so ownership is unprovable.</summary>
    NoLocalIdentity = 5,

    /// <summary>
    /// This install already reached a decision on a previous launch (or earlier in this process).
    /// The bare accounts were not probed again.
    /// </summary>
    AlreadyDecided = 6,
}

/// <summary>The three values that make up a stored session, read from the bare accounts.</summary>
public readonly record struct LegacyCredentialTriple(string AccessToken, string RefreshToken, string Expires);

/// <summary>
/// Decides whether a credential triple found under the shared, un-namespaced keychain accounts
/// belongs to <em>this</em> install.
/// </summary>
/// <remarks>
/// <para>
/// The keychain service name MAUI uses on macOS (<c>maui_secure_storage</c>) is machine-global and
/// carries no owner, and the account names are the bare key names — <c>auth_jwt</c>,
/// <c>auth_refresh</c>, <c>auth_expires</c>. Any MAUI application on the machine can write those
/// exact names. "There is something at <c>auth_refresh</c>" therefore says nothing whatsoever about
/// whose refresh token it is.
/// </para>
/// <para>
/// Adopting on name alone would copy another product's refresh token into this app's namespace and
/// then present it to the SentenceStudio API. That is a credential-confusion bug with a real blast
/// radius, so ownership is corroborated from the payload instead: the triple must be complete and
/// self-consistent, and the access token's SentenceStudio identity claim must equal the profile id
/// this install already has on its own side.
/// </para>
/// <para>
/// Everything here is a pure function of its inputs. No keychain, no I/O, no clock — the caller
/// supplies "now" — so every branch is directly testable.
/// </para>
/// </remarks>
public static class LegacyCredentialOwnership
{
    /// <summary>Claim names that carry the SentenceStudio profile id, in order of preference.</summary>
    private static readonly string[] ProfileClaimNames =
    {
        "user_profile_id",
        "userProfileId",
        "profile_id",
    };

    /// <summary>
    /// Corroborates ownership of <paramref name="triple"/> against this install's own state.
    /// </summary>
    /// <param name="triple">Values read from the bare accounts, or <c>null</c> when absent.</param>
    /// <param name="localActiveProfileId">
    /// The app's stored <c>active_profile_id</c>. This is app-scoped state another application
    /// cannot write, which is what makes it usable as evidence.
    /// </param>
    /// <param name="expectedIssuer">Optional. When set, the token's <c>iss</c> must match.</param>
    /// <param name="expectedAudience">Optional. When set, the token's <c>aud</c> must match.</param>
    public static LegacyOwnershipVerdict Corroborate(
        LegacyCredentialTriple? triple,
        string? localActiveProfileId,
        string? expectedIssuer = null,
        string? expectedAudience = null)
    {
        if (triple is not { } t)
            return LegacyOwnershipVerdict.Absent;

        if (string.IsNullOrWhiteSpace(t.AccessToken)
            || string.IsNullOrWhiteSpace(t.RefreshToken)
            || string.IsNullOrWhiteSpace(t.Expires))
        {
            return LegacyOwnershipVerdict.Incoherent;
        }

        // A triple whose expiry is not a timestamp was not written by this app's token store.
        if (!DateTimeOffset.TryParse(
                t.Expires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            return LegacyOwnershipVerdict.Incoherent;
        }

        var claims = TryReadJwtPayload(t.AccessToken);
        if (claims is null)
            return LegacyOwnershipVerdict.Incoherent;

        // No local identity means nothing to compare against. Refusing here is the whole point:
        // a fresh install with no profile must never adopt whatever it finds lying around.
        if (string.IsNullOrWhiteSpace(localActiveProfileId))
            return LegacyOwnershipVerdict.NoLocalIdentity;

        var tokenProfileId = FirstClaim(claims.Value, ProfileClaimNames);
        if (string.IsNullOrWhiteSpace(tokenProfileId))
            return LegacyOwnershipVerdict.Incoherent;

        if (!string.Equals(tokenProfileId, localActiveProfileId, StringComparison.Ordinal))
            return LegacyOwnershipVerdict.ForeignIdentity;

        if (!string.IsNullOrWhiteSpace(expectedIssuer)
            && !string.Equals(FirstClaim(claims.Value, "iss"), expectedIssuer, StringComparison.Ordinal))
        {
            return LegacyOwnershipVerdict.ForeignIdentity;
        }

        if (!string.IsNullOrWhiteSpace(expectedAudience)
            && !string.Equals(FirstClaim(claims.Value, "aud"), expectedAudience, StringComparison.Ordinal))
        {
            return LegacyOwnershipVerdict.ForeignIdentity;
        }

        return LegacyOwnershipVerdict.Owned;
    }

    private static string? FirstClaim(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var value))
                continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Array when value.GetArrayLength() > 0:
                    // "aud" is allowed to be an array; take the first entry.
                    var first = value[0];
                    if (first.ValueKind == JsonValueKind.String)
                        return first.GetString();
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes a JWT payload without validating the signature.
    /// </summary>
    /// <remarks>
    /// Signature validation is deliberately out of scope and would not help: this app does not hold
    /// the signing key, and a forged token still would not match the locally stored profile id. The
    /// parse exists to read a claim, not to establish trust — the trust decision is the profile-id
    /// comparison in <see cref="Corroborate"/>.
    /// </remarks>
    private static JsonElement? TryReadJwtPayload(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
                return null;

            var payload = Base64UrlDecode(parts[1]);
            if (payload is null)
                return null;

            using var document = JsonDocument.Parse(payload);
            // Clone so the element outlives the document.
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? Base64UrlDecode(string value)
    {
        var builder = new StringBuilder(value.Length + 3);
        builder.Append(value.Replace('-', '+').Replace('_', '/'));
        builder.Append('=', (4 - (value.Length % 4)) % 4);

        return Convert.TryFromBase64String(builder.ToString(), new byte[builder.Length], out var written)
            ? Convert.FromBase64String(builder.ToString())[..written]
            : null;
    }
}
