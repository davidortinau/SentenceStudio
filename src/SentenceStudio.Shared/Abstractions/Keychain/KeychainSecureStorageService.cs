using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Abstractions.Keychain;

/// <summary>
/// <see cref="ISecureStorageService"/> for platforms whose keychain can block on a modal
/// authorisation prompt — currently the macOS AppKit head.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> On the macOS AppKit head MAUI's <c>SecureStorage</c>
/// (<c>Microsoft.Maui.Platforms.MacOS.Essentials.SecureStorageImplementation</c>) reads generic
/// passwords with <c>SecKeyChain.QueryAsRecord</c> → <c>SecItemCopyMatching</c> and does not set
/// <c>kSecUseDataProtectionKeychain</c>, so the call is serviced by the <b>legacy file-based
/// keychain</b> (<c>SecItemCopyMatching_osx</c> → <c>SecKeychainSearchCopyNext</c> →
/// <c>SecKeychainItemCopyContent</c>). Legacy items carry a per-item ACL whose trusted-application
/// list is the creating binary's code signature. Debug builds of the macOS head are <b>ad-hoc
/// signed</b>, so every rebuild produces a new cdhash, the ACL stops matching, and macOS raises a
/// modal SecurityAgent dialog. <c>SecItemCopyMatching</c> then blocks until it is answered — it
/// never returns and never throws, so wrapping it in <c>Task.Run</c> only moves the deadlock off
/// the UI thread. Any startup code awaiting a token restore hangs forever.</para>
///
/// <para><b>The fix.</b> Automatic reads run with the platform's interactive authorisation UI
/// switched off (Apple's <c>SecKeychainSetUserInteractionAllowed(false)</c>), so a read that would
/// have prompted fails in single-digit milliseconds with a typed
/// <see cref="SecureStorageReadStatus.InteractionRequired"/> instead. Nothing is written, cleared
/// or deleted; the item is preserved untouched and the app simply routes to signed-out UI.</para>
///
/// <para><b>Self-healing writes.</b> An explicit, user-initiated save replaces the item rather
/// than merely rewriting its bytes, so the new item is owned by the running code signature and can
/// be read back by this build. See <see cref="IKeychainGate.Write"/>.</para>
///
/// <para><b>Logging.</b> This type never logs a key's value, nor any substring or length of it.
/// Only the key name and the status are logged.</para>
/// </remarks>
public sealed class KeychainSecureStorageService : ISecureStorageService
{
    /// <summary>
    /// Prefix applied to every account name this app stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MAUI's macOS SecureStorage puts generic passwords under the service name
    /// <c>maui_secure_storage</c> with no app scoping, so on a login keychain that name is shared
    /// by every MAUI application on the machine and the account name is the only thing separating
    /// them. An account called <c>auth_refresh</c> in a machine-global namespace is a collision
    /// waiting to happen: two MAUI apps that both store a refresh token overwrite, read and delete
    /// each other's, and neither has any way to notice.
    /// </para>
    /// <para>
    /// The service name is deliberately left alone. Changing it would strand every item written by
    /// previous builds behind a name nothing looks up any more, with no path to recover them — the
    /// abrupt rename this prefix exists to avoid. Prefixing the account instead keeps the old items
    /// addressable, so they can be migrated one at a time and only when a read proves they exist.
    /// </para>
    /// </remarks>
    internal const string AccountNamespace = "com.simplyprofound.sentencestudio.";

    private readonly IKeychainGate _gate;
    private readonly ILogger<KeychainSecureStorageService> _logger;

    /// <summary>
    /// <c>SecKeychainSetUserInteractionAllowed</c> is process-global, so reads that suppress the
    /// prompt must not overlap with an interactive operation that needs it.
    /// </summary>
    private readonly SemaphoreSlim _interactionLock = new(1, 1);

    /// <summary>
    /// Keys whose item is known to need user authorisation. Holds <b>scoped</b> account names.
    /// </summary>
    /// <remarks>
    /// A legacy keychain item's ACL only changes when the item is rewritten, and this type is the
    /// only writer, so once a non-interactive read has been refused the answer cannot change until
    /// we write, remove, or read that key interactively. Without this, an unreadable token makes
    /// every outgoing HTTP request re-query the keychain and re-log the refusal.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _needsInteraction = new(StringComparer.Ordinal);

    public KeychainSecureStorageService(
        IKeychainGate gate,
        ILogger<KeychainSecureStorageService> logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The account name this app actually stores <paramref name="key"/> under.</summary>
    private static string ScopedAccount(string key) => AccountNamespace + key;

    /// <inheritdoc/>
    /// <remarks>
    /// Kept for source compatibility. Defaults to a non-interactive read, because every existing
    /// caller in this app reads tokens automatically. Callers that want the richer outcome should
    /// use <see cref="TryGetAsync"/>.
    /// </remarks>
    public async Task<string?> GetAsync(string key)
    {
        var result = await TryGetAsync(key, SecureStorageAccess.NoInteraction).ConfigureAwait(false);
        return result.IsFound ? result.Value : null;
    }

    /// <inheritdoc/>
    public async Task<SecureStorageReadResult> TryGetAsync(
        string key,
        SecureStorageAccess access,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be a non-empty string.", nameof(key));

        if (!_gate.IsAvailable)
        {
            // NOT Missing. "Missing" is proof that nothing is stored, and callers act on it:
            // AuthTokenStore treats NotFound as proof a credential was removed, and the auth
            // provider treats it as proof there is no session. An unavailable gate proves neither.
            _logger.LogWarning(
                "Keychain gate unavailable; cannot determine whether '{Key}' is stored.", key);
            return SecureStorageReadResult.Failed;
        }

        if (cancellationToken.IsCancellationRequested)
            return SecureStorageReadResult.Cancelled;

        var account = ScopedAccount(key);

        if (access == SecureStorageAccess.NoInteraction && _needsInteraction.ContainsKey(account))
        {
            // Already established this item cannot be read without asking the user, and only this
            // type can change that. Skip the native call instead of re-asking on every request.
            _logger.LogDebug(
                "Keychain read of '{Key}' skipped: already known to need user authorisation.", key);
            return SecureStorageReadResult.NeedsInteraction;
        }

        // The native call is uninterruptible, so cancellation is honoured either side of it
        // rather than by abandoning a call that is already in flight.
        try
        {
            await _interactionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SecureStorageReadResult.Cancelled;
        }

        KeychainReadResult raw;
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return SecureStorageReadResult.Cancelled;

            // Scoped account only. This type never reads, writes or deletes a bare (un-namespaced)
            // account: those names live in a machine-global service every MAUI app can address, so
            // touching one on the strength of its name alone is either reading somebody else's
            // credential or destroying it. The one path allowed to look at them is
            // LegacyCredentialAdoption, which corroborates ownership from the payload first.
            raw = ReadAccount(account, access);
        }
        finally
        {
            _interactionLock.Release();
        }

        return Interpret(key, account, raw);
    }

    /// <summary>
    /// Turns a raw keychain result into the typed outcome, updating the needs-interaction cache.
    /// </summary>
    private SecureStorageReadResult Interpret(string key, string account, KeychainReadResult raw)
    {
        var status = KeychainStatusMapper.MapRead(raw.OsStatus);

        switch (status)
        {
            case SecureStorageReadStatus.Found:
                _needsInteraction.TryRemove(account, out _);
                var decoded = TryDecode(raw.Data);
                if (decoded is null)
                {
                    // Preserve the item. A corrupt payload is not a licence to delete a user's
                    // credentials — it may simply mean another writer used a different encoding.
                    _logger.LogWarning(
                        "Keychain item '{Key}' could not be decoded as UTF-8; leaving it in place.",
                        key);
                    return SecureStorageReadResult.MalformedValue;
                }
                return SecureStorageReadResult.FromValue(decoded);

            case SecureStorageReadStatus.NotFound:
                _needsInteraction.TryRemove(account, out _);
                return SecureStorageReadResult.Missing;

            case SecureStorageReadStatus.InteractionRequired:
                // Log once per key. Repeating this on every outgoing request buries the rest of
                // the log without adding information.
                if (_needsInteraction.TryAdd(account, 0))
                {
                    _logger.LogInformation(
                        "Keychain read of '{Key}' needs user authorisation (OSStatus {OsStatus}); " +
                        "skipping without prompting. The stored item was left untouched, and " +
                        "further automatic reads of this key will be skipped until it is rewritten.",
                        key, raw.OsStatus);
                }
                return SecureStorageReadResult.NeedsInteraction;

            default:
                _logger.LogWarning(
                    "Keychain read of '{Key}' failed with OSStatus {OsStatus}.", key, raw.OsStatus);
                return SecureStorageReadResult.Failed;
        }
    }

    private KeychainReadResult ReadAccount(string account, SecureStorageAccess access) =>
        access == SecureStorageAccess.NoInteraction
            ? ReadWithoutUserInteraction(account)
            : _gate.Read(account);

    /// <summary>
    /// Reads with the platform's interactive authorisation UI switched off, restoring whatever
    /// setting was in force beforehand.
    /// </summary>
    /// <remarks>
    /// The restore used to be a flat <c>SetUserInteractionAllowed(true)</c>. That is only correct
    /// when nothing else had already suppressed the prompt — and this method runs from token reads
    /// that can be nested inside a wider suppressed operation, where switching the SecurityAgent
    /// back on hands the outer caller the modal dialog it had specifically disabled. The prior
    /// state is captured before the flag is touched and put back afterwards; when the platform
    /// cannot report it, the restore falls back to <c>true</c>, which is both the historical
    /// behaviour and the fail-safe direction (a stranded process with the SecurityAgent disabled
    /// breaks every later keychain call, including deliberately interactive ones).
    /// </remarks>
    /// <param name="account">
    /// A fully-scoped account name, never a bare logical key. Named explicitly because passing a
    /// bare key here is precisely the mistake that made this app delete another product's
    /// credential, and a parameter called "key" invites it.
    /// </param>
    private KeychainReadResult ReadWithoutUserInteraction(string account)
    {
        var previous = TryCaptureInteractionState();
        var suppressed = _gate.SetUserInteractionAllowed(false);
        try
        {
            if (!suppressed)
            {
                // Refuse to make a call that could block indefinitely on a prompt nobody can answer.
                _logger.LogWarning(
                    "Could not disable keychain user interaction; skipping the read of '{Account}' " +
                    "rather than risk blocking on a prompt.",
                    account);
                return KeychainReadResult.Status(KeychainStatus.InteractionNotAllowed);
            }

            return _gate.Read(account);
        }
        finally
        {
            // Unconditional, and inside the finally rather than after the early return: a gate that
            // reported failure may still have mutated the process-global flag, and leaving the
            // SecurityAgent disabled would break every later keychain call in the process.
            RestoreInteractionState(previous);
        }
    }

    /// <summary>
    /// Reads the prompt flag before we change it, tolerating a gate that cannot report it.
    /// </summary>
    private bool? TryCaptureInteractionState()
    {
        try
        {
            return _gate.GetUserInteractionAllowed();
        }
        catch (Exception ex)
        {
            // Never fatal: not knowing the prior state costs us an accurate restore, not the read.
            _logger.LogDebug(ex, "Could not read the keychain user-interaction flag; will restore it to allowed.");
            return null;
        }
    }

    /// <summary>
    /// Puts the prompt flag back. <c>null</c> means the platform would not say what it was, in
    /// which case allowed is the safe answer — see <see cref="ReadWithoutUserInteraction"/>.
    /// </summary>
    private void RestoreInteractionState(bool? previous)
    {
        try
        {
            _gate.SetUserInteractionAllowed(previous ?? true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore the keychain user-interaction flag.");
        }
    }

    private static string? TryDecode(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return null;

        try
        {
            // Throw-on-invalid: a silently mangled token is worse than a reported one.
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(data);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    /// <exception cref="SecureStorageWriteException">The keychain refused the write.</exception>
    public async Task SetAsync(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be a non-empty string.", nameof(key));
        ArgumentNullException.ThrowIfNull(value);

        if (!_gate.IsAvailable)
            throw new InvalidOperationException("Keychain is unavailable on this platform.");

        var account = ScopedAccount(key);

        await _interactionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var status = _gate.Write(account, Encoding.UTF8.GetBytes(value));
            if (status != KeychainStatus.Success)
            {
                _logger.LogError(
                    "Failed to write keychain item '{Key}' (OSStatus {OsStatus}).", key, status);
                throw new SecureStorageWriteException(
                    key,
                    $"Failed to write secure storage item '{key}' (OSStatus {status}).");
            }

            _logger.LogDebug("Stored keychain item '{Key}'.", key);

            // The item now belongs to the running code signature, so it is readable again.
            _needsInteraction.TryRemove(account, out _);

            // Deliberately NOT followed by a delete of the bare account name. A previous revision
            // retired it here on the reasoning that "this app always wrote that name, so removing
            // it is no worse than overwriting it". That reasoning is wrong on this platform: the
            // service is machine-global, the account name is unqualified, and the legacy delete
            // path succeeds against items owned by another code signature (measured — the items
            // carry no ACLAuthorizationDelete entry). A write by this app is not evidence about who
            // owns the bare name, so this must not act on it.
        }
        finally
        {
            _interactionLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Removes the app-scoped account. Never the bare (un-namespaced) twin — see the body.
    /// </remarks>
    public bool Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_gate.IsAvailable)
            return false;

        var account = ScopedAccount(key);

        _interactionLock.Wait();
        try
        {
            var status = _gate.Delete(account);
            _needsInteraction.TryRemove(account, out _);

            // Scoped account only. Sign-out used to delete the bare twin as well, "in case an
            // install never completed a migration" — but on a shared service that is a delete of an
            // account name this app cannot prove it owns, executed on every sign-out. Suppressing
            // adoption is what stops a stale bare credential being used (see
            // LegacyCredentialAdoption.RetireAsync); deleting somebody else's item is not.
            if (status == KeychainStatus.Success)
                return true;

            if (status != KeychainStatus.ItemNotFound)
                _logger.LogWarning(
                    "Failed to remove keychain item '{Key}' (OSStatus {OsStatus}).", key, status);

            return false;
        }
        finally
        {
            _interactionLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately unsupported. MAUI's macOS SecureStorage stores generic passwords under the
    /// machine-global service name <c>maui_secure_storage</c> with no app scoping, so every MAUI
    /// app on the machine shares that namespace — a login keychain here really does contain another
    /// product's item under the same service. A "delete everything in the service" implementation
    /// would therefore destroy other applications' credentials. Callers must remove the keys they
    /// own, which is what <see cref="ISecureStorageService.Remove"/> is for.
    /// </remarks>
    public void RemoveAll() =>
        throw new NotSupportedException(
            "RemoveAll is not supported on macOS: MAUI's keychain service name " +
            "('maui_secure_storage') is shared by every MAUI app on the machine, so clearing it " +
            "would delete other applications' items. Remove individual keys instead.");
}
