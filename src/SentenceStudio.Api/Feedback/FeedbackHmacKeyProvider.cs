using System.Security.Cryptography;
using System.Text;

namespace SentenceStudio.Api.Feedback;

/// <summary>
/// Supplies the key that signs feedback preview tokens, and refuses to let a deployment start
/// without one.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no fallback to <c>Jwt:SigningKey</c>.</b> The old code used it when
/// <c>Feedback:HmacKey</c> was absent, with a warning, and that arrangement is worse than it
/// reads. It puts the key that authenticates every session into a second signing context whose
/// message format is attacker-influenced: the preview payload contains a title and a body derived
/// from text the caller supplied. Two HMAC constructions over one key are only safe while their
/// message encodings can never collide, and nothing in the codebase was enforcing that — it was
/// true by coincidence of the JSON shapes. A confusion between them is a forged authentication
/// token, not a forged feedback preview, so the blast radius of the cheaper surface is the
/// expensive one.
/// </para>
/// <para>
/// It also made rotation impossible in the direction that matters. Rotating the JWT key to respond
/// to a feedback-side leak signs every learner out; not rotating it leaves the leaked key
/// authenticating sessions. A deployment should never have to choose between those.
/// </para>
/// <para>
/// Outside Development and Testing the key is mandatory, must be long enough to be a key rather
/// than a passphrase, and must not equal <c>Jwt:SigningKey</c> — configuring them identically
/// re-creates by hand exactly the coupling this removed. In Development and Testing a random
/// per-process key is generated so a developer can run the feature without provisioning a secret;
/// tokens then do not survive a restart, which is honest and is bounded by the ten-minute
/// lifetime anyway.
/// </para>
/// <para>
/// Neither key is ever logged, in any form — not truncated, not hashed, not "first four
/// characters for correlation". The only thing this type will say about a key is whether one is
/// configured.
/// </para>
/// </remarks>
public interface IFeedbackHmacKeyProvider
{
    /// <summary>The signing key bytes. Never logged, never returned as text.</summary>
    ReadOnlySpan<byte> Key { get; }
}

/// <inheritdoc />
public sealed class FeedbackHmacKeyProvider : IFeedbackHmacKeyProvider
{
    /// <summary>The configuration key holding the dedicated feedback signing key.</summary>
    public const string ConfigurationKey = "Feedback:HmacKey";

    /// <summary>The configuration key this must never be shared with.</summary>
    public const string JwtSigningKeyConfigurationKey = "Jwt:SigningKey";

    /// <summary>The shortest key a non-development deployment may configure.</summary>
    public const int MinimumKeyLength = 32;

    private readonly byte[] _key;

    private FeedbackHmacKeyProvider(byte[] key) => _key = key;

    /// <inheritdoc />
    public ReadOnlySpan<byte> Key => _key;

    /// <summary>
    /// Builds the provider for <paramref name="configuration"/>, or throws with an actionable,
    /// secret-free message.
    /// </summary>
    /// <param name="allowGeneratedKey">
    /// True only for Development and Testing. A generated key is a convenience, and a convenience
    /// that silently applied in Production would be a deployment whose feedback tokens are
    /// forgeable across a restart and unverifiable across replicas.
    /// </param>
    public static FeedbackHmacKeyProvider Create(IConfiguration configuration, bool allowGeneratedKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!allowGeneratedKey)
            {
                throw new InvalidOperationException(
                    $"{ConfigurationKey} must be configured outside Development and Testing. "
                    + "Feedback preview tokens are signed with a key dedicated to this feature so "
                    + "it can be rotated without invalidating every session; there is deliberately "
                    + $"no fallback to {JwtSigningKeyConfigurationKey}.");
            }

            return new FeedbackHmacKeyProvider(RandomNumberGenerator.GetBytes(64));
        }

        if (!allowGeneratedKey)
        {
            if (configured.Length < MinimumKeyLength)
            {
                throw new InvalidOperationException(
                    $"{ConfigurationKey} must be at least {MinimumKeyLength} characters outside "
                    + "Development and Testing.");
            }

            var jwtKey = configuration[JwtSigningKeyConfigurationKey];
            if (!string.IsNullOrWhiteSpace(jwtKey)
                && string.Equals(jwtKey, configured, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{ConfigurationKey} must not be the same value as "
                    + $"{JwtSigningKeyConfigurationKey}. Sharing one key across the session and "
                    + "feedback signing contexts means a feedback-side compromise is an "
                    + "authentication compromise, and that rotating either one signs every learner "
                    + "out.");
            }
        }

        return new FeedbackHmacKeyProvider(Encoding.UTF8.GetBytes(configured));
    }
}
