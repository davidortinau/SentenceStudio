using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Vocabulary;

[QueryProperty(nameof(ShouldRefresh), "refresh")]
public partial class ListVocabularyPageModel : ObservableObject
{
    public LocalizationManager Localize => LocalizationManager.Instance;
    
    private VocabularyService _vocabService;

    [ObservableProperty]
    private List<VocabularyList> _vocabLists;


    private bool _shouldRefresh;
    public bool ShouldRefresh
    {
        get
        {
            return _shouldRefresh;
        }
        set
        {
            _shouldRefresh = value; 
            if(_shouldRefresh)
               TaskMonitor.Create(LoadVocabLists);
        }
    }
    
    public ListVocabularyPageModel(IServiceProvider service)
    {
        _vocabService = service.GetRequiredService<VocabularyService>();
        TaskMonitor.Create(LoadVocabLists);
    }

    // public void Init()
    // {
    //     TaskMonitor.Create(LoadVocabLists);
    // }

    private async Task LoadVocabLists()
    {
        await Task.Delay(100);
        VocabLists = await _vocabService.GetAllListsWithTermsAsync();
    }

    [RelayCommand]
    async Task AddVocabulary()
    {
        await Shell.Current.GoToAsync("addVocabulary");
    }

    [RelayCommand]
    async Task ViewList(int listID)
    {
        await Shell.Current.GoToAsync($"editVocabulary?id={listID}");
    }

    [RelayCommand]
    async Task Write(int listID)
    {
        try{
            await Shell.Current.GoToAsync($"writingLesson?listID={listID}&playMode=Blocks&level=1");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }

    [RelayCommand]
    async Task Play(int listID)
    {
        try{
            await Shell.Current.GoToAsync($"lesson?listID={listID}&playMode=Blocks&level=1");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }
}
