using CommunityToolkit.Mvvm.Input;
using LukeMauiFilePicker;
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

    [ObservableProperty]
    string _displayLanguage;

    
    private UserProfileService _userProfileService;
    private VocabularyService _vocabularyService;
    
    public OnboardingPageModel(IServiceProvider service)
    {
        _userProfileService = service.GetRequiredService<UserProfileService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        
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

        await _userProfileService.SaveAsync(profile);
        
        await AppShell.DisplayToastAsync(Localize["Saved"].ToString());

        var lists = await _vocabularyService.GetListsAsync();
        if(lists.Count == 0)
        {
            var response = await Shell.Current.DisplayAlert(
                Localize["VocabularyList"].ToString(), 
                Localize["CreateStarterVocabPrompt"].ToString(), 
                Localize["Yes"].ToString(), 
                Localize["No, I'll do it myself"].ToString());
            if(response){
                IsBusy = true;
                await _vocabularyService.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);
                IsBusy = false;
            }

        }

        await Shell.Current.GoToAsync("//dashboard");
    }

    private int _screens = 4;

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

    
}
