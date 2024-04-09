using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using SentenceStudio.Models;
using SentenceStudio.Pages.Controls;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Lesson;

[QueryProperty(nameof(ListID), "listID")]
[QueryProperty(nameof(PlayMode), "playMode")]
[QueryProperty(nameof(Level), "level")]
public partial class WritingPageModel : ObservableObject
{
    public LocalizationManager Localize => LocalizationManager.Instance;

    private TeacherService _teacherService;
    private VocabularyService _vocabularyService;

    private IPopupService _popupService;

    public int ListID { get; set; }
    public string PlayMode { get; set; }
    public int Level { get; set; }

    [ObservableProperty]
    private string _currentSentence;

    [ObservableProperty]
    private string _userTranslation;
    
    private string _terms;

    [ObservableProperty]
    private GradeResponse _gradeResponse;

    [ObservableProperty]
    private List<VocabWord> _vocabulary;

    [ObservableProperty]
    private List<Term> _vocabBlocks;

    [ObservableProperty]
    private string _userInput;

    [ObservableProperty]
    private string _progress;

    [ObservableProperty]
    private string _recommendedTranslation;

    [ObservableProperty]
    private bool _hasFeedback;

    [ObservableProperty]
    private bool _isBusy;

    public List<Term> Terms
    {
        get => _teacherService.Terms;
    }

    

    public WritingPageModel(IServiceProvider service)
    {
        _teacherService = service.GetRequiredService<TeacherService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        _popupService = service.GetRequiredService<IPopupService>();
        TaskMonitor.Create(GetVocab);
    }
    public async Task GetVocab()
    {
        await Task.Delay(100);
        IsBusy = true; 
        VocabularyList vocab = await _vocabularyService.GetListAsync(ListID);

            // if (vocab is null || vocab.Terms is null)
            //     return null;

        var random = new Random();
        
        VocabBlocks = vocab.Terms.OrderBy(t => random.Next()).Take(4).ToList();
        //string t = string.Join(",", _terms.Select(t => t.TargetLanguageTerm));
        IsBusy = false;
            
    }

    [ObservableProperty]
    private ObservableCollection<Sentence> _sentences;

    [RelayCommand]
    async Task GradeMe()
    {
        if(Sentences is null)
            Sentences = new ObservableCollection<Sentence>();

        // IsBusy = true;
        var s = new Sentence{
            Answer = UserInput,
        };
        Sentences.Add(s);
        UserInput = string.Empty;
        var grade = await _teacherService.GradeSentence(s.Answer);
        
        s.Accuracy = grade.Accuracy;
        s.Fluency = grade.Fluency;
        s.FluencyExplanation = grade.FluencyExplanation;
        s.AccuracyExplanation = grade.AccuracyExplanation;
        //s.GrammarNotes = grade.;

        // IsBusy = false;
    }

    [RelayCommand]
    async Task Next()
    {
        // SetCurrentSentence();
    }

    [RelayCommand]
    async Task Previous()
    {
        //SetCurrentSentence();
    }

    [RelayCommand]
    void UseVocab(string word)
    {
        UserInput += word;
    }

    static Page MainPage => Shell.Current;

    [RelayCommand]
    async Task ShowExplanation(string explanation)
    {
        // var popup = new ExplanationPopup{
        //     BindingContext = new { Explanation = explanation }
        // };
        
        // MainPage.ShowPopup(popup);
        // await _popupService.ShowPopupAsync(new Popup());
        await App.Current.MainPage.DisplayAlert("Explanation", explanation, "OK");
    }


}
