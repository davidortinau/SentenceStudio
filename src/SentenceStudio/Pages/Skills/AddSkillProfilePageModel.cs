using SentenceStudio.Data;

namespace SentenceStudio.Pages.Skills;

public partial class AddSkillProfilePageModel : ObservableObject
{
    public LocalizationManager Localize => LocalizationManager.Instance;

    [ObservableProperty]
    SkillProfile _profile;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _description;

    public AddSkillProfilePageModel(IServiceProvider service)
    {
        Profile = new SkillProfile();
        _skillsRepository = service.GetRequiredService<SkillProfileRepository>();
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
    
}
