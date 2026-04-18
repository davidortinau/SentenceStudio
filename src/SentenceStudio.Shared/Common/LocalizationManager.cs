using System.ComponentModel;
using System.Globalization;
using SentenceStudio.Resources.Strings;
using Microsoft.Extensions.Logging;

namespace SentenceStudio;

public class LocalizationManager : INotifyPropertyChanged {
    public event PropertyChangedEventHandler PropertyChanged;
    private static readonly Lazy<LocalizationManager> _instance = new(()=> new LocalizationManager());
    private static ILogger<LocalizationManager>? _logger;

    private LocalizationManager()
    {
        AppResources.Culture = CultureInfo.CurrentUICulture;
    }

    public static LocalizationManager Instance => _instance.Value;

    public static void Initialize(ILogger<LocalizationManager> logger)
    {
        _logger = logger;
    }

    public object this[string resourceKey] => AppResources.ResourceManager.GetObject(resourceKey, AppResources.Culture) ?? string.Empty;

    /// <summary>
    /// Returns a localized string for the given key under the specified culture (or the
    /// manager's current culture if null). Exposed so Blazor Server's per-circuit
    /// localization service can look up strings without mutating process-wide culture.
    /// </summary>
    public string GetString(string key, CultureInfo? culture = null)
        => AppResources.ResourceManager.GetString(key, culture ?? AppResources.Culture) ?? key;

    public void SetCulture(CultureInfo culture)
    {
        try{
            _logger?.LogDebug("Current Culture: {CultureName}", CultureInfo.CurrentUICulture.Name);
            // CultureInfo.CurrentUICulture = new CultureInfo( "ko", false );
            AppResources.Culture =
            CultureInfo.DefaultThreadCurrentCulture=
            CultureInfo.DefaultThreadCurrentUICulture =
            CultureInfo.CurrentCulture =
            CultureInfo.CurrentUICulture =
            culture;

            _logger?.LogDebug("New Culture: {CultureName}", CultureInfo.CurrentUICulture.Name);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error setting culture");
        }
    }

}
