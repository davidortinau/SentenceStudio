using System.Security.Cryptography;
using System.Text;

namespace SentenceStudio.Api.Coach.Application.History;

/// <summary>
/// Derives the durable idempotency key and operation id a compatibility <c>/sessions</c> request
/// needs, from the only handle an old client sends: its client turn id.
/// </summary>
/// <remarks>
/// <para>
/// The durable turn path requires both a key and a caller-chosen operation id. An old client
/// knows about neither. Deriving them here keeps the old wire shape untouched while still giving
/// the request everything the durable envelope needs, and deriving them <em>deterministically</em>
/// is what makes an old client's retry a replay rather than a second charge.
/// </para>
/// <para>
/// The conversation id is mixed in so the same client turn id used against two conversations
/// produces two distinct operations. Without that, a client that numbers its turns per-session
/// from one would collide across threads on its very first message.
/// </para>
/// </remarks>
internal static class CoachCompatibilityKeys
{
    /// <summary>The prefix that marks a derived key, so a stored row's origin is legible.</summary>
    private const string Prefix = "legacy";

    /// <summary>
    /// The idempotency key for a compatibility request. Hashed before storage by the store, like
    /// any other key.
    /// </summary>
    internal static string IdempotencyKey(string conversationId, string clientTurnId) =>
        $"{Prefix}:{conversationId}:{clientTurnId}";

    /// <summary>
    /// A stable operation id for a compatibility request.
    /// </summary>
    /// <remarks>
    /// Derived by hash rather than by concatenation because the operation id is handed back to
    /// clients and stored in the clear, while the idempotency key is deliberately not: the store
    /// salts and hashes the key so it cannot be recovered. Embedding the key verbatim in a public
    /// identifier would undo that on the one path that was supposed to be a compatibility shim.
    /// </remarks>
    internal static string OperationId(string conversationId, string idempotencyKey)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Prefix}\u001f{conversationId}\u001f{idempotencyKey}"));

        // 128 bits of the digest, url-safe. Long enough that two derived ids will not collide,
        // short enough to sit inside the store's identifier budget.
        return $"{Prefix}-{Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant()}";
    }
}
