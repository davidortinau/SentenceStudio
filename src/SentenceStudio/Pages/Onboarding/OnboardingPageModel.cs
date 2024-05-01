using CommunityToolkit.Mvvm.Input;
using LukeMauiFilePicker;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Onboarding;

public partial class OnboardingPageModel : ObservableObject
{
    public LocalizationManager Localize => LocalizationManager.Instance;

    [ObservableProperty]
    string _name;

    [ObservableProperty]
    string _email;

    [ObservableProperty]
    string _nativeLanguage;

    [ObservableProperty]
    string _targetLanguage;

    [ObservableProperty]
    string _displayLanguage;
    
    public OnboardingPageModel(IServiceProvider service)
    {
        _userProfileService = service.GetRequiredService<UserProfileService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        // TaskMonitor.Create(LoadProfile);
    }

    private async Task LoadProfile()
    {
        var profile = await _userProfileService.GetAsync();
        Name = profile.Name;
        Email = profile.Email;
        NativeLanguage = profile.NativeLanguage;
        TargetLanguage = profile.TargetLanguage;
        DisplayLanguage = profile.DisplayLanguage;
    }

    private UserProfileService _userProfileService;
    private VocabularyService _vocabularyService;

    [RelayCommand]
    async Task Save()
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
            var response = await Shell.Current.DisplayAlert("Vocabulary", "Would you like me to create a starter vocabulary list for you?", "Yes", "No, I'll do it myself");
            if(response)
                await _vocabularyService.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);
        }
    }

    
}
