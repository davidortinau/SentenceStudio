using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Media;
using SentenceStudio.Data;

namespace SentenceStudio.Pages.Clozure;

public partial class ClozurePageModel : BaseViewModel, IQueryAttributable
{
    private ClozureService _clozureService;
    private AiService _aiService;
    private UserActivityRepository _userActivityRepository;

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
    public string PlayMode { get; set; }
    public int Level { get; set; }

    [ObservableProperty]
    private ObservableCollection<Challenge> _sentences = new ObservableCollection<Challenge>();

    [ObservableProperty]
    private string _currentSentence;

    [ObservableProperty]
    private string _userTranslation;

    [ObservableProperty]
    private GradeResponse _gradeResponse;

    [ObservableProperty]
    private string[] _guessOptions;

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

    [ObservableProperty]
    private string _userGuess;

    partial void OnUserGuessChanged(string oldValue, string newValue)
    {
        _ = GradeAnswer(newValue);
    }

    public List<VocabularyWord> Words
    {
        get => _clozureService.Words;
    }

    public ClozurePageModel(IServiceProvider service, ISpeechToText speechToText)
    {
        _clozureService = service.GetRequiredService<ClozureService>();
        _aiService = service.GetRequiredService<AiService>();
        _userActivityRepository = service.GetRequiredService<UserActivityRepository>();
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
        if(start)
            IsBusy = true;
        else
            IsBuffering = true;
        
        var sentences = await _clozureService.GetSentences(ListID, count);
        // await Task.Delay(100);
        foreach(var s in sentences)
        {
            Sentences.Add(s);
            await _clozureService.SaveChallenges(s);
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
    private Challenge _currentChallenge;

    async void SetCurrentSentence()
    {
        if (Sentences != null && Sentences.Count > 0 && _currentSentenceIndex < Sentences.Count)
        {
            GradeResponse = null;
            HasFeedback = false;
            Feedback = string.Empty;

            _currentChallenge = Sentences[_currentSentenceIndex];
            _currentChallenge.IsCurrent = true;

            CurrentSentence = _currentChallenge.SentenceText.Replace(_currentChallenge.VocabularyWordAsUsed, "__");
            GuessOptions = _currentChallenge.VocabularyWordGuesses.Split(",").Select(x => x.Trim()).OrderBy(x => Guid.NewGuid()).ToArray();

            // Vocabulary = challenge.Vocabulary;

            // var random = new Random();
            // VocabBlocks = Vocabulary.Select(v => v.TargetLanguageTerm)
            //                         .Where(t => !string.IsNullOrEmpty(t))
            //                         .OrderBy(_ => random.Next())
            //                         .ToList();

            if(_currentChallenge.UserActivity != null)
            {
                UserInput = _currentChallenge.UserActivity.Input;
            }
            else
            {
                UserInput = string.Empty;
            }

            // UserInput = string.Empty;
            RecommendedTranslation = _currentChallenge.RecommendedTranslation;

            Progress = $"{_currentSentenceIndex + 1} / {Sentences.Count}";
            IsBusy = false;
        }else{
            await GetSentences(false, 10);
        }
    }

    [RelayCommand]
    async Task GradeMe()
    {
        await GradeAnswer(UserInput);
    }

    async Task GradeAnswer(string answer)
    {
        var ua = new UserActivity
        {
            Activity = "Clozure",
            Input = answer
        };

        if(_currentChallenge.VocabularyWordAsUsed == answer)
        {
            ua.Accuracy = 100;
            // await Toast.Make("Correct!").Show();
        }
        else
        {
            ua.Accuracy = 0;
            // await Toast.Make("Incorrect!").Show();
        }

        Sentences[_currentSentenceIndex].UserActivity = ua;

        await _userActivityRepository.SaveAsync(ua);
        
    }
    

    [RelayCommand]
    void NextSentence()
    {
        UserMode = InputMode.Text.ToString();
        Sentences[_currentSentenceIndex].IsCurrent = false;
        _currentSentenceIndex++;
        SetCurrentSentence();
    }

    [RelayCommand]
    void PreviousSentence()
    {
        UserMode = InputMode.Text.ToString();
        Sentences[_currentSentenceIndex].IsCurrent = false;
        _currentSentenceIndex--;
        SetCurrentSentence();
    }

    [RelayCommand]
    void JumpTo(Challenge challenge)
    {
        UserInput = InputMode.Text.ToString();
        Sentences[_currentSentenceIndex].IsCurrent = false;
        _currentSentenceIndex = Sentences.IndexOf(challenge);
        SetCurrentSentence();

    }

    [RelayCommand]
    async Task UseVocab(string word)
    {
        await GradeAnswer(word);
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

		await _speechToText.StartListenAsync(CultureInfo.GetCultureInfo("ko-KR"), cancellationToken);

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
		UserInput = e.RecognitionResult;
	}

	async void HandleSpeechToTextStateChanged(object? sender, SpeechToTextStateChangedEventArgs e)
	{
		await Toast.Make($"State Changed: {e.State}").Show(CancellationToken.None);
	}

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        ListID = (int)query["listID"];
        // PlayMode = (string)query["playMode"];
        // Level = (int)query["level"];

        TaskMonitor.Create(LoadSentences);
    }
}
