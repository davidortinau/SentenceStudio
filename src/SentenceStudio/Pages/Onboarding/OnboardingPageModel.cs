using CommunityToolkit.Mvvm.Input;
using LukeMauiFilePicker;
using Microsoft.Extensions.Configuration;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Onboarding;

public partial class OnboardingPageModel : BaseViewModel
{
    public LocalizationManager Localize => LocalizationManager.Instance;

    [ObservableProperty] private int _currentPosition;
    [ObservableProperty] private bool _lastPositionReached;
    [ObservableProperty] string _name;
    [ObservableProperty] string _email;
    [ObservableProperty] string _nativeLanguage;
    [ObservableProperty] string _targetLanguage;
    [ObservableProperty] string _displayLanguage;

    [ObservableProperty] string _openAI_APIKey;

    [ObservableProperty] bool _needsApiKey;
    private IServiceProvider _service;
    private UserProfileRepository _userProfileRepository;
    private VocabularyService _vocabularyService;

    private readonly string _openAiApiKey;
    private int _screens = 4;

    
    
    public OnboardingPageModel(IServiceProvider service, IConfiguration configuration)
    {
        _service = service;
        _userProfileRepository = service.GetRequiredService<UserProfileRepository>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        _openAiApiKey = configuration.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;

        NeedsApiKey = string.IsNullOrEmpty(_openAiApiKey);
    }

    [RelayCommand]
    public async Task End()
    {
        var profile = new UserProfile
        {
            Name = Name,
            Email = Email,
            NativeLanguage = NativeLanguage,
            TargetLanguage = TargetLanguage,
            DisplayLanguage = DisplayLanguage
        };

        await _userProfileRepository.SaveAsync(profile);
        
        await AppShell.DisplayToastAsync(Localize["Saved"].ToString());

        //await Shell.Current.GoToAsync("//dashboard");
        Preferences.Default.Set("is_onboarded", true);
        App.Current.Windows[0].Page = new AppShell(_service.GetService<AppShellModel>());
    }

    

    [RelayCommand]
    public void Next()
    {
        if (CurrentPosition >= _screens) 
            return;
        CurrentPosition++;
    }

    partial void OnCurrentPositionChanged(int value)
    {
        if (CurrentPosition == _screens)
            LastPositionReached = true;
        else
            LastPositionReached = false;
    }

    [RelayCommand]
    async Task GoToOpenAI()
    {
        await Browser.OpenAsync("https://platform.openai.com/account/api-keys");
    }

    
}
