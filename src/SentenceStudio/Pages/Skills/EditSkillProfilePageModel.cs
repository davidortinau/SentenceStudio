using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LukeMauiFilePicker;
using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Skills;

[QueryProperty(nameof(SkillProfileID), "profileID")]
public partial class EditSkillProfilePageModel : ObservableObject
{
    public LocalizationManager Localize => LocalizationManager.Instance;

    [ObservableProperty]
    SkillProfile _profile;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _description;

    [ObservableProperty] private int _skillProfileID;

    partial void OnSkillProfileIDChanged(int value)
    {
        if (value > 0)
        {
            TaskMonitor.Create(LoadSkillProfile);
        }
    }
    
    public EditSkillProfilePageModel(IServiceProvider service)
    {
        _skillsRepository = service.GetRequiredService<SkillProfileRepository>();
    }

    private async Task LoadSkillProfile()
    {
        Profile = await _skillsRepository.GetSkillProfileAsync(SkillProfileID);
        Title = Profile.Title;
        Description = Profile.Description;
    }

    private SkillProfileRepository _skillsRepository;

    [RelayCommand]
    async Task Save()
    {
        Profile.Title = Title;
        Profile.Description = Description;

        var result = await _skillsRepository.SaveAsync(Profile);
        if(result > 0)
            await AppShell.DisplayToastAsync(Localize["Saved"].ToString());
    }

    
    [RelayCommand]
    async Task Delete()
    {
        var result = await _skillsRepository.DeleteAsync(Profile);
        if(result > 0)
            await AppShell.DisplayToastAsync(Localize["Deleted"].ToString());
    }

    
}
