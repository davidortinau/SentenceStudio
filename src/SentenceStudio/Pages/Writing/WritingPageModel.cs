using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core.Platform;
using SentenceStudio.Data;

namespace SentenceStudio.Pages.Writing;

public partial class WritingPageModel : BaseViewModel, IQueryAttributable
{
    // public void ApplyQueryAttributes(IDictionary<string, object> query)
    // {
    //     ListID = int.Parse(query["listID"].ToString());
    //     // OnPropertyChanged(nameof(ListID));
    //     TaskMonitor.Create(GetVocab);
    // }
    
    public LocalizationManager Localize => LocalizationManager.Instance;

    private TeacherService _teacherService;
    private VocabularyService _vocabularyService;
    private UserActivityRepository _userActivityRepository;
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

    public List<VocabularyWord> Words
    {
        get => _teacherService.Words;
    }  

    public WritingPageModel(IServiceProvider service)
    {
        _teacherService = service.GetRequiredService<TeacherService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        // TaskMonitor.Create(GetVocab);
        
        _popupService = service.GetRequiredService<IPopupService>();
        _userActivityRepository = service.GetRequiredService<UserActivityRepository>();
        
    }
    
    private HashSet<int> _usedWordIds = new HashSet<int>();

    public async Task GetVocab()
    {
        // await Task.Delay(100);
        IsBusy = true; 
        VocabularyList vocab = await _vocabularyService.GetListAsync(ListID);
        if(vocab is not null)
        {
            var random = new Random();       
            VocabBlocks = vocab.Words
                .Where(w => !_usedWordIds.Contains(w.ID)) // Filter out already used words
                .OrderBy(t => random.Next())
                .Take(4)
                .ToList();

            // Add the newly selected word IDs to the usedWordIds set
            _usedWordIds.UnionWith(VocabBlocks.Select(w => w.ID));
        }
        IsBusy = false;
    }

    [ObservableProperty]
    private ObservableCollection<Sentence> _sentences = new ObservableCollection<Sentence>();

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GradeMe()
    {
        if(ShowMore && string.IsNullOrWhiteSpace(UserMeaning))
            return;

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
        await _userActivityRepository.SaveAsync(new UserActivity{
            Activity = Models.Activity.Writer.ToString(),
            Input = $"{s.Answer} {s.Problem}",
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
    async Task TranslateInput(Button btn)
    {
        if(string.IsNullOrWhiteSpace(UserInput))
            return;

        var translation = await _teacherService.Translate(UserInput);
        
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        var snackbarOptions = new SnackbarOptions
        {
            CornerRadius = new CornerRadius(8)
        };

        TimeSpan duration = TimeSpan.FromSeconds(3);

        var snackbar = Snackbar.Make(translation, duration: duration, visualOptions: snackbarOptions, anchor: btn);
        await snackbar.Show(cancellationTokenSource.Token);
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
        bool isSuccessful = await view.HideKeyboardAsync(CancellationToken.None);
    }

    [ObservableProperty]
    private bool _showMore;

    [RelayCommand]
    async Task ToggleMore()
    {
        ShowMore = !ShowMore;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        ListID = (int)query["listID"];
        // SkillProfileID = query.ContainsKey("skillProfileID") ? (int)query["skillProfileID"] : 1;
        // PlayMode = (string)query["playMode"];
        // Level = (int)query["level"];

        TaskMonitor.Create(GetVocab);
    }
}
