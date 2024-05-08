using System.Diagnostics;
using System.Text;
using System.Web;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.SyntacticAnalysis;

[QueryProperty(nameof(ListID), "listID")]
public partial class AnalysisPageModel : BaseViewModel
{
    

    private TeacherService _teacherService;
    private VocabularyService _vocabularyService;
    private SyntacticAnalysisService _syntacticAnalysisService;

    public int ListID { get; set; }
    
    [ObservableProperty]
    private List<SyntacticSentence> _sentences;

    [ObservableProperty]
    private SyntacticSentence _currentSentence;

    [ObservableProperty]
    private List<Chunk> _chunks;

    
    [ObservableProperty]
    private List<VocabularyWord> _vocabulary;

    [ObservableProperty]
    private string _progress;

    [ObservableProperty]
    private bool _isBusy;

    void SetCurrentSentence()
    {
        if (Sentences != null && Sentences.Count > 0)
        {
            // GradeResponse = null;
            // HasFeedback = false;
            // Feedback = string.Empty;
            CurrentSentence = Sentences[0];
            Chunks = CurrentSentence.Chunks;
            // Vocabulary = Sentences[0].Vocabulary;

            // var random = new Random();
            // VocabBlocks = Vocabulary.Select(v => v.TargetLanguageTerm)
            //                         .Where(t => !string.IsNullOrEmpty(t))
            //                         .OrderBy(_ => random.Next())
            //                         .ToList();

            // UserInput = string.Empty;
            // RecommendedTranslation = Sentences[0].RecommendedTranslation;
            // Sentences.RemoveAt(0);

            // Progress = $"{10 - Sentences.Count} / 10";
            IsBusy = false;
        }
    }

    public AnalysisPageModel(IServiceProvider service)
    {
        _teacherService = service.GetRequiredService<TeacherService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        _syntacticAnalysisService = service.GetRequiredService<SyntacticAnalysisService>();
        TaskMonitor.Create(GetSentences);
    }
    public async Task GetSentences()
    {
        await Task.Delay(100);
        IsBusy = true;
        Sentences = await _syntacticAnalysisService.GetSentences(ListID);
        SetCurrentSentence();
    }

    [RelayCommand]
    async Task GradeMe()
    {
        IsBusy = true;
        // GradeResponse = await _teacherService.GradeTranslation(UserInput, CurrentSentence, RecommendedTranslation);
        // Feedback = FormatGradeResponse(GradeResponse);
        // HasFeedback = true;
        IsBusy = false;
    }

    [RelayCommand]
    void NextSentence()
    {
        SetCurrentSentence();
    }


}
