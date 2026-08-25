using System;
using System.Threading;
using System.Threading.Tasks;

namespace SentenceStudio.Abstractions;

/// <summary>
/// Why a secure-storage read produced (or failed to produce) a value.
/// </summary>
/// <remarks>
/// Introduced for the macOS AppKit head, where the platform keystore can demand
/// interactive authorisation. See <see cref="SecureStorageAccess"/>.
/// </remarks>
public enum SecureStorageReadStatus
{
    /// <summary>A value was found and decoded.</summary>
    Found = 0,

    /// <summary>No item is stored under the key. This is a normal, expected outcome.</summary>
    NotFound = 1,

    /// <summary>
    /// The item exists but the platform keystore would have to prompt the user to release it
    /// (macOS SecurityAgent keychain ACL dialog, biometric/passphrase gate, locked keychain).
    /// The item was <b>not</b> read, modified, or removed.
    /// </summary>
    InteractionRequired = 2,

    /// <summary>
    /// An item was returned but its payload could not be decoded into a usable string.
    /// The item is left in place — callers must not delete it on the app's behalf.
    /// </summary>
    Malformed = 3,

    /// <summary>The read was cancelled before the platform call completed.</summary>
    Cancelled = 4,

    /// <summary>The platform keystore reported an error that is none of the above.</summary>
    Failed = 5,
}

/// <summary>
/// How much a secure-storage read is allowed to inconvenience the user.
/// </summary>
public enum SecureStorageAccess
{
    /// <summary>
    /// Automatic/background read (app start, silent token restore, background refresh).
    /// The implementation MUST fail fast rather than block waiting on a UI prompt.
    /// </summary>
    NoInteraction = 0,

    /// <summary>
    /// The read is a direct consequence of something the user just did, so a platform
    /// authorisation prompt is acceptable.
    /// </summary>
    AllowInteraction = 1,
}

/// <summary>
/// Outcome of <see cref="ISecureStorageService.TryGetAsync"/>.
/// </summary>
public readonly struct SecureStorageReadResult : IEquatable<SecureStorageReadResult>
{
    public SecureStorageReadResult(SecureStorageReadStatus status, string? value)
    {
        Status = status;
        Value = value;
    }

    public SecureStorageReadStatus Status { get; }

    /// <summary>
    /// The stored value. Only ever non-null when <see cref="Status"/> is
    /// <see cref="SecureStorageReadStatus.Found"/>.
    /// </summary>
    public string? Value { get; }

    public bool IsFound => Status == SecureStorageReadStatus.Found;

    /// <summary>
    /// True when the platform refused without user authorisation. Callers should treat this as
    /// "no usable session right now" and must NOT clear or overwrite any stored data.
    /// </summary>
    public bool RequiresInteraction => Status == SecureStorageReadStatus.InteractionRequired;

    public static SecureStorageReadResult FromValue(string value) =>
        new(SecureStorageReadStatus.Found, value);

    public static SecureStorageReadResult Missing { get; } =
        new(SecureStorageReadStatus.NotFound, null);

    public static SecureStorageReadResult NeedsInteraction { get; } =
        new(SecureStorageReadStatus.InteractionRequired, null);

    public static SecureStorageReadResult MalformedValue { get; } =
        new(SecureStorageReadStatus.Malformed, null);

    public static SecureStorageReadResult Cancelled { get; } =
        new(SecureStorageReadStatus.Cancelled, null);

    public static SecureStorageReadResult Failed { get; } =
        new(SecureStorageReadStatus.Failed, null);

    public bool Equals(SecureStorageReadResult other) =>
        Status == other.Status && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SecureStorageReadResult o && Equals(o);

    public override int GetHashCode() => HashCode.Combine((int)Status, Value);

    public static bool operator ==(SecureStorageReadResult a, SecureStorageReadResult b) => a.Equals(b);

    public static bool operator !=(SecureStorageReadResult a, SecureStorageReadResult b) => !a.Equals(b);

    /// <summary>Status only — never includes <see cref="Value"/>, so this is safe to log.</summary>
    public override string ToString() => Status.ToString();
}

public interface ISecureStorageService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    bool Remove(string key);
    void RemoveAll();

    /// <summary>
    /// Reads <paramref name="key"/> and reports <i>why</i> the read succeeded or failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default implementation delegates to <see cref="GetAsync"/>, so every existing
    /// implementation keeps its current behaviour with no source changes. Platforms whose
    /// keystore can block on a user prompt (currently the macOS AppKit head) override this so
    /// that <see cref="SecureStorageAccess.NoInteraction"/> reads fail fast with
    /// <see cref="SecureStorageReadStatus.InteractionRequired"/> instead of deadlocking startup.
    /// </para>
    /// <para>Implementations must never log the returned value or any part of it.</para>
    /// </remarks>
    Task<SecureStorageReadResult> TryGetAsync(
        string key,
        SecureStorageAccess access,
        CancellationToken cancellationToken = default)
        => DelegateToGetAsync(this, key, cancellationToken);

    private static async Task<SecureStorageReadResult> DelegateToGetAsync(
        ISecureStorageService store,
        string key,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return SecureStorageReadResult.Cancelled;

        var value = await store.GetAsync(key).ConfigureAwait(false);
        return string.IsNullOrEmpty(value)
            ? SecureStorageReadResult.Missing
            : SecureStorageReadResult.FromValue(value!);
    }
}
