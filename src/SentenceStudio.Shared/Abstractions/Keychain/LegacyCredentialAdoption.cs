using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Abstractions.Keychain;

/// <summary>
/// The only code in this application permitted to look at the pre-namespacing ("bare") keychain
/// accounts, and the only code that may adopt what it finds there.
/// </summary>
/// <remarks>
/// <para><b>Why this is separate from the storage service.</b> Blind, per-key migration inside
/// <c>SetAsync</c>/<c>TryGetAsync</c> could only ever reason about an account <em>name</em>, and on
/// macOS the name is worthless as evidence: MAUI stores every app's secrets in the machine-global
/// service <c>maui_secure_storage</c> under the unqualified key name. A per-key migration therefore
/// either copies whatever it finds — a foreign refresh token, which this app would then present to
/// the SentenceStudio API — or deletes whatever it finds, which destroys another product's
/// credential. Both were real defects in the previous revision.</para>
///
/// <para><b>The rule.</b> A bare item is foreign until corroborated. Corroboration is a property of
/// the <em>triple</em>, not of a key: all three values must be readable and self-consistent, and
/// the access token's SentenceStudio profile claim must equal the <c>active_profile_id</c> this
/// install already holds in its own preference store — state no other application can write. See
/// <see cref="LegacyCredentialOwnership"/>.</para>
///
/// <para><b>Nothing is ever deleted.</b> Even a fully corroborated triple is left exactly where it
/// is. Adoption copies it into the app-scoped accounts, verifies the copy byte-for-byte, and then
/// records a durable decision so the bare accounts are never read again on this install. Leaving
/// them costs a stale copy of a credential that was already there; deleting them risks removing an
/// item this app cannot prove it owns. Given the shared namespace, that trade is not close.</para>
///
/// <para><b>Sign-out closes the door permanently.</b> <see cref="RetireAsync"/> records
/// <see cref="LegacyAdoptionOutcome.Retired"/>, so no later launch can re-adopt and silently sign
/// the learner back in after they asked to be signed out.</para>
///
/// <para><b>Logging.</b> No token, no fragment of one, and no length. Verdicts and key names only.</para>
/// </remarks>
public sealed class LegacyCredentialAdoption
{
    /// <summary>
    /// The decision is recorded for the credential triple as a whole, never per key: the three
    /// values are only meaningful together, so a per-key record could half-adopt a session.
    /// </summary>
    internal const string CredentialGroupId = "auth_triple_v1";

    internal const string AccessTokenKey = "auth_jwt";
    internal const string RefreshTokenKey = "auth_refresh";
    internal const string ExpiresKey = "auth_expires";

    /// <summary>The bare account names, in the order they are read.</summary>
    internal static readonly IReadOnlyList<string> LegacyAccounts =
        new[] { AccessTokenKey, RefreshTokenKey, ExpiresKey };

    /// <summary>Preference this app writes for itself, used as the ownership anchor.</summary>
    internal const string ActiveProfilePreferenceKey = "active_profile_id";

    private readonly IKeychainGate _gate;
    private readonly ISecureStorageService _scopedStorage;
    private readonly ILegacyAdoptionJournal _journal;
    private readonly IPreferencesService _preferences;
    private readonly ILogger<LegacyCredentialAdoption> _logger;

    /// <summary>Serialises against the process-global keychain interaction flag.</summary>
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    private int _attemptedThisProcess;

    public LegacyCredentialAdoption(
        IKeychainGate gate,
        ISecureStorageService scopedStorage,
        ILegacyAdoptionJournal journal,
        IPreferencesService preferences,
        ILogger<LegacyCredentialAdoption> logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _scopedStorage = scopedStorage ?? throw new ArgumentNullException(nameof(scopedStorage));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Optional issuer to require on the access token.</summary>
    public string? ExpectedIssuer { get; init; }

    /// <summary>Optional audience to require on the access token.</summary>
    public string? ExpectedAudience { get; init; }

    /// <summary>
    /// Runs the one adoption attempt this install is allowed, if it has not already been decided.
    /// </summary>
    /// <returns>The verdict reached, for logging and tests.</returns>
    public async Task<LegacyOwnershipVerdict> TryAdoptAsync(CancellationToken cancellationToken = default)
    {
        // Durable first: a decision recorded on a previous launch is final.
        var recorded = _journal.Read(CredentialGroupId);
        if (recorded != LegacyAdoptionOutcome.Undecided)
        {
            _logger.LogDebug(
                "Legacy keychain adoption already concluded for this install ({Outcome}); not probing.",
                recorded);
            return LegacyOwnershipVerdict.AlreadyDecided;
        }

        // Then process-level: never probe twice, even if the journal write failed.
        if (Interlocked.Exchange(ref _attemptedThisProcess, 1) != 0)
            return LegacyOwnershipVerdict.AlreadyDecided;

        if (!_gate.IsAvailable)
        {
            _logger.LogDebug("Keychain unavailable; not probing legacy accounts.");
            return LegacyOwnershipVerdict.Unreadable;
        }

        LegacyCredentialTriple? triple;
        try
        {
            triple = await ReadLegacyTripleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the legacy keychain accounts; leaving them untouched.");
            _journal.Record(CredentialGroupId, LegacyAdoptionOutcome.Rejected);
            return LegacyOwnershipVerdict.Unreadable;
        }

        var localProfileId = TryReadActiveProfileId();

        var verdict = LegacyCredentialOwnership.Corroborate(
            triple, localProfileId, ExpectedIssuer, ExpectedAudience);

        if (verdict != LegacyOwnershipVerdict.Owned)
        {
            // Absent is not a rejection worth remembering forever — there is simply nothing there,
            // and an install that later writes bare items is not a scenario this app creates. Every
            // other verdict is remembered, so a foreign item is probed exactly once.
            if (verdict != LegacyOwnershipVerdict.Absent)
            {
                _journal.Record(CredentialGroupId, LegacyAdoptionOutcome.Rejected);
                _logger.LogInformation(
                    "Legacy keychain credentials were not adopted ({Verdict}). They belong to " +
                    "another application or cannot be proven to belong to this one; they were left " +
                    "untouched and will not be read again on this install.",
                    verdict);
            }

            return verdict;
        }

        var adopted = await CopyToScopedAccountsAsync(triple!.Value).ConfigureAwait(false);
        if (!adopted)
        {
            // The copy failed or did not verify. Record nothing as adopted, delete nothing, and let
            // the learner sign in normally.
            _journal.Record(CredentialGroupId, LegacyAdoptionOutcome.Rejected);
            return LegacyOwnershipVerdict.Incoherent;
        }

        _journal.Record(CredentialGroupId, LegacyAdoptionOutcome.Adopted);
        _logger.LogInformation(
            "Adopted this install's own pre-namespacing credentials into app-scoped keychain " +
            "accounts. The original items were left in place and will not be read again.");

        return LegacyOwnershipVerdict.Owned;
    }

    /// <summary>
    /// Closes adoption permanently for this install. Called on sign-out.
    /// </summary>
    /// <remarks>
    /// Without this, a learner could sign out — clearing the app-scoped accounts — and have the next
    /// launch re-adopt the still-present bare triple and sign them straight back in. Recording the
    /// decision is what makes sign-out durable; deleting the bare items is not an option, because
    /// this app cannot prove it owns them.
    /// </remarks>
    public void Retire()
    {
        _journal.Record(CredentialGroupId, LegacyAdoptionOutcome.Retired);
        Interlocked.Exchange(ref _attemptedThisProcess, 1);
        _logger.LogDebug("Legacy keychain adoption retired for this install.");
    }

    private string? TryReadActiveProfileId()
    {
        try
        {
            return _preferences.Get<string?>(ActiveProfilePreferenceKey, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the active profile id; refusing legacy adoption.");
            return null;
        }
    }

    /// <summary>
    /// Reads the three bare accounts with the platform prompt suppressed. Read-only: this method
    /// never writes and never deletes.
    /// </summary>
    private async Task<LegacyCredentialTriple?> ReadLegacyTripleAsync(CancellationToken cancellationToken)
    {
        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        var previous = TryCaptureInteractionState();
        var suppressed = _gate.SetUserInteractionAllowed(false);
        try
        {
            if (!suppressed)
            {
                // Never risk a modal prompt for a probe the learner did not ask for.
                _logger.LogDebug("Could not suppress the keychain prompt; skipping the legacy probe.");
                return null;
            }

            var values = new string[LegacyAccounts.Count];
            for (var i = 0; i < LegacyAccounts.Count; i++)
            {
                var raw = _gate.Read(LegacyAccounts[i]);
                var status = KeychainStatusMapper.MapRead(raw.OsStatus);

                if (status != SecureStorageReadStatus.Found)
                {
                    // Absent, ACL-refused, or errored. All three mean the same thing here: no
                    // corroboration is possible, so nothing is adopted and nothing is touched.
                    // Crucially this does NOT feed the scoped storage service's needs-interaction
                    // cache — a refused *legacy* read says nothing about the app's own accounts.
                    _logger.LogDebug(
                        "Legacy account '{Account}' is not readable ({Status}); no adoption.",
                        LegacyAccounts[i], status);
                    return null;
                }

                var decoded = TryDecode(raw.Data);
                if (decoded is null)
                    return null;

                values[i] = decoded;
            }

            return new LegacyCredentialTriple(values[0], values[1], values[2]);
        }
        finally
        {
            RestoreInteractionState(previous);
            _probeLock.Release();
        }
    }

    /// <summary>
    /// Copies a corroborated triple into the app-scoped accounts and verifies every value reads
    /// back byte-identically. Any failure aborts without touching the originals.
    /// </summary>
    private async Task<bool> CopyToScopedAccountsAsync(LegacyCredentialTriple triple)
    {
        var pairs = new[]
        {
            (Key: AccessTokenKey, Value: triple.AccessToken),
            (Key: RefreshTokenKey, Value: triple.RefreshToken),
            (Key: ExpiresKey, Value: triple.Expires),
        };

        foreach (var (key, value) in pairs)
        {
            try
            {
                await _scopedStorage.SetAsync(key, value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not copy '{Key}' into its app-scoped account.", key);
                return false;
            }

            var readBack = await _scopedStorage
                .TryGetAsync(key, SecureStorageAccess.NoInteraction)
                .ConfigureAwait(false);

            if (!readBack.IsFound || !string.Equals(readBack.Value, value, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Adopted value for '{Key}' did not read back identically; abandoning adoption.",
                    key);
                return false;
            }
        }

        return true;
    }

    private bool? TryCaptureInteractionState()
    {
        try
        {
            return _gate.GetUserInteractionAllowed();
        }
        catch (Exception)
        {
            return null;
        }
    }

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
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(data);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
