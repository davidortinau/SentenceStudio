using Microsoft.AspNetCore.DataProtection;
using SentenceStudio.Api.Coach.Telemetry;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// What a protected agent session is bound to.
/// </summary>
/// <remarks>
/// <para>
/// Both fields become part of the protection purpose chain, which is the
/// authenticated-additional-data equivalent for ASP.NET Data Protection: ciphertext produced
/// under one context cannot be read under another. Binding the owner is what stops a
/// <c>ProtectedAgentSession</c> column copied from one learner's row into another's from
/// decrypting; binding the session id stops the same learner's expired session being replayed
/// into a live one.
/// </para>
/// <para>
/// This mirrors <c>CoachProtectionContext</c> in the history lane, deliberately: two protectors
/// with different binding rules is how one of them ends up being the weak one.
/// </para>
/// </remarks>
/// <param name="UserProfileId">The trusted owner of the session row.</param>
/// <param name="SessionId">The identity of the session row the payload belongs to.</param>
public readonly record struct CoachAgentSessionContext(string UserProfileId, string SessionId);

/// <summary>
/// Protects the serialized agent session before it reaches the database and unprotects
/// it on read. The stored column is ciphertext: a plaintext scan of the row never
/// reveals the learner's conversation.
/// </summary>
public interface ICoachAgentSessionProtector
{
    /// <summary>
    /// Encrypts serialized agent-session JSON for exactly one owner and session.
    /// Returns null when there is nothing to store.
    /// </summary>
    string? Protect(CoachAgentSessionContext context, string? agentSessionJson);

    /// <summary>
    /// Decrypts a stored payload. Returns false when the payload is missing, was written for a
    /// different owner or session, or can no longer be read (for example after key rotation),
    /// so the caller can reject the session instead of resuming with a half-readable state.
    /// </summary>
    bool TryUnprotect(CoachAgentSessionContext context, string? protectedPayload, out string? agentSessionJson);
}

/// <summary>
/// <see cref="IDataProtectionProvider"/>-backed implementation. The purpose chain is
/// <c>root → version → owner → record</c> and is versioned, so a future format change cannot
/// silently decrypt older payloads.
/// </summary>
/// <remarks>
/// <para>
/// v1 bound nothing but a single static purpose string. Every learner's agent session was
/// therefore encrypted under the same key and purpose, so ciphertext moved between
/// <c>CoachSessions</c> rows decrypted cleanly — the agent session carries the running
/// conversation, so that is a cross-learner content read for anyone who can write the table.
/// v2 binds owner and session id.
/// </para>
/// <para>
/// <b>Reads fall back to v1 exactly once, and writes never do.</b> Sessions written before this
/// change are still readable, so no learner loses a conversation in progress, and the fallback
/// ages out on its own: a session is re-protected under v2 the next time it is saved, and the
/// sliding expiry (<c>CoachPersistenceOptions.SessionLifetime</c>, one day by default) retires
/// anything that is not. The fallback is bounded to the legacy purpose only — it never tries a
/// different owner's v2 chain, so it cannot be used to read across learners.
/// </para>
/// </remarks>
public sealed class DataProtectionCoachAgentSessionProtector : ICoachAgentSessionProtector
{
    /// <summary>The root purpose. Changing it invalidates every stored payload.</summary>
    public const string RootPurpose = "SentenceStudio.Coach.AgentSession";

    /// <summary>
    /// The v1 data-protection purpose: a single static string, bound to nothing.
    /// Read-only — retained solely so sessions written before the owner/record chain existed
    /// remain readable until they expire.
    /// </summary>
    public const string LegacyPurpose = "SentenceStudio.Coach.AgentSession.v1";

    /// <summary>The envelope version new writes are stamped with.</summary>
    public const int Version2 = 2;

    private readonly IDataProtectionProvider _provider;
    private readonly IDataProtector _legacyProtector;
    private readonly ILogger<DataProtectionCoachAgentSessionProtector> _logger;

    // Protectors are not cached. The record segment makes one protector per session row, so a
    // cache would grow with the session table. Building one is purpose derivation only.

    public DataProtectionCoachAgentSessionProtector(
        IDataProtectionProvider provider,
        ILogger<DataProtectionCoachAgentSessionProtector> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _legacyProtector = provider.CreateProtector(LegacyPurpose);
        _logger = logger;
    }

    public string? Protect(CoachAgentSessionContext context, string? agentSessionJson)
    {
        if (string.IsNullOrEmpty(agentSessionJson))
        {
            return null;
        }

        return CreateProtector(context).Protect(agentSessionJson);
    }

    public bool TryUnprotect(CoachAgentSessionContext context, string? protectedPayload, out string? agentSessionJson)
    {
        agentSessionJson = null;
        if (string.IsNullOrEmpty(protectedPayload))
        {
            return false;
        }

        try
        {
            agentSessionJson = CreateProtector(context).Unprotect(protectedPayload);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Key rotation, tampering, a payload written for a different owner or session, or a
            // payload written before the owner/record chain existed. Try the legacy purpose once
            // before giving up, then fail closed: the caller starts a fresh session rather than
            // resuming an unreadable one.
            if (TryUnprotectLegacy(protectedPayload, out agentSessionJson))
            {
                return true;
            }

            // The exception object is not logged. Data Protection names the missing or rejected
            // key in its message ("The key {guid} was not found in the key ring"), and a key
            // identifier is not something this application writes to logs. Category only.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogWarning(
                "[Coach] Stored agent session could not be unprotected; treating it as unreadable. " +
                "Category={FailureCategory} InnerDepth={InnerDepth}",
                facts.Category,
                facts.InnerDepth);

            return false;
        }
        catch (ArgumentException)
        {
            // Not a Data Protection payload at all (truncated column, hand-edited row). The
            // legacy protector would reject it for the same reason, so there is nothing to retry.
            _logger.LogWarning(
                "[Coach] Stored agent session is not a readable payload; treating it as unreadable.");
            return false;
        }
    }

    /// <summary>
    /// The one bounded fallback: the v1 static purpose, tried only after the v2 chain failed.
    /// </summary>
    /// <remarks>
    /// It reads no context, so it cannot be steered at another owner's chain; it is simply the
    /// old, unbound purpose. A success here is a pre-upgrade session, and the caller re-saves it
    /// under v2 on the next write.
    /// </remarks>
    private bool TryUnprotectLegacy(string protectedPayload, out string? agentSessionJson)
    {
        agentSessionJson = null;

        try
        {
            agentSessionJson = _legacyProtector.Unprotect(protectedPayload);
            _logger.LogInformation(
                "[Coach] Stored agent session was read under the legacy (v1) protection purpose. " +
                "It is re-protected with the owner-bound purpose on the next save.");
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private IDataProtector CreateProtector(CoachAgentSessionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.UserProfileId))
        {
            throw new ArgumentException("A protected agent session requires a trusted owner.", nameof(context));
        }

        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            throw new ArgumentException("A protected agent session requires a session identifier.", nameof(context));
        }

        // Each segment is prefixed so a value can never impersonate a segment boundary, and the
        // version segment comes first so a future envelope change cannot silently read v2.
        return _provider.CreateProtector(
            RootPurpose,
            $"v{Version2}",
            $"user:{context.UserProfileId.Trim()}",
            $"record:{context.SessionId.Trim()}");
    }
}
