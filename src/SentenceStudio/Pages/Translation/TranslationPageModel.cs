using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Media;
using Plugin.Maui.Audio;

namespace SentenceStudio.Pages.Translation;

public partial class TranslationPageModel : BaseViewModel, IQueryAttributable
{
    private TeacherService _teacherService;
    private VocabularyService _vocabularyService;
    private AiService _aiService;

    readonly ISpeechToText _speechToText;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartListeningCommand))]
	bool canListenExecute = true;

	[ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartListeningCommand))]
	bool canStartListenExecute = true;

	[ObservableProperty, NotifyCanExecuteChangedFor(nameof(StopListeningCommand))]
	bool canStopListenExecute = false;

    [ObservableProperty]
    private bool _isBuffering;

    [ObservableProperty]
    private string _userMode = InputMode.Text.ToString();

    public int ListID { get; set; }
    public int SkillProfileID { get; set; }
    public string PlayMode { get; set; }
    public int Level { get; set; }

    [ObservableProperty]
    private List<Challenge> _sentences = new List<Challenge>();

    [ObservableProperty]
    private string _currentSentence;

    [ObservableProperty]
    private string _userTranslation;

    [ObservableProperty]
    private GradeResponse _gradeResponse;

    [ObservableProperty]
    private List<VocabularyWord> _vocabulary;

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
    private string _feedback;

    public List<VocabularyWord> Words
    {
        get => _teacherService.Words;
    }

    public TranslationPageModel(IServiceProvider service, ISpeechToText speechToText)
    {
        _teacherService = service.GetRequiredService<TeacherService>();
        _vocabularyService = service.GetRequiredService<VocabularyService>();
        _aiService = service.GetRequiredService<AiService>();
        // TaskMonitor.Create(LoadSentences);

        _speechToText = speechToText;
        // _speechToText.StateChanged += HandleSpeechToTextStateChanged;
		// _speechToText.RecognitionResultCompleted += HandleRecognitionResultCompleted;
    }

    private async Task LoadSentences()
    {
        await GetSentences(true);
    }

    public async Task GetSentences(bool start = true, int count = 2)
    {
        await Task.Delay(100);
        if(start)
            IsBusy = true;
        else
            IsBuffering = true;
        
        var sentences = await _teacherService.GetChallenges(ListID, count, SkillProfileID);
        await Task.Delay(100);
        foreach(var s in sentences)
        {
            Sentences.Add(s);
            await _teacherService.SaveChallenges(s);
        }
        
        IsBusy = false;
        
        // WeakReferenceMessenger.Default.Unregister<ChatCompletionMessage>(this);
        if(start)
            SetCurrentSentence();

        if(Sentences.Count < 10)
            await GetSentences(false, 8);
        else
            IsBuffering = false;
        
    }

    private int _currentSentenceIndex = 0;

    void SetCurrentSentence()
    {
        if (Sentences != null && Sentences.Count > 0)
        {
            UserMode = InputMode.Text.ToString();
            GradeResponse = null;
            HasFeedback = false;
            Feedback = string.Empty;
            CurrentSentence = Sentences[_currentSentenceIndex].RecommendedTranslation;
            Vocabulary = Sentences[_currentSentenceIndex].Vocabulary;

            var random = new Random();
            VocabBlocks = Vocabulary.Select(v => v.TargetLanguageTerm)
                                    .Where(t => !string.IsNullOrEmpty(t))
                                    .OrderBy(_ => random.Next())
                                    .ToList();

            UserInput = string.Empty;
            RecommendedTranslation = Sentences[_currentSentenceIndex].SentenceText;
            // Sentences.RemoveAt(0);

            Progress = $"{_currentSentenceIndex + 1} / {Sentences.Count}";
            IsBusy = false;
        }
    }

    [RelayCommand]
    async Task GradeMe()
    {
        Feedback = string.Empty; // wipe it in case of a repeat
        IsBusy = true;
        var prompt = string.Empty;     
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeTranslation.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(reader.ReadToEnd());
            prompt = await template.RenderAsync(new { original_sentence = CurrentSentence, recommended_translation = RecommendedTranslation, user_input = UserInput});

            Debug.WriteLine(prompt);
        }

        WeakReferenceMessenger.Default.Register<ChatCompletionMessage>(this, (r, m) =>
        {
            HasFeedback = true;
            IsBusy = false;
            Feedback += m.Value;
            // I could parse the feedback quickly to capture Accuracy and Fluency scores and display them in the UI
        });

        await _aiService.SendPrompt(prompt, false, true);
        // HasFeedback = true;
        // IsBusy = false;

        WeakReferenceMessenger.Default.Unregister<ChatCompletionMessage>(this);

        // Feedback += await _aiService.SendPrompt(prompt, false, true);
        // GradeResponse = await _teacherService.GradeTranslation(UserInput, CurrentSentence, RecommendedTranslation);
        // Feedback = FormatGradeResponse(GradeResponse);
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
        _currentSentenceIndex++;
        SetCurrentSentence();
    }

    [RelayCommand]
    void PreviousSentence()
    {
        _currentSentenceIndex--;
        SetCurrentSentence();
    }

    [RelayCommand]
    void UseVocab(string word)
    {
        UserInput += word;
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanListenExecute))]
    async Task StartListening(CancellationToken cancellationToken)
    {
        CanListenExecute = false;
		CanStartListenExecute = false;
		CanStopListenExecute = true;

		var isGranted = await _speechToText.RequestPermissions(cancellationToken);
		if (!isGranted)
		{
			await Toast.Make("Permission not granted").Show(cancellationToken);
			return;
		}

		const string beginSpeakingPrompt = "Begin speaking...";

		UserInput = beginSpeakingPrompt;

		await _speechToText.StartListenAsync(new SpeechToTextOptions{ Culture = CultureInfo.GetCultureInfo("ko-KR") }, cancellationToken);

		_speechToText.RecognitionResultUpdated += HandleRecognitionResultUpdated;

		if (UserInput is beginSpeakingPrompt)
		{
			UserInput = string.Empty;
		}
    }

    [RelayCommand(CanExecute = nameof(CanStopListenExecute))]
    async Task StopListening(CancellationToken cancellationToken)
	{
		CanListenExecute = true;
		CanStartListenExecute = true;
		CanStopListenExecute = false;

		_speechToText.RecognitionResultUpdated -= HandleRecognitionResultUpdated;

		_speechToText.StopListenAsync(cancellationToken);
	}

    void HandleRecognitionResultUpdated(object? sender, SpeechToTextRecognitionResultUpdatedEventArgs e)
	{
		UserInput += e.RecognitionResult;
	}

	void HandleRecognitionResultCompleted(object? sender, SpeechToTextRecognitionResultCompletedEventArgs e)
	{
		UserInput = e.RecognitionResult.Text;
	}

	async void HandleSpeechToTextStateChanged(object? sender, SpeechToTextStateChangedEventArgs e)
	{
		await Toast.Make($"State Changed: {e.State}").Show(CancellationToken.None);
	}

    [RelayCommand]
    async Task PlayAudio()
    {
        var myStream = await _aiService.TextToSpeechAsync(RecommendedTranslation, "Nova");
            
        var audioPlayer = AudioManager.Current.CreatePlayer(myStream);
        audioPlayer.Play();
        
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        ListID = (int)query["listID"];
        SkillProfileID = query.ContainsKey("skillProfileID") ? (int)query["skillProfileID"] : 1;
        // PlayMode = (string)query["playMode"];
        // Level = (int)query["level"];

        TaskMonitor.Create(LoadSentences);
    }

}
