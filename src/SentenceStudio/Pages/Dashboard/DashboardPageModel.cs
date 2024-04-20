using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Dashboard;

[QueryProperty(nameof(ShouldRefresh), "refresh")]
public partial class DashboardPageModel : ObservableObject
{
    public LocalizationManager Localize => LocalizationManager.Instance;

    [ObservableProperty]
    private List<VocabularyList> _vocabLists;

    public DashboardPageModel(IServiceProvider service)
    {
        _vocabService = service.GetRequiredService<VocabularyService>();
        TaskMonitor.Create(GetLists);
    }

    private async Task GetLists()
    {
        VocabLists = await _vocabService.GetListsAsync();
    }

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
        }
    }

    

    public VocabularyService _vocabService { get; }

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
    async Task Play(int listID)
    {
        // if(listID == 0)
        //     listID = VocabLists.First().ID;

        try{
            await Shell.Current.GoToAsync($"lesson?listID={listID}&playMode=Blocks&level=1");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }

    [RelayCommand]
    async Task DefaultTranslate()
    {
        if(VocabLists.Count == 0)
            VocabLists = await _vocabService.GetListsAsync();
        
        await Play(VocabLists.First().ID);
    }

    [RelayCommand]
    async Task DefaultWrite()
    {
        if(VocabLists.Count == 0)
            VocabLists = await _vocabService.GetListsAsync();
        await Write(VocabLists.First().ID);
        
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
    async Task DescribeAScene()
    {
        try{
            await Shell.Current.GoToAsync($"describeScene");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }

    [RelayCommand]
    async Task Warmup()
    {
        try{
            await Shell.Current.GoToAsync($"warmup");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }
}
