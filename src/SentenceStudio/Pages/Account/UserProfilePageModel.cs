using CommunityToolkit.Mvvm.Input;
using LukeMauiFilePicker;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Account;

public partial class UserProfilePageModel : ObservableObject
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
    
    public UserProfilePageModel(IServiceProvider service)
    {
        _userProfileRepository = service.GetRequiredService<UserProfileRepository>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        TaskMonitor.Create(LoadProfile);
    }

    private async Task LoadProfile()
    {
        var profile = await _userProfileRepository.GetAsync();
        Name = profile.Name;
        Email = profile.Email;
        NativeLanguage = profile.NativeLanguage;
        TargetLanguage = profile.TargetLanguage;
        DisplayLanguage = profile.DisplayLanguage;
    }

    private UserProfileRepository _userProfileRepository;
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

        await _userProfileRepository.SaveAsync(profile);
        
        await AppShell.DisplayToastAsync(Localize["Saved"].ToString());

        var lists = await _vocabularyService.GetListsAsync();
        if(lists.Count == 0)
        {
            var response = await Shell.Current.DisplayAlert("Vocabulary", "Would you like me to create a starter vocabulary list for you?", "Yes", "No, I'll do it myself");
            if(response)
                await _vocabularyService.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);
        }
    }

    [RelayCommand]
    async Task Reset()
    {
        var response = await Shell.Current.DisplayAlert("Reset", "Are you sure you want to reset your profile?", "Yes", "No");
        if(response)
        {
            await _userProfileRepository.DeleteAsync();
            await LoadProfile();
        }
    }

    
}
