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
            //if(_shouldRefresh)
            //    TaskMonitor.Create(LoadVocabLists);
        }
    }

    private async Task LoadVocabLists()
    {
        VocabLists = await _vocabService.GetAllListsWithTermsAsync();
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
        try{
        await Shell.Current.GoToAsync($"lesson?listID={listID}&playMode=Blocks&level=1");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
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
    async Task Warmup()
    {
        try{
            await Shell.Current.GoToAsync($"warmup");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }

    public void Init()
    {
        TaskMonitor.Create(LoadVocabLists);
    }
}
