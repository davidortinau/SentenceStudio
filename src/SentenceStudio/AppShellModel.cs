using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace SentenceStudio;

public partial class AppShellModel : ObservableObject
{   
    public LocalizationManager Localize => LocalizationManager.Instance;

    [RelayCommand]
    void ChangeUILanguage()
    {
        if(CultureInfo.CurrentUICulture.Name == "ko")
            Localize.SetCulture(new CultureInfo( "en", false ));
        else
            Localize.SetCulture(new CultureInfo( "ko", false ));

    }

}