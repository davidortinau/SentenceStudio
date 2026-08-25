using System;
using System.Collections.Generic;
using System.Linq;

namespace SentenceStudio.Abstractions;

/// <summary>
/// Base type for the two ways credential persistence can fail loudly.
/// </summary>
/// <remarks>
/// <para>
/// Both carry only <b>key names</b> — never a token, never a fragment of one, never a length.
/// A length is not a redaction: it narrows a brute-force search and distinguishes an access token
/// from a refresh token in a log an operator can read.
/// </para>
/// <para>
/// These are deliberately checked-at-the-call-site exceptions rather than a bool return. A bool
/// that says "storing your credentials failed" is trivially ignored at a call site, and the whole
/// point of this type is that a caller cannot end up believing a session was persisted, or wiped,
/// when it was not.
/// </para>
/// </remarks>
public abstract class AuthTokenStorageException : Exception
{
    private protected AuthTokenStorageException(string message, Exception? inner)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Key names this app owns that are known to be, or may still be, holding data.
    /// Never contains a value.
    /// </summary>
    public IReadOnlyList<string> AffectedKeys { get; private protected init; } = Array.Empty<string>();

    private protected static string Describe(IEnumerable<string> keys)
    {
        var names = keys as IReadOnlyList<string> ?? keys.ToList();
        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }
}

/// <summary>
/// Writing the access/refresh/expiry triple failed part-way through, so no session was stored.
/// </summary>
/// <remarks>
/// <para>
/// By the time this is thrown the store has already attempted to remove <b>all three</b> owned
/// keys, so the caller is not looking at a half-written triple. What it must not do is carry on as
/// though sign-in succeeded: an in-memory access token with no persisted refresh token survives
/// exactly until the process exits and then silently signs the learner out.
/// </para>
/// <para>
/// <see cref="AffectedKeys"/> lists keys the rollback could <i>not</i> prove were cleared. An empty
/// list means the rollback verified all three are gone.
/// </para>
/// </remarks>
public sealed class AuthTokenPersistenceException : AuthTokenStorageException
{
    public AuthTokenPersistenceException(
        string failedKey,
        IReadOnlyList<string> unclearedKeys,
        Exception? inner)
        : base(
            $"Storing the credential triple failed while writing '{failedKey}'. " +
            $"All owned keys were rolled back; keys that could not be confirmed clear: " +
            $"{Describe(unclearedKeys)}.",
            inner)
    {
        FailedKey = failedKey;
        AffectedKeys = unclearedKeys;
    }

    /// <summary>Name of the key whose write failed. Never a value.</summary>
    public string FailedKey { get; }

    /// <summary>True when rollback confirmed every owned key is gone.</summary>
    public bool RollbackVerified => AffectedKeys.Count == 0;
}

/// <summary>
/// Sign-out could not prove the stored credentials are gone.
/// </summary>
/// <remarks>
/// <para>
/// Thrown only after the in-memory session has already been dropped and every bounded removal
/// attempt has been made. It exists so that "signed out" is never reported for a device that may
/// still be holding a usable refresh token — the failure mode that lets a shared or handed-on
/// machine restore someone else's session on the next cold start.
/// </para>
/// <para>
/// The store latches <see cref="AuthTokenStore.CleanupPendingPreferenceKey"/> before it starts
/// removing, so residue left behind by this failure (or by a crash mid-removal) blocks every
/// silent-restore path until a fresh sign-in rewrites the triple.
/// </para>
/// </remarks>
public sealed class AuthTokenCleanupException : AuthTokenStorageException
{
    public AuthTokenCleanupException(IReadOnlyList<string> unclearedKeys, Exception? inner = null)
        : base(
            "Sign-out could not confirm that stored credentials were removed. " +
            $"Keys still possibly holding data: {Describe(unclearedKeys)}. " +
            "In-memory authentication was cleared and silent restore is now blocked.",
            inner)
    {
        AffectedKeys = unclearedKeys;
    }
}

/// <summary>
/// A secure-storage write was refused by the platform keystore.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="AuthTokenPersistenceException"/>: this is one key failing at the
/// storage layer, that one is the credential triple failing as a unit. Carries the key name only.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so that every
/// <see cref="ISecureStorageService"/> implementation reports a refused write as the same shape
/// callers already handle, while still letting a caller that cares match the specific type and
/// read <see cref="Key"/>.
/// </para>
/// </remarks>
public sealed class SecureStorageWriteException : InvalidOperationException
{
    public SecureStorageWriteException(string key, Exception? inner = null)
        : base($"Failed to write secure storage item '{key}'.", inner)
    {
        Key = key;
    }

    public SecureStorageWriteException(string key, string message)
        : base(message)
    {
        Key = key;
    }

    /// <summary>Name of the key whose write failed. Never a value.</summary>
    public string Key { get; }
}

/// <summary>
/// What a sign-out actually achieved.
/// </summary>
/// <remarks>
/// Returned rather than swallowed so a UI layer that must keep navigating (it has already published
/// an anonymous principal) still cannot report unqualified success. <see cref="UnclearedKeys"/>
/// holds key names only.
/// </remarks>
public readonly struct SignOutOutcome
{
    public SignOutOutcome(bool credentialsCleared, IReadOnlyList<string> unclearedKeys)
    {
        CredentialsCleared = credentialsCleared;
        UnclearedKeys = unclearedKeys;
    }

    /// <summary>True only when every owned key was verified absent after removal.</summary>
    public bool CredentialsCleared { get; }

    /// <summary>Names of keys that could not be confirmed clear. Never contains a value.</summary>
    public IReadOnlyList<string> UnclearedKeys { get; }

    public static SignOutOutcome Clean { get; } = new(true, Array.Empty<string>());

    public static SignOutOutcome Failed(IReadOnlyList<string> unclearedKeys) =>
        new(false, unclearedKeys);
}
