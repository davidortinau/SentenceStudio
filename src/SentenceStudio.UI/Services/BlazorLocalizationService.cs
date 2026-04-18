using System.Globalization;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Scoped, circuit-safe localization service for Blazor. Each circuit holds its own
/// CultureInfo — never mutates the process-wide DefaultThread culture, so two users
/// on different languages don't collide on the server.
///
/// Raise <see cref="CultureChanged"/> from <see cref="SetCulture"/>; subscribers
/// (Razor components) should call StateHasChanged in response.
/// </summary>
public class BlazorLocalizationService
{
    private CultureInfo _culture;

    public BlazorLocalizationService()
    {
        _culture = CultureInfo.CurrentUICulture ?? new CultureInfo("en");
    }

    /// <summary>Fires when <see cref="SetCulture"/> is called. Components should StateHasChanged.</summary>
    public event Action? CultureChanged;

    public CultureInfo Culture => _culture;

    public string this[string key] => Get(key);

    public string Get(string key)
        => SentenceStudio.LocalizationManager.Instance.GetString(key, _culture);

    public string Get(string key, params object[] args)
    {
        var value = SentenceStudio.LocalizationManager.Instance.GetString(key, _culture);
        return string.Format(_culture, value, args);
    }

    /// <summary>
    /// Update this circuit's culture and notify subscribers. Does NOT mutate process-wide
    /// culture statics; callers writing culture to MAUI singletons or cookies should do so
    /// outside this method.
    /// </summary>
    public void SetCulture(CultureInfo culture)
    {
        if (culture is null) return;
        _culture = culture;
        // Keep the current async-local scope aligned so any downstream .NET formatting
        // in this request/circuit honors the new culture.
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureChanged?.Invoke();
    }

    public void SetCulture(string cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) return;
        try
        {
            SetCulture(new CultureInfo(cultureName));
        }
        catch (CultureNotFoundException)
        {
            // Ignore — unknown culture names shouldn't crash the circuit.
        }
    }
}
