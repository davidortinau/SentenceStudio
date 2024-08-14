using System.ComponentModel;
using System.Globalization;
using SentenceStudio.Resources.Strings;

namespace SentenceStudio;

public class LocalizationManager : INotifyPropertyChanged {
    public event PropertyChangedEventHandler PropertyChanged;
    private static readonly Lazy<LocalizationManager> _instance = new(()=> new LocalizationManager());
    private LocalizationManager()
    {
        AppResources.Culture = CultureInfo.CurrentUICulture;
    }
    public static LocalizationManager Instance => _instance.Value;

    public object this[string resourceKey] => AppResources.ResourceManager.GetObject(resourceKey, AppResources.Culture) ?? string.Empty;

    public void SetCulture(CultureInfo culture)
    {
        try{
            Debug.WriteLine($"Current Culture: {CultureInfo.CurrentUICulture.Name}");
            // CultureInfo.CurrentUICulture = new CultureInfo( "ko", false );
            AppResources.Culture =
            CultureInfo.DefaultThreadCurrentCulture=
            CultureInfo.DefaultThreadCurrentUICulture =
            CultureInfo.CurrentCulture =
            CultureInfo.CurrentUICulture =
            culture;

            Debug.WriteLine($"New Culture: {CultureInfo.CurrentUICulture.Name}");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

}
