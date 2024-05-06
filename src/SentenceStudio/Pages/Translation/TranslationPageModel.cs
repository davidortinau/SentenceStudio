using System.Diagnostics;
using System.Text;
using System.Web;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Translation;

[QueryProperty(nameof(ListID), "listID")]
[QueryProperty(nameof(PlayMode), "playMode")]
[QueryProperty(nameof(Level), "level")]
public partial class TranslationPageModel : BaseViewModel
{
    

    private TeacherService _teacherService;
    private VocabularyService _vocabularyService;

    public int ListID { get; set; }
    public string PlayMode { get; set; }
    public int Level { get; set; }

    [ObservableProperty]
    private List<Challenge> _sentences;

    [ObservableProperty]
    private string _currentSentence;

    [ObservableProperty]
    private string _userTranslation;

    [ObservableProperty]
    private GradeResponse _gradeResponse;

    [ObservableProperty]
    private List<VocabWord> _vocabulary;

    [ObservableProperty]
    private List<string> _vocabBlocks;

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

    [ObservableProperty]
    private string _feedback;

    public List<Term> Terms
    {
        get => _teacherService.Terms;
    }

    void SetCurrentSentence()
    {
        if (Sentences != null && Sentences.Count > 0)
        {
            GradeResponse = null;
            HasFeedback = false;
            Feedback = string.Empty;
            CurrentSentence = Sentences[0].SentenceText;
            Vocabulary = Sentences[0].Vocabulary;

            var random = new Random();
            VocabBlocks = Vocabulary.Select(v => v.TargetLanguageTerm)
                                    .Where(t => !string.IsNullOrEmpty(t))
                                    .OrderBy(_ => random.Next())
                                    .ToList();

            UserInput = string.Empty;
            RecommendedTranslation = Sentences[0].RecommendedTranslation;
            Sentences.RemoveAt(0);

            Progress = $"{10 - Sentences.Count} / 10";
            IsBusy = false;
        }
    }

    public TranslationPageModel(IServiceProvider service)
    {
        _teacherService = service.GetRequiredService<TeacherService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        TaskMonitor.Create(GetSentences);
    }
    public async Task GetSentences()
    {
        await Task.Delay(100);
        IsBusy = true;
        Sentences = await _teacherService.GetChallenges(ListID);
        SetCurrentSentence();
    }

    [RelayCommand]
    async Task GradeMe()
    {
        IsBusy = true;
        GradeResponse = await _teacherService.GradeTranslation(UserInput, CurrentSentence, RecommendedTranslation);
        Feedback = FormatGradeResponse(GradeResponse);
        HasFeedback = true;
        IsBusy = false;
    }

    private string FormatGradeResponse(GradeResponse gradeResponse)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendFormat("<p>{0}</p>", HttpUtility.HtmlEncode(CurrentSentence));
        sb.AppendFormat("<p>Accuracy: {0}</p>", HttpUtility.HtmlEncode(GradeResponse.Accuracy));
        sb.AppendFormat("<p>Explanation: {0}</p>", HttpUtility.HtmlEncode(GradeResponse.AccuracyExplanation));
        sb.AppendFormat("<p>Fluency: {0}</p>", HttpUtility.HtmlEncode(GradeResponse.Fluency));
        sb.AppendFormat("<p>Explanation: {0}</p>", HttpUtility.HtmlEncode(GradeResponse.FluencyExplanation));
        sb.AppendFormat("<p>Recommended: {0}</p>", HttpUtility.HtmlEncode(GradeResponse.GrammarNotes.RecommendedTranslation));
        sb.AppendFormat("<p>Notes: {0}</p>", HttpUtility.HtmlEncode(GradeResponse.GrammarNotes.Explanation));

        return sb.ToString();
    }

    [RelayCommand]
    void NextSentence()
    {
        SetCurrentSentence();
    }

    [RelayCommand]
    void UseVocab(string word)
    {
        UserInput += word;
    }


}
