using System;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Abstractions.Keychain;

/// <summary>
/// <see cref="ILegacyAdoptionJournal"/> over the app's own preference store.
/// </summary>
/// <remarks>
/// App-scoped by construction: <see cref="IPreferencesService"/> is this application's private
/// key/value store, so a decision recorded here cannot be seen — or forged — by the other
/// applications that share the keychain service name.
/// </remarks>
public sealed class PreferencesLegacyAdoptionJournal : ILegacyAdoptionJournal
{
    /// <summary>
    /// Prefix for the recorded decisions. Deliberately explicit: anybody auditing the preference
    /// store should be able to tell what these are without reading this file.
    /// </summary>
    internal const string KeyPrefix = "keychain_legacy_adoption_";

    private readonly IPreferencesService _preferences;
    private readonly ILogger<PreferencesLegacyAdoptionJournal>? _logger;

    public PreferencesLegacyAdoptionJournal(
        IPreferencesService preferences,
        ILogger<PreferencesLegacyAdoptionJournal>? logger = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _logger = logger;
    }

    /// <inheritdoc/>
    public LegacyAdoptionOutcome Read(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentException("Group id must be a non-empty string.", nameof(groupId));

        try
        {
            var raw = _preferences.Get<string?>(KeyPrefix + groupId, null);
            if (string.IsNullOrEmpty(raw))
                return LegacyAdoptionOutcome.Undecided;

            return Enum.TryParse<LegacyAdoptionOutcome>(raw, ignoreCase: true, out var parsed)
                ? parsed
                // An unrecognised value is somebody else's data or a downgrade artefact. Refusing
                // is the safe reading: it can only ever prevent an adoption, never cause one.
                : LegacyAdoptionOutcome.Rejected;
        }
        catch (Exception ex)
        {
            // A preference store we cannot read is not permission to adopt a credential.
            _logger?.LogWarning(ex, "Could not read the legacy keychain adoption decision; refusing adoption.");
            return LegacyAdoptionOutcome.Rejected;
        }
    }

    /// <inheritdoc/>
    public void Record(string groupId, LegacyAdoptionOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentException("Group id must be a non-empty string.", nameof(groupId));

        try
        {
            _preferences.Set(KeyPrefix + groupId, outcome.ToString());
        }
        catch (Exception ex)
        {
            // Recording is best effort. Failing to record can only cause the decision to be
            // re-derived next launch, which re-applies the same evidence rules.
            _logger?.LogWarning(ex, "Could not record the legacy keychain adoption decision.");
        }
    }
}
