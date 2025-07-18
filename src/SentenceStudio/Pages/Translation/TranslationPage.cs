using MauiReactor.Shapes;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Clozure;
using System.Text;
using System.Web;
using System.Diagnostics;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Translation;

class TranslationPageState
{
    public bool IsBusy { get; set; }
    public bool IsBuffering { get; set; }
    public string UserMode { get; set; } = InputMode.Text.ToString();
    public string CurrentSentence { get; set; }
    public string UserInput { get; set; }
    public string Progress { get; set; }
    public bool HasFeedback { get; set; }
    public string Feedback { get; set; }
    public bool CanListenExecute { get; set; } = true;
    public bool CanStartListenExecute { get; set; } = true;
    public bool CanStopListenExecute { get; set; }
    public List<string> VocabBlocks { get; set; } = [];
    public string RecommendedTranslation { get; set; }
    public List<Challenge> Sentences { get; set; } = [];
    public string FeedbackMessage { get; set; }
    public string FeedbackType { get; set; } // "success", "info", "hint", "achievement"
    public bool ShowFeedback { get; set; }
}

partial class TranslationPage : Component<TranslationPageState, ActivityProps>
{
    [Inject] TeacherService _teacherService;
    [Inject] AiService _aiService;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] VocabularyProgressService _progressService;
    [Inject] LearningResourceRepository _resourceRepo;

    LocalizationManager _localize => LocalizationManager.Instance;

    int _currentSentenceIndex = 0;

    public override VisualNode Render() 
		=> ContentPage(
            Grid(rows: "*,80", columns: "*",
                ScrollView(
                    Grid("30,*,auto", "*",
                        RenderSentenceContent(),
                        RenderInputUI(),
                        RenderProgress()
                    )
                ),

                RenderBottomNavigation(),

                RenderLoadingOverlay()
            )
		)
		.OnAppearing(LoadSentences);

    VisualNode RenderLoadingOverlay() =>
		Grid(
			Label("Thinking.....")
				.FontSize(64)
				.TextColor(Theme.IsLightTheme ? 
					ApplicationTheme.DarkOnLightBackground : 
					ApplicationTheme.LightOnDarkBackground)
				.Center()
		)
			.Background(Color.FromArgb("#80000000"))
			.GridRowSpan(2)
			.IsVisible(State.IsBusy);

    VisualNode RenderSentenceContent() =>
        VStack(spacing: 16,
            Label()
                .Text(State.CurrentSentence)
                .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 32 : 64)
                .TextColor(Theme.IsLightTheme ?
                    ApplicationTheme.DarkOnLightBackground :
                    ApplicationTheme.LightOnDarkBackground)
                .HStart(),
            
            State.ShowFeedback ? 
                Border(
                    Label(State.FeedbackMessage)
                        .FontSize(16)
                        .Padding(12)
                        .Center()
                )
                .BackgroundColor(GetFeedbackBackgroundColor(State.FeedbackType))
                .StrokeShape(new RoundRectangle().CornerRadius(8))
                .StrokeThickness(0)
                .Margin(0, 8) 
                : null
        )
        .GridRow(1)
        .Margin(30);

    VisualNode RenderInputUI() =>
        Grid("*,*", "*,auto,auto,auto",
            State.UserMode == InputMode.MultipleChoice.ToString() ?
                RenderVocabBlocks() : null,
                RenderUserInput()
        )
        .RowSpacing(ApplicationTheme.Size40)
        .Padding(30)
        .ColumnSpacing(15)
        .GridRow(2);

    VisualNode RenderUserInput() =>
        new SfTextInputLayout(
            Entry()
                .FontSize(32)
                .ReturnType(ReturnType.Go)
                .Text(State.UserInput)
                .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                .OnCompleted(GradeMe)
		)
        .Hint("그건 한국어로 어떻게 말해요?")
        .GridRow(1)
        .GridColumnSpan(4);

    VisualNode RenderVocabBlocks() =>
        HStack(
            State.VocabBlocks.Select(word =>
                Button()
                    .Text(word)
                    .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 18 : 24)
                    .Padding(ApplicationTheme.Size40)
                    .BackgroundColor(ApplicationTheme.Gray200)
                    .TextColor(ApplicationTheme.Gray900)
                    .OnClicked(() => UseVocab(word))
            )
		)
		.Spacing(4)
        .GridRow(0)
        .GridColumnSpan(4);    

    VisualNode RenderProgress() =>
        HStack(        
            ActivityIndicator()
                .IsRunning(State.IsBuffering)
                .IsVisible(State.IsBuffering)
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .VCenter(),
            Label()
                .Text(State.Progress)
                .VCenter()
                .TextColor(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
		)
		.Spacing(8)
        .Padding(30)
        .HorizontalOptions(LayoutOptions.End)
        .VerticalOptions(LayoutOptions.Start)
        .GridRowSpan(2);

    VisualNode RenderBottomNavigation() =>
        Grid("1,*", "60,1,*,1,60,1,60",
            Button("GO")
                .Background(Colors.Transparent)
                .GridRow(1).GridColumn(4)
                .OnClicked(GradeMe),

            new ModeSelector()
                .SelectedMode(State.UserMode)
                .OnSelectedModeChanged(mode => SetState(s => s.UserMode = mode))
                .GridRow(1).GridColumn(2),

            ImageButton()
                .Background(Colors.Transparent)
                .Aspect(Aspect.Center)
                .Source(ApplicationTheme.IconPrevious)
                .GridRow(1).GridColumn(0)
                .OnClicked(PreviousSentence),

            ImageButton()
                .Background(Colors.Transparent)
                .Aspect(Aspect.Center)
                .Source(ApplicationTheme.IconNext)
                .GridRow(1).GridColumn(6)
                .OnClicked(NextSentence),

            BoxView()
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .HeightRequest(1)
                .GridColumnSpan(7),

            BoxView()
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .WidthRequest(1)
                .GridRow(1).GridColumn(1),

            BoxView()
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .WidthRequest(1)
                .GridRow(1).GridColumn(3),

            BoxView()
                .Color(Theme.IsLightTheme ? 
                    ApplicationTheme.DarkOnLightBackground : 
                    ApplicationTheme.LightOnDarkBackground)
                .WidthRequest(1)
                .GridRow(1).GridColumn(5)
        ).GridRow(1);

    // this is the label that should float over the screen near the cursor when over a text block
    VisualNode RenderPopOverLabel() =>
        Label()
            .Padding(8)
            .LineHeight(1)
            .IsVisible(false)
            .ZIndex(10)
            .FontSize(64)
            .HStart()
            .VStart()
            .BackgroundColor(Theme.IsLightTheme ?
                ApplicationTheme.LightBackground :
                ApplicationTheme.DarkBackground)
            .TextColor(Theme.IsLightTheme ?
                ApplicationTheme.DarkOnLightBackground :
                ApplicationTheme.LightOnDarkBackground);

    Color GetFeedbackBackgroundColor(string feedbackType) =>
        feedbackType switch
        {
            "success" => Color.FromArgb("#E8F5E8"),
            "achievement" => Color.FromArgb("#FFF3E0"),
            "hint" => Color.FromArgb("#E3F2FD"),
            _ => Color.FromArgb("#F5F5F5")
        };

    // Event handlers and methods
    async Task LoadSentences()
    {
        await Task.Delay(100);
        SetState(s => s.IsBusy = true);
        
        // Use the resource Id if available, or fallback to null
        var resourceId = Props.Resource?.Id ?? 0;
        
        var sentences = await _teacherService.GetChallenges(resourceId, 2, Props.Skill.Id);
        await Task.Delay(100);
        
        SetState(s => {
            foreach(var sentence in sentences)
            {
                s.Sentences.Add(sentence);
            }
        });
        
        SetState(s => s.IsBusy = false);
        
        SetCurrentSentence();

        if(State.Sentences.Count < 10)
        {
            SetState(s => s.IsBuffering = true);
            var moreSentences = await _teacherService.GetChallenges(resourceId, 8, Props.Skill.Id);
            SetState(s => {
                foreach(var sentence in moreSentences)
                {
                    s.Sentences.Add(sentence);
                }
                s.IsBuffering = false;
            });
        }
    }

    void SetCurrentSentence()
    {
        if (State.Sentences != null && State.Sentences.Count > 0 && _currentSentenceIndex < State.Sentences.Count)
        {
            SetState(s => {
                s.UserMode = InputMode.Text.ToString();
                s.HasFeedback = false;
                s.Feedback = string.Empty;
                s.ShowFeedback = false;
                s.FeedbackMessage = string.Empty;
                s.CurrentSentence = State.Sentences[_currentSentenceIndex].RecommendedTranslation;
                s.UserInput = string.Empty;
                s.RecommendedTranslation = State.Sentences[_currentSentenceIndex].SentenceText;
                s.Progress = $"{_currentSentenceIndex + 1} / {State.Sentences.Count}";
                s.VocabBlocks = State.Sentences[_currentSentenceIndex].Vocabulary?
                    .Select(v => v.TargetLanguageTerm)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .OrderBy(_ => Random.Shared.Next())
                    .ToList() ?? [];
            });
        }
    }

    async Task GradeMe()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput))
        {
            SetState(s => {
                s.FeedbackMessage = "Please enter your translation before grading.";
                s.FeedbackType = "hint";
                s.ShowFeedback = true;
            });
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        SetState(s => {
            s.Feedback = string.Empty;
            s.IsBusy = true;
            s.ShowFeedback = false;
        });

        var prompt = await BuildGradePrompt();
        if (string.IsNullOrEmpty(prompt)) return;

        try 
        {
            var feedback = await _aiService.SendPrompt<GradeResponse>(prompt);
            stopwatch.Stop();
            
            // Process vocabulary from translation
            await ProcessVocabularyFromTranslation(State.UserInput, feedback, (int)stopwatch.ElapsedMilliseconds);
            
            // Track user activity
            await _userActivityRepository.SaveAsync(new UserActivity
            {
                Activity = SentenceStudio.Shared.Models.Activity.Translation.ToString(),
                Input = State.UserInput,
                Accuracy = feedback.Accuracy,
                Fluency = feedback.Fluency,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });
            
            // Display comprehensive feedback
            var feedbackMessage = BuildFeedbackMessage(feedback);
            var feedbackType = GetFeedbackType((int)feedback.Accuracy);
            
            SetState(s => {
                s.HasFeedback = true;
                s.Feedback = FormatGradeResponse(feedback);
                s.FeedbackMessage = feedbackMessage;
                s.FeedbackType = feedbackType;
                s.ShowFeedback = true;
                s.IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Error in GradeMe: {ex.Message}");
            SetState(s => {
                s.FeedbackMessage = "Sorry, there was an error grading your translation. Please try again.";
                s.FeedbackType = "info";
                s.ShowFeedback = true;
                s.IsBusy = false;
            });
        }
    }
    
    private string FormatGradeResponse(GradeResponse gradeResponse)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendFormat("<p>{0}</p>", HttpUtility.HtmlEncode(State.CurrentSentence));
        sb.AppendFormat("<p>Accuracy: {0}</p>", HttpUtility.HtmlEncode(gradeResponse.Accuracy));
        sb.AppendFormat("<p>Explanation: {0}</p>", HttpUtility.HtmlEncode(gradeResponse.AccuracyExplanation));
        sb.AppendFormat("<p>Fluency: {0}</p>", HttpUtility.HtmlEncode(gradeResponse.Fluency));
        sb.AppendFormat("<p>Explanation: {0}</p>", HttpUtility.HtmlEncode(gradeResponse.FluencyExplanation));
        sb.AppendFormat("<p>Recommended: {0}</p>", HttpUtility.HtmlEncode(gradeResponse.GrammarNotes.RecommendedTranslation));
        sb.AppendFormat("<p>Notes: {0}</p>", HttpUtility.HtmlEncode(gradeResponse.GrammarNotes.Explanation));

        return sb.ToString();
    }    async Task<string> BuildGradePrompt()
    {
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeTranslation.scriban-txt");
        using StreamReader reader = new StreamReader(templateStream);
        var template = Template.Parse(await reader.ReadToEndAsync());
        return await template.RenderAsync(new {
            original_sentence = State.CurrentSentence,
            recommended_translation = State.RecommendedTranslation,
            user_input = State.UserInput
        });
    }

    void NextSentence()
    {
        if (_currentSentenceIndex < State.Sentences.Count - 1)
        {
            _currentSentenceIndex++;
            SetCurrentSentence();
        }
    }

    void PreviousSentence()
    {
        if (_currentSentenceIndex > 0)
        {
            _currentSentenceIndex--;
            SetCurrentSentence();
        }
    }

    void UseVocab(string word)
    {
        SetState(s => s.UserInput += word);
    }

    string BuildFeedbackMessage(GradeResponse feedback)
    {
        var message = new StringBuilder();
        
        if (feedback.Accuracy >= 90)
            message.AppendLine("🎉 Excellent translation!");
        else if (feedback.Accuracy >= 80)
            message.AppendLine("✅ Great work!");
        else if (feedback.Accuracy >= 70)
            message.AppendLine("👍 Good effort!");
        else
            message.AppendLine("💪 Keep practicing!");
        
        message.AppendLine($"\nAccuracy: {feedback.Accuracy}/100");
        if (!string.IsNullOrEmpty(feedback.AccuracyExplanation))
            message.AppendLine(feedback.AccuracyExplanation);
        
        message.AppendLine($"\nFluency: {feedback.Fluency}/100");
        if (!string.IsNullOrEmpty(feedback.FluencyExplanation))
            message.AppendLine(feedback.FluencyExplanation);
        
        if (feedback.GrammarNotes != null)
        {
            if (!string.IsNullOrEmpty(feedback.GrammarNotes.RecommendedTranslation))
                message.AppendLine($"\nRecommended: {feedback.GrammarNotes.RecommendedTranslation}");
            if (!string.IsNullOrEmpty(feedback.GrammarNotes.Explanation))
                message.AppendLine($"Notes: {feedback.GrammarNotes.Explanation}");
        }
        
        return message.ToString();
    }

    string GetFeedbackType(int accuracy) =>
        accuracy switch
        {
            >= 90 => "success",
            >= 80 => "achievement", 
            >= 70 => "info",
            _ => "hint"
        };

    async Task ProcessVocabularyFromTranslation(string userInput, GradeResponse grade, int responseTimeMs)
    {
        try
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Starting vocabulary processing for: '{userInput}'");
            
            // Extract vocabulary words from user input using AI analysis
            var words = await ExtractVocabularyFromUserInput(userInput, grade);
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Extracted {words.Count} vocabulary words");
            
            // Process each vocabulary word
            foreach (var word in words)
            {
                try 
                {
                    var attempt = new VocabularyAttempt
                    {
                        VocabularyWordId = word.Id,
                        UserId = 1, // Default user
                        Activity = "Translation",
                        InputMode = "TextEntry",
                        WasCorrect = true, // For now, assume translation attempts are learning experiences
                        ContextType = "Sentence",
                        UserInput = userInput,
                        ExpectedAnswer = word.NativeLanguageTerm,
                        ResponseTimeMs = responseTimeMs,
                        DifficultyWeight = 1.0f
                    };
                    
                    var progress = await _progressService.RecordAttemptAsync(attempt);
                    
                    Debug.WriteLine($"🏴‍☠️ TranslationPage: Recorded progress for '{word.TargetLanguageTerm}'");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"🏴‍☠️ TranslationPage: Error recording progress for word '{word.TargetLanguageTerm}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Error in ProcessVocabularyFromTranslation: {ex.Message}");
        }
    }

    async Task<List<VocabularyWord>> ExtractVocabularyFromUserInput(string userInput, GradeResponse grade)
    {
        try
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Extracting vocabulary from user input: '{userInput}'");
            
            // Get available vocabulary from learning resources
            var resources = await _resourceRepo.GetAllResourcesAsync();
            var allVocabulary = resources.SelectMany(r => r.Vocabulary ?? new List<VocabularyWord>()).ToList();
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Loaded {allVocabulary.Count} vocabulary words from {resources.Count} resources");
            
            var foundWords = new List<VocabularyWord>();
            
            // First, try to use AI vocabulary analysis if available
            if (grade?.VocabularyAnalysis != null && grade.VocabularyAnalysis.Any())
            {
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Using AI vocabulary analysis - found {grade.VocabularyAnalysis.Count} analyzed words");
                
                foreach (var analysis in grade.VocabularyAnalysis)
                {
                    Debug.WriteLine($"🏴‍☠️ TranslationPage: Looking for dictionary form '{analysis.DictionaryForm}' (used as '{analysis.UsedForm}')");
                    
                    // Try to find the word by dictionary form first
                    var vocabularyWord = allVocabulary.FirstOrDefault(v => 
                        v.TargetLanguageTerm?.Equals(analysis.DictionaryForm, StringComparison.OrdinalIgnoreCase) == true);
                    
                    if (vocabularyWord == null)
                    {
                        // Try to find by used form
                        vocabularyWord = allVocabulary.FirstOrDefault(v => 
                            v.TargetLanguageTerm?.Equals(analysis.UsedForm, StringComparison.OrdinalIgnoreCase) == true);
                    }
                    
                    if (vocabularyWord != null)
                    {
                        foundWords.Add(vocabularyWord);
                        Debug.WriteLine($"🏴‍☠️ TranslationPage: ✅ Found match for '{analysis.UsedForm}' -> '{vocabularyWord.TargetLanguageTerm}'");
                    }
                    else
                    {
                        Debug.WriteLine($"🏴‍☠️ TranslationPage: ❌ No match found for '{analysis.UsedForm}' (dictionary form: '{analysis.DictionaryForm}')");
                    }
                }
            }
            
            // Fallback: Simple word extraction for Korean text
            if (foundWords.Count == 0)
            {
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Using fallback word extraction for Korean text");
                
                // Split by spaces and common Korean particles/endings
                var particles = new[] { "은", "는", "이", "가", "을", "를", "에", "에서", "으로", "로", "과", "와", "하고" };
                var potentialWords = userInput
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .SelectMany(word => {
                        // Remove particles from the end of words
                        var cleanWord = word;
                        foreach (var particle in particles)
                        {
                            if (cleanWord.EndsWith(particle))
                                cleanWord = cleanWord.Substring(0, cleanWord.Length - particle.Length);
                        }
                        return new[] { word, cleanWord };
                    })
                    .Where(word => !string.IsNullOrWhiteSpace(word) && word.Length > 1)
                    .Distinct()
                    .ToList();
                
                foreach (var word in potentialWords)
                {
                    var vocabularyWord = allVocabulary.FirstOrDefault(v => 
                        v.TargetLanguageTerm?.Contains(word, StringComparison.OrdinalIgnoreCase) == true);
                    
                    if (vocabularyWord != null && !foundWords.Contains(vocabularyWord))
                    {
                        foundWords.Add(vocabularyWord);
                        Debug.WriteLine($"🏴‍☠️ TranslationPage: ✅ Fallback found match for '{word}' -> '{vocabularyWord.TargetLanguageTerm}'");
                    }
                }
            }
            
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Final result: Found {foundWords.Count} vocabulary words");
            return foundWords;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Error in ExtractVocabularyFromUserInput: {ex.Message}");
            return new List<VocabularyWord>();
        }
    }    

    protected override void OnMounted()
    {
        base.OnMounted();
        LoadSentences();
    }
}

partial class FeedbackPanel : Component
{
    public bool IsVisible { get; set; }
    public string Feedback { get; set; }

    public override VisualNode Render()
    {
        return Border(
            VScrollView(
                VStack(
                    Label()
                        .Text(Feedback)
                        .TextColor(Theme.IsLightTheme ? 
                            ApplicationTheme.DarkOnLightBackground : 
                            ApplicationTheme.LightOnDarkBackground)
                        .FontSize(24)
                )
            )
		)
        .Background(Theme.IsLightTheme ? 
            ApplicationTheme.LightBackground : 
            ApplicationTheme.DarkBackground)
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .Padding(20)
        .IsVisible(IsVisible);
    }
}