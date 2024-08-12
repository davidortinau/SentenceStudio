using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LukeMauiFilePicker;
using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Account;

public partial class SkillProfilesPageModel : ObservableObject
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

    [ObservableProperty]
    ObservableCollection<SkillProfile> _profiles;
    
    public SkillProfilesPageModel(IServiceProvider service)
    {
        _skillsRepository = service.GetRequiredService<SkillProfileRepository>();
        TaskMonitor.Create(LoadProfiles);
    }

    private async Task LoadProfiles()
    {
        var profiles = await _skillsRepository.ListAsync();
        Profiles = new ObservableCollection<SkillProfile>(profiles);
    }

    private SkillProfileRepository _skillsRepository;

    [RelayCommand]
    async Task Save()
    {
        // TODO
        // var profile = new UserProfile
        // {
        //     Name = Name,
        //     Email = Email,
        //     NativeLanguage = NativeLanguage,
        //     TargetLanguage = TargetLanguage,
        //     DisplayLanguage = DisplayLanguage
        // };

        // await _userProfileService.SaveAsync(profile);
        
        // await AppShell.DisplayToastAsync(Localize["Saved"].ToString());

        // var lists = await _vocabularyService.GetListsAsync();
        // if(lists.Count == 0)
        // {
        //     var response = await Shell.Current.DisplayAlert("Vocabulary", "Would you like me to create a starter vocabulary list for you?", "Yes", "No, I'll do it myself");
        //     if(response)
        //         await _vocabularyService.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);
        // }
    }

    [RelayCommand]
    void AddProfile()
    {
        Profiles.Add(new SkillProfile());
    }

    [RelayCommand]
    async Task SaveProfiles()
    {
        foreach(var profile in Profiles)
        {
            await _skillsRepository.SaveAsync(profile);
        }

        await AppShell.DisplayToastAsync(Localize["Saved"].ToString());
    }

    
}
