using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Abstractions;

/// <summary>
/// The single owner of the persisted credential triple (access token, refresh token, expiry).
/// </summary>
/// <remarks>
/// <para><b>Why this is a type and not three <c>SetAsync</c> calls.</b> The three keys are only
/// meaningful together, and they are only meaningful for <i>one</i> account. Writing them one at a
/// time means a failure on the second or third write leaves account B's access token beside
/// account A's refresh token. The next silent refresh then presents A's refresh token, gets a
/// session for A, and the learner is quietly signed in as somebody else — on a shared machine that
/// is a cross-account disclosure, not an inconvenience. <see cref="PersistAsync"/> is therefore
/// all-or-nothing: on any failure it removes all three keys, not merely the ones it believes it
/// wrote, because a failed write can still have destroyed the item it was replacing (the macOS
/// gate implements a write as delete-then-add).</para>
///
/// <para><b>Why removal is verified rather than assumed.</b>
/// <see cref="ISecureStorageService.Remove"/> returns <c>false</c> both for "there was nothing
/// there" and for "I could not remove it", so its return value cannot distinguish a clean sign-out
/// from a failed one. Every removal here is therefore followed by a non-interactive read-back, and
/// only <see cref="SecureStorageReadStatus.NotFound"/> counts as proof. A read that needs user
/// authorisation, or that fails, is treated as "still possibly there" — the conservative reading,
/// because the cost of being wrong is a live refresh token on a machine whose user believes they
/// signed out.</para>
///
/// <para><b>Why a pending-cleanup latch.</b> A cleanup failure is not a transient nuisance that
/// resolves itself: the token is still on disk and the next cold start will happily restore it.
/// <see cref="CleanupPendingPreferenceKey"/> is set <i>before</i> the first removal, so a crash
/// part-way through latches it too, and it is cleared only when every owned key is verified gone —
/// or when a fresh <see cref="PersistAsync"/> puts the triple back into a known state for a known
/// account.</para>
///
/// <para><b>Logging.</b> Nothing here logs a token, a fragment of one, or a length. Only key names,
/// statuses and attempt counts.</para>
/// </remarks>
public sealed class AuthTokenStore
{
    public const string JwtKey = "auth_jwt";
    public const string RefreshKey = "auth_refresh";
    public const string ExpiresKey = "auth_expires";

    /// <summary>
    /// Latched when credential removal starts and cleared only when it is proven complete. While
    /// set, every silent-restore path must refuse to restore a session.
    /// </summary>
    public const string CleanupPendingPreferenceKey = "auth_cleanup_incomplete";

    /// <summary>
    /// Every key this app owns under the secure store. Rollback and sign-out operate on exactly
    /// this set — never on "everything in the store", which on macOS is a machine-global namespace
    /// shared with other applications.
    /// </summary>
    public static readonly IReadOnlyList<string> OwnedKeys = new[] { JwtKey, RefreshKey, ExpiresKey };

    /// <summary>
    /// Removal attempts per key. Bounded so a wedged keystore cannot turn sign-out into an
    /// unbounded retry loop on the UI thread; two attempts covers a transient failure without
    /// pretending a persistent one will resolve.
    /// </summary>
    private const int MaxRemoveAttempts = 2;

    private readonly ISecureStorageService _secureStorage;
    private readonly IPreferencesService _preferences;
    private readonly ILogger _logger;

    public AuthTokenStore(
        ISecureStorageService secureStorage,
        IPreferencesService preferences,
        ILogger logger)
    {
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// True when a previous sign-out (or a crash during one) left credentials that could not be
    /// proven gone. Silent-restore paths must treat this as "there is no session".
    /// </summary>
    public bool IsCleanupPending
    {
        get
        {
            try
            {
                return _preferences.Get(CleanupPendingPreferenceKey, false);
            }
            catch (Exception ex)
            {
                // Unreadable preference: assume the worst rather than restore a session we cannot
                // vouch for.
                _logger.LogWarning(ex, "Could not read the credential cleanup flag; assuming cleanup is pending.");
                return true;
            }
        }
    }

    /// <summary>
    /// Writes the credential triple atomically: either all three keys hold this account's values,
    /// or none of them holds anything and an <see cref="AuthTokenPersistenceException"/> is thrown.
    /// </summary>
    /// <exception cref="AuthTokenPersistenceException">
    /// Any write failed. All three owned keys have been removed on a best-effort, verified basis
    /// before this is thrown.
    /// </exception>
    public async Task PersistAsync(string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        // Latch first. A crash between the first and third write is indistinguishable from a
        // failure we caught, and both leave a triple that must not be trusted on next launch.
        SetCleanupPending(true);

        var stage = JwtKey;
        try
        {
            await _secureStorage.SetAsync(JwtKey, accessToken).ConfigureAwait(false);

            stage = RefreshKey;
            await _secureStorage.SetAsync(RefreshKey, refreshToken).ConfigureAwait(false);

            stage = ExpiresKey;
            await _secureStorage
                .SetAsync(ExpiresKey, expiresAt.ToString("O", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to persist the credential triple at key '{Key}'; rolling back all owned keys.",
                stage);

            var uncleared = await TryClearOwnedKeysAsync().ConfigureAwait(false);

            // Only unlatch when the rollback is provably complete. Residue must keep blocking
            // silent restore, because that residue may belong to a different account.
            SetCleanupPending(uncleared.Count > 0);

            throw new AuthTokenPersistenceException(stage, uncleared, ex);
        }

        // All three keys now hold this account's values, so the store is in a known state again.
        SetCleanupPending(false);
        _logger.LogInformation("Credential triple persisted; expires at {Expires}.", expiresAt);
    }

    /// <summary>
    /// Removes every owned key and proves each one is gone.
    /// </summary>
    /// <returns>
    /// <see cref="SignOutOutcome.Clean"/> when all three were verified absent.
    /// </returns>
    /// <exception cref="AuthTokenCleanupException">
    /// At least one key could not be proven gone. The pending-cleanup latch is left set.
    /// </exception>
    public async Task<SignOutOutcome> ClearAsync()
    {
        // Set before removing: if the process dies mid-removal the next launch must not restore.
        SetCleanupPending(true);

        var uncleared = await TryClearOwnedKeysAsync().ConfigureAwait(false);

        if (uncleared.Count == 0)
        {
            SetCleanupPending(false);
            _logger.LogInformation("Stored credentials removed and verified absent.");
            return SignOutOutcome.Clean;
        }

        _logger.LogError(
            "Sign-out could not confirm removal of {Count} credential key(s): {Keys}. " +
            "Silent restore stays blocked until the next successful sign-in.",
            uncleared.Count,
            string.Join(", ", uncleared));

        throw new AuthTokenCleanupException(uncleared);
    }

    /// <summary>
    /// Re-runs removal after an earlier failure, without throwing. For a startup guard that wants
    /// to take one more bounded shot at clearing residue before refusing to restore.
    /// </summary>
    public async Task<SignOutOutcome> TryClearAsync()
    {
        var uncleared = await TryClearOwnedKeysAsync().ConfigureAwait(false);
        SetCleanupPending(uncleared.Count > 0);

        return uncleared.Count == 0
            ? SignOutOutcome.Clean
            : SignOutOutcome.Failed(uncleared);
    }

    /// <summary>
    /// Removes each owned key and returns the names of those that could not be proven gone.
    /// Never throws — a rollback path must not fail on top of the failure that triggered it.
    /// </summary>
    private async Task<IReadOnlyList<string>> TryClearOwnedKeysAsync()
    {
        var uncleared = new List<string>(OwnedKeys.Count);

        foreach (var key in OwnedKeys)
        {
            if (!await TryClearKeyAsync(key).ConfigureAwait(false))
                uncleared.Add(key);
        }

        return uncleared;
    }

    private async Task<bool> TryClearKeyAsync(string key)
    {
        for (var attempt = 1; attempt <= MaxRemoveAttempts; attempt++)
        {
            try
            {
                _secureStorage.Remove(key);
            }
            catch (Exception ex)
            {
                // Deliberately not fatal, and deliberately not a reason to skip the read-back:
                // some platform implementations throw after having removed the item.
                _logger.LogWarning(
                    ex,
                    "Removing secure-storage key '{Key}' threw on attempt {Attempt} of {Max}.",
                    key, attempt, MaxRemoveAttempts);
            }

            // Remove() cannot distinguish "wasn't there" from "couldn't remove it", so the only
            // trustworthy evidence is a read that comes back empty.
            var absent = await TryVerifyAbsentAsync(key).ConfigureAwait(false);
            if (absent == true)
                return true;

            if (absent is null)
            {
                // The keystore would not answer without user authorisation, or errored. Retrying
                // will not change that, and we must not claim success we cannot demonstrate.
                _logger.LogWarning(
                    "Could not verify that secure-storage key '{Key}' is gone; treating it as still present.",
                    key);
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the key back non-interactively.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the store reports nothing under the key, <c>false</c> when something is
    /// still stored, and <c>null</c> when the store would not say without prompting the user or
    /// reported an error.
    /// </returns>
    private async Task<bool?> TryVerifyAbsentAsync(string key)
    {
        try
        {
            // NoInteraction: sign-out can run from a background path or an app-menu handler, and a
            // modal keychain prompt there is exactly the deadlock the keychain gate exists to avoid.
            var read = await _secureStorage
                .TryGetAsync(key, SecureStorageAccess.NoInteraction)
                .ConfigureAwait(false);

            return read.Status switch
            {
                SecureStorageReadStatus.NotFound => true,

                // Something is still under the key. Malformed counts: the bytes are still there,
                // we simply could not decode them.
                SecureStorageReadStatus.Found or SecureStorageReadStatus.Malformed => false,

                // InteractionRequired / Failed / Cancelled — unknown, and unknown is not clear.
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verifying removal of secure-storage key '{Key}' threw.", key);
            return null;
        }
    }

    private void SetCleanupPending(bool pending)
    {
        try
        {
            if (pending)
                _preferences.Set(CleanupPendingPreferenceKey, true);
            else
                _preferences.Remove(CleanupPendingPreferenceKey);
        }
        catch (Exception ex)
        {
            // Non-fatal on its own; the caller still gets a truthful exception from the operation
            // that mattered. Worth a line because a latch that cannot be written is a latch that
            // will not protect the next cold start.
            _logger.LogWarning(
                ex,
                "Could not update the credential cleanup flag (pending={Pending}).",
                pending);
        }
    }
}
