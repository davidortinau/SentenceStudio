using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// MAUI implementation: reads active_profile_id from device preferences.
/// Safe for single-user-per-device scenarios.
/// </summary>
public class PreferencesActiveUserProvider : IActiveUserProvider
{
    private readonly IPreferencesService? _preferences;

    public PreferencesActiveUserProvider(IPreferencesService? preferences = null)
    {
        _preferences = preferences;
    }

    public string? GetActiveProfileId()
    {
        var id = _preferences?.Get("active_profile_id", string.Empty);
        return string.IsNullOrEmpty(id) ? null : id;
    }

    // MAUI is single-user per device; falling back to first profile is safe
    public bool ShouldFallbackToFirstProfile => true;
}
