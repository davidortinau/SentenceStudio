using SentenceStudio.Common;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Bridge between LocalizationManager and Blazor components.
/// Inject into Razor components to access localized strings.
/// </summary>
public class BlazorLocalizationService
{
    private readonly LocalizationManager _localize = LocalizationManager.Instance;

    public string this[string key] => $"{_localize[key]}";

    public string Get(string key) => $"{_localize[key]}";

    public string Get(string key, params object[] args)
        => string.Format($"{_localize[key]}", args);
}
