using SentenceStudio.Services;
using Sharpnado.Tasks;
using SentenceStudio.Models;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;

namespace SentenceStudio.Pages.Lesson;

public partial class LessonStartPageModel : BaseViewModel
{
    private TeacherService _teacherService;

    private VocabularyService _vocabService;

    [ObservableProperty]
    private int _level;

    [ObservableProperty]
    private PlayMode _selectedPlayMode;

    [ObservableProperty]
    private string _selectedLesson;

    [ObservableProperty]
    private VocabularyList _vocabList;

    [ObservableProperty]
    private List<VocabularyList> _vocabLists;

    [ObservableProperty]
    private List<PlayMode> _playModes;
    
    public LessonStartPageModel(IServiceProvider service)
    {
        _teacherService = service.GetRequiredService<TeacherService>();
        _vocabService = service.GetRequiredService<VocabularyService>();

        PlayModes = new List<PlayMode>
        {
            PlayMode.Blocks,
            PlayMode.Keyboard,
            PlayMode.Mic,
            PlayMode.Photo
        };

        TaskMonitor.Create(LoadVocabLists);
    }

    private async Task LoadVocabLists()
    {
        VocabLists = await _vocabService.GetAllListsWithTermsAsync();
        
    }

    [RelayCommand]
    async Task StartLesson()
    {
        try
        {
            string route = string.Empty;
            switch (SelectedLesson)
            {
                case "Warmup":
                    route = "warmup";
                    break;
                case "Write":
                    route = "writingLesson";
                    break;
                case "Translate":
                default:
                    route = "translation";
                    break;
            }
            await Shell.Current.GoToAsync($"{route}?listID={VocabList.ID}&playMode={SelectedPlayMode}&level={Level}");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }
    
}
