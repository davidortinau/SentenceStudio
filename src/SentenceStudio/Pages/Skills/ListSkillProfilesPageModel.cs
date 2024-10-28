using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LukeMauiFilePicker;
using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Skills;

public partial class ListSkillProfilesPageModel : BaseViewModel
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
    
    public ListSkillProfilesPageModel(IServiceProvider service)
    {
        _skillsRepository = service.GetRequiredService<SkillProfileRepository>();
        // TaskMonitor.Create(LoadProfiles);
    }

    private async Task LoadProfiles()
    {
        var profiles = await _skillsRepository.ListAsync();
        Profiles = new ObservableCollection<SkillProfile>(profiles);
    }

    public override async Task Refresh()
    {
        await LoadProfiles();
    }   

    private SkillProfileRepository _skillsRepository;
    
    [RelayCommand]
    async Task EditProfile(SkillProfile profile)
    {
        await Shell.Current.GoToAsync($"editSkillProfile?profileID={profile.ID}");
    }

    [RelayCommand]
    async Task AddProfile()
    {
        // Profiles.Add(new SkillProfile());
        await Shell.Current.GoToAsync($"addSkillProfile");

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
