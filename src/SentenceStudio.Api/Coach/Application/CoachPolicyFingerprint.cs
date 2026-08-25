using System.Security.Cryptography;
using System.Text;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Stable fingerprints of the prompt text and the tool allow-list, used as the prompt and tool
/// policy versions on a checkpoint's coverage.
/// </summary>
/// <remarks>
/// <para>
/// A checkpoint stores a live agent session that was built under a particular prompt and a
/// particular set of callable tools. When either changes, the stored session is answering under
/// rules that no longer exist. Comparing a hand-maintained version string would only work if
/// somebody remembered to bump it in the same commit that edited the prompt; deriving the version
/// from the prompt itself removes that dependency on memory. Editing the instructions or adding a
/// tool changes the fingerprint, which makes the next turn rebuild from the ledger.
/// </para>
/// <para>
/// The digest is truncated for storage; it is a change detector, not a security boundary. Two
/// different prompts colliding would mean a stale checkpoint survives one deploy, which is the
/// same outcome as not having the field at all, so the truncation costs nothing that was
/// otherwise guaranteed.
/// </para>
/// </remarks>
internal static class CoachPolicyFingerprint
{
    /// <summary>A fingerprint of the instruction text the agent is built with.</summary>
    internal static string Prompt { get; } = Digest(CoachInstructions.Instructions);

    /// <summary>
    /// A fingerprint of the tool allow-list. Order is part of the identity because the allow-list
    /// is declared in registration order and the model sees it that way.
    /// </summary>
    internal static string ToolPolicy { get; } = Digest(string.Join('\n', CoachToolNames.All));

    private static string Digest(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 8));
    }
}
