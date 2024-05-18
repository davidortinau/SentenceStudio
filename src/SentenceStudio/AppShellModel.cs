using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio;

public partial class AppShellModel : ObservableObject
{
    private UserProfileService _userProfileService;

    public LocalizationManager Localize => LocalizationManager.Instance;

    [RelayCommand]
    async Task ChangeUILanguage()
    {
        Debug.WriteLine($"ChangeUILanguage Current Culture: {CultureInfo.CurrentUICulture.Name}");
        var culture = (CultureInfo.CurrentUICulture.Name == "ko-KR") ? "en-US" : "ko-KR";
        Localize.SetCulture(new CultureInfo( culture, false ));
        await _userProfileService.SaveDisplayCultureAsync(culture);

    }

    public AppShellModel(IServiceProvider serviceProvider)
    {
        _userProfileService = serviceProvider.GetRequiredService<UserProfileService>();
    }

    public async Task LoadProfile()
    {
        var user = await _userProfileService.GetAsync();
        if(user != null){
            Localize.SetCulture(new CultureInfo(user.DisplayCulture, false));
            await Shell.Current.GoToAsync("//dashboard");
        }
    }
}