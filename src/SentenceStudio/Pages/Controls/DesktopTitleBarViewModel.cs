using SentenceStudio.Data;

namespace SentenceStudio.Pages.Controls;

public partial class DesktopTitleBarViewModel : ObservableObject
{
    [ObservableProperty]
    private List<string> _languages;

    [ObservableProperty]
    private List<SkillProfile> _profiles;

    [ObservableProperty]
    private string _selectedLanguage;

    [ObservableProperty]
    private string _selectedProfileTitle;

    private readonly SkillProfileRepository _skillsRepository;

    public DesktopTitleBarViewModel(IServiceProvider service)
    {
        _skillsRepository = service.GetRequiredService<SkillProfileRepository>();
        TaskMonitor.Create(Load);
    }

    private async Task Load()
    {
        Languages = new List<string> { "English", "한국어" };
        SelectedLanguage = "한국어";
        Profiles = await _skillsRepository.ListAsync();
        SelectedProfileTitle = Profiles[0].Title;
    }


}