using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using SentenceStudio.Models;
using SentenceStudio.Pages.Controls;
using SentenceStudio.Services;
using Sharpnado.Tasks;
using CommunityToolkit.Maui.Alerts;


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

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GradeMe()
    {
        if(Sentences is null)
            Sentences = new ObservableCollection<Sentence>();

        var s = new Sentence{
            Answer = UserInput,
        };
        Sentences.Add(s);
        UserInput = string.Empty;
        var grade = await _teacherService.GradeSentence(s.Answer);
        if(grade is null){
            _ = ShowError();
        }
        s.Accuracy = grade.Accuracy;
        s.Fluency = grade.Fluency;
        s.FluencyExplanation = grade.FluencyExplanation;
        s.AccuracyExplanation = grade.AccuracyExplanation;
        s.RecommendedSentence = grade.GrammarNotes.RecommendedTranslation;
        s.GrammarNotes = grade.GrammarNotes.Explanation;
    }

    private async Task ShowError()
    {
        ToastDuration duration = ToastDuration.Long;
        double fontSize = 14;
        var toast = Toast.Make("Something went wrong. Check the server.", duration, fontSize);
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        await toast.Show(cancellationTokenSource.Token);
    }

    [RelayCommand]
    void UseVocab(string word)
    {
        UserInput += word;
    }

    static Page MainPage => Shell.Current;

    [RelayCommand]
    async Task ShowExplanation(Sentence s)
    {
        string explanation = $"Original: {s.Answer}" + Environment.NewLine + Environment.NewLine;
        explanation += $"Recommended: {s.RecommendedSentence}" + Environment.NewLine + Environment.NewLine;
        explanation += $"Accuracy: {s.AccuracyExplanation}" + Environment.NewLine + Environment.NewLine;
        explanation += $"Fluency: {s.FluencyExplanation}" + Environment.NewLine + Environment.NewLine;
        explanation += $"Additional Notes: {s.GrammarNotes}" + Environment.NewLine + Environment.NewLine;

        try{
            await _popupService.ShowPopupAsync<ExplanationViewModel>(onPresenting: viewModel => {
                viewModel.Text = explanation;
                });
        }catch(Exception e){
            Debug.WriteLine(e.Message);
        }
    }

    [RelayCommand]
    async Task TranslateInput()
    {
        if(string.IsNullOrWhiteSpace(UserInput))
            return;

        
        var translation = await _teacherService.Translate(UserInput);
        ToastDuration duration = ToastDuration.Long;
        double fontSize = 14;
        var toast = Toast.Make(translation, duration, fontSize);
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        await toast.Show(cancellationTokenSource.Token);
    }

    [RelayCommand]
    void ClearInput()
    {
        UserInput = string.Empty;
    }
}
