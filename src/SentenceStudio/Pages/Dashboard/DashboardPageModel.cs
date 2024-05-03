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
        _userService = service.GetRequiredService<UserProfileService>();
        _userActivityService = service.GetRequiredService<UserActivityService>();
        TaskMonitor.Create(GetLists);
    }

    
    private async Task GetLists()
    {
        VocabLists = await _vocabService.GetListsAsync();
        if(VocabLists.Count == 0)
        {
            //do we have a profile with languages
            var profile = await _userService.GetAsync();
            if(profile != null)
            {
                var lists = await _vocabService.GetListsAsync();
                if(lists.Count == 0)
                {
                    //create default lists
                    var response = await Shell.Current.DisplayAlert("Vocabulary", "Would you like me to create a starter vocabulary list for you?", "Yes", "No, I'll do it myself");
                    if(response){
                        await _vocabService.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);
                        VocabLists = await _vocabService.GetListsAsync();
                    }
                }
            }else{
                // prompt to create a profile first
                var response = await Shell.Current.DisplayAlert("Profile", "To get started, create a profile and tell us what language you are learning today.", "Let's do it", "Maybe later");
                if(response)
                {
                    await Shell.Current.GoToAsync("userProfile");
                }
            }
        }
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

    private UserProfileService _userService;
    private UserActivityService _userActivityService;
    

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
    async Task SyntacticAnalysis()
    {
        try{
            if(VocabLists.Count == 0)
            VocabLists = await _vocabService.GetListsAsync();

            var listID = VocabLists.First().ID;
            
            await Shell.Current.GoToAsync($"syntacticAnalysis?listID={listID}");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
        
    }

    [RelayCommand]
    async Task Write(int listID)
    {
        // await Shell.Current.DisplayAlert("HR", "Reloaded", "Okay");

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
        // await Shell.Current.DisplayAlert("HR", "Reloaded", "Okay");
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
