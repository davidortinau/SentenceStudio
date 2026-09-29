using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace SentenceStudio.Abstractions;

/// <summary>
/// <see cref="ISecureStorageService"/> over MAUI's <c>SecureStorage</c> for the iOS, Android and
/// Mac Catalyst heads.
/// </summary>
/// <remarks>
/// <para><b>There is no plaintext fallback, by design.</b> This type used to catch any
/// <c>SecureStorage</c> exception, latch a process-wide flag, and route every subsequent read and
/// write to <c>Preferences</c> under a <c>__ss_fb_</c> prefix. The keys it stores are the JWT
/// access token, the refresh token and the expiry, so that fallback wrote long-lived bearer
/// credentials to <c>NSUserDefaults</c> / <c>SharedPreferences</c> — unencrypted, readable by
/// anything with file access to the app container, and included in unencrypted device backups. It
/// was also a one-way latch: a single transient failure at startup downgraded credential storage
/// for the rest of the process, silently, with one warning line to show for it.</para>
///
/// <para>The fallback is removed rather than kept behind a debug-only compilation gate. A
/// debug-only variant that is forbidden from storing access or refresh tokens has nothing left to
/// store — those three keys are the entire purpose of this type — so it would be dead code that
/// still reads as a sanctioned escape hatch. The failure mode it was written for (Mac Catalyst
/// debug builds without keychain entitlements) is addressed at the source: see the Mac Catalyst
/// entitlements guidance in AGENTS.md, and <c>KeychainSecureStorageService</c> for the macOS AppKit
/// head.</para>
///
/// <para><b>Fail closed.</b> A failed write throws, so a caller cannot believe a session was
/// persisted when it was not. A failed read reports <see cref="SecureStorageReadStatus.Failed"/>
/// rather than "nothing stored", so a caller cannot mistake "the keystore would not answer" for
/// "the credential is gone" — the distinction sign-out verification depends on.</para>
///
/// <para><b>Logging.</b> Never logs a stored value, a fragment of one, or a length.</para>
/// </remarks>
public sealed class MauiSecureStorageService : ISecureStorageService
{
    /// <summary>
    /// Prefix the removed <c>Preferences</c> fallback used. Retained only so the plaintext
    /// credentials it left behind on existing installs can be purged.
    /// </summary>
    private const string LegacyPlaintextFallbackPrefix = "__ss_fb_";

    private readonly ILogger<MauiSecureStorageService> _logger;

    private int _legacyResiduePurged;

    public MauiSecureStorageService(ILogger<MauiSecureStorageService> logger)
    {
        _logger = logger;
        PurgeLegacyPlaintextCredentials();
    }

    /// <inheritdoc/>
    public async Task<string?> GetAsync(string key)
    {
        var result = await TryGetAsync(key, SecureStorageAccess.NoInteraction).ConfigureAwait(false);
        return result.IsFound ? result.Value : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Overridden rather than left to the interface default, which maps every non-value outcome to
    /// "not found". Sign-out proves a credential is gone by reading it back, and a platform error
    /// reported as "not found" would let it declare success over a credential still on disk.
    /// </remarks>
    public async Task<SecureStorageReadResult> TryGetAsync(
        string key,
        SecureStorageAccess access,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be a non-empty string.", nameof(key));

        if (cancellationToken.IsCancellationRequested)
            return SecureStorageReadResult.Cancelled;

        try
        {
            // Task.Run, not a bare await: SecureStorage is only async by signature on Apple
            // platforms — the platform read happens synchronously on the calling thread. Pushing
            // it to the thread pool keeps a UI-thread caller responsive.
            //
            // NOTE: this does NOT make an interactive keychain prompt safe. On the macOS AppKit
            // head the native read blocks until the prompt is answered and never throws, so the
            // await never completes and startup wedges. That head therefore does NOT use this
            // type — SentenceStudio.MacOS registers KeychainSecureStorageService +
            // MacOSKeychainGate instead, which suppresses the prompt for automatic reads. See
            // src/SentenceStudio.MacOS/Platform/MacOSKeychainGate.cs.
            var value = await Task.Run(() => SecureStorage.Default.GetAsync(key), cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrEmpty(value)
                ? SecureStorageReadResult.Missing
                : SecureStorageReadResult.FromValue(value!);
        }
        catch (OperationCanceledException)
        {
            return SecureStorageReadResult.Cancelled;
        }
        catch (Exception ex)
        {
            // Key name and exception only. Fail closed: report the failure as a failure so callers
            // can tell it apart from an empty slot.
            _logger.LogWarning(
                ex,
                "Secure storage read of '{Key}' failed. Reporting failure rather than treating it " +
                "as absent; nothing was written or cleared.",
                key);

            return SecureStorageReadResult.Failed;
        }
    }

    /// <inheritdoc/>
    /// <exception cref="SecureStorageWriteException">
    /// The platform keystore refused the write. Nothing is written anywhere else.
    /// </exception>
    public async Task SetAsync(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be a non-empty string.", nameof(key));
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            // Off the calling thread for the same reason as TryGetAsync — see the comment there.
            await Task.Run(() => SecureStorage.Default.SetAsync(key, value)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Secure storage write of '{Key}' failed. The value was NOT stored anywhere else.",
                key);

            throw new SecureStorageWriteException(key, ex);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns <c>false</c> rather than throwing so a sign-out path can attempt every key it owns.
    /// A <c>false</c> here is not proof the item survived — <c>SecureStorage.Remove</c> also
    /// returns <c>false</c> for a key that was never present — which is why callers verify with a
    /// read-back instead of trusting this value.
    /// </remarks>
    public bool Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        try
        {
            return SecureStorage.Default.Remove(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Secure storage removal of '{Key}' threw.", key);
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Safe on the heads this type serves: iOS and Mac Catalyst scope the keychain to the app's
    /// access group and Android to the app's own encrypted store, so this clears only this app's
    /// items. The macOS AppKit head, whose keychain service name is machine-global, uses
    /// <c>KeychainSecureStorageService</c>, which refuses this operation outright.
    /// </remarks>
    public void RemoveAll()
    {
        try
        {
            SecureStorage.Default.RemoveAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Secure storage RemoveAll threw.");
        }
    }

    /// <summary>
    /// Deletes the plaintext credentials the removed <c>Preferences</c> fallback may have written
    /// on this install.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Removing the fallback stops new plaintext credentials being created; it does nothing about
    /// the ones already on disk, which stay readable until something deletes them. This is that
    /// something.
    /// </para>
    /// <para>
    /// Scoped to exactly the three credential keys this app owns, each behind the fallback's own
    /// marker prefix. No enumeration, no wildcard, no <c>Preferences.Clear()</c> — every other
    /// preference on the device is left untouched.
    /// </para>
    /// </remarks>
    private void PurgeLegacyPlaintextCredentials()
    {
        if (Interlocked.Exchange(ref _legacyResiduePurged, 1) != 0)
            return;

        foreach (var key in AuthTokenStore.OwnedKeys)
        {
            var legacyKey = LegacyPlaintextFallbackPrefix + key;
            try
            {
                if (!Preferences.Default.ContainsKey(legacyKey))
                    continue;

                Preferences.Default.Remove(legacyKey);
                _logger.LogWarning(
                    "Removed a plaintext credential left in Preferences by the retired secure-storage " +
                    "fallback ('{Key}'). Sign in again if the session does not restore.",
                    legacyKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not purge the retired plaintext fallback entry '{Key}'.",
                    legacyKey);
            }
        }
    }
}
