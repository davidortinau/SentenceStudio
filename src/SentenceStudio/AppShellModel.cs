using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Services;
using Microsoft.Extensions.Logging;

namespace SentenceStudio;

public partial class AppShellModel : ObservableObject
{
    private UserProfileRepository _userProfileRepository;
    private readonly ILogger<AppShellModel> _logger;

    public LocalizationManager Localize => LocalizationManager.Instance;

    [RelayCommand]
    async Task ChangeUILanguage()
    {
        _logger.LogDebug("ChangeUILanguage Current Culture: {CultureName}", CultureInfo.CurrentUICulture.Name);
        var culture = (CultureInfo.CurrentUICulture.Name == "ko-KR") ? "en-US" : "ko-KR";
        Localize.SetCulture(new CultureInfo( culture, false ));
        await _userProfileRepository.SaveDisplayCultureAsync(culture);

        TitleDashboard = "YOU GOT UPDATED!";
    }

    public AppShellModel(IServiceProvider serviceProvider, ILogger<AppShellModel> logger)
    {
        _userProfileRepository = serviceProvider.GetRequiredService<UserProfileRepository>();
        _logger = logger;
    }

    public async Task LoadProfile()
    {
        // var user = await _userProfileRepository.GetAsync();
        // if(user != null){
        //     Localize.SetCulture(new CultureInfo(user.DisplayCulture, false));
        //     await Shell.Current.GoToAsync("//dashboard");
        // }
    }

    [ObservableProperty]
    private string _titleDashboard = "Dashboard";

    [RelayCommand]
    async Task NavigateTo(string route)
    {
        // await Shell.Current.GoToAsync($"//{route}");
        // Shell.Current.FlyoutIsPresented = false;
    }
}