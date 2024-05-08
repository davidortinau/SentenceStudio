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
using CommunityToolkit.Maui.Core.Platform;


namespace SentenceStudio.Pages.Lesson;

[QueryProperty(nameof(ListID), "listID")]
[QueryProperty(nameof(PlayMode), "playMode")]
[QueryProperty(nameof(Level), "level")]
public partial class WritingPageModel : BaseViewModel
{
    public LocalizationManager Localize => LocalizationManager.Instance;

    private TeacherService _teacherService;
    private VocabularyService _vocabularyService;

    private UserActivityService _userActivityService;

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
    private List<VocabularyWord> _vocabulary;

    [ObservableProperty]
    private List<VocabularyWord> _vocabBlocks;

    [ObservableProperty]
    private string _userInput;

    [ObservableProperty]
    private string _userMeaning;

    [ObservableProperty]
    private string _progress;

    [ObservableProperty]
    private string _recommendedTranslation;

    [ObservableProperty]
    private bool _hasFeedback;

    [ObservableProperty]
    private bool _isBusy;

    public List<VocabularyWord> Words
    {
        get => _teacherService.Words;
    }

    

    public WritingPageModel(IServiceProvider service)
    {
        _teacherService = service.GetRequiredService<TeacherService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        _popupService = service.GetRequiredService<IPopupService>();
        _userActivityService = service.GetRequiredService<UserActivityService>();
        TaskMonitor.Create(GetVocab);
    }
    public async Task GetVocab()
    {
        await Task.Delay(100);
        IsBusy = true; 
        VocabularyList vocab = await _vocabularyService.GetListAsync(ListID);

        var random = new Random();       
        VocabBlocks = vocab.Words.OrderBy(t => random.Next()).Take(4).ToList();
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
            Problem = UserMeaning
        };
        Sentences.Add(s);
        UserInput = UserMeaning = string.Empty;
        var grade = await _teacherService.GradeSentence(s.Answer, s.Problem);
        if(grade is null){
            _ = ShowError();
        }
        s.Accuracy = grade.Accuracy;
        s.Fluency = grade.Fluency;
        s.FluencyExplanation = grade.FluencyExplanation;
        s.AccuracyExplanation = grade.AccuracyExplanation;
        s.RecommendedSentence = grade.GrammarNotes.RecommendedTranslation;
        s.GrammarNotes = grade.GrammarNotes.Explanation;

        // here is where we save the sentence to the database
        await _userActivityService.SaveAsync(new UserActivity{
            Activity = Models.Activity.Writer.ToString(),
            Input = s.Problem,
            Accuracy = s.Accuracy,
            Fluency = s.Fluency,
            CreatedAt = DateTime.Now
        });
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

    [RelayCommand]
    void RefreshVocab()
    {
        TaskMonitor.Create(GetVocab);
    }

    [RelayCommand]
    async Task HideKeyboard(ITextInput view, CancellationToken token)
    {
        bool isSuccessful = await view.HideKeyboardAsync(token);
    }
}
