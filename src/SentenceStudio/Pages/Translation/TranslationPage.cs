using MauiReactor.Shapes;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Clozure;
using System.Text;
using System.Web;
using System.Diagnostics;
using Scriban;

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
    [Inject] TranslationService _translationService;
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
            
            // Add vocabulary progress scoreboard
            RenderVocabularyScoreboard(),
            
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

    VisualNode RenderVocabularyScoreboard() =>
        _currentSentenceIndex >= 0 && _currentSentenceIndex < State.Sentences.Count ?
            HStack(
                State.Sentences[_currentSentenceIndex].Vocabulary?
                    .Select(word => RenderVocabularyWordStatusSync(word))
                    .ToArray() ?? Array.Empty<VisualNode>()
            )
            .Spacing(8)
            .Margin(0, 8)
            .HorizontalOptions(LayoutOptions.Center)
            : null;

    VisualNode RenderVocabularyWordStatusSync(VocabularyWord word)
    {
        try
        {
            // Use a simple visual indicator for now - can be enhanced with real-time progress later
            return Border(
                    Label("◦")
                        .FontSize(16)
                        .TextColor(ApplicationTheme.Primary)
                        .Center()
                )
                .StrokeShape(new RoundRectangle().CornerRadius(12))
                .StrokeThickness(1)
                .Stroke(ApplicationTheme.Primary)
                .HeightRequest(24)
                .WidthRequest(24)
                .BackgroundColor(Colors.Transparent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Error rendering word status for '{word.TargetLanguageTerm}': {ex.Message}");
            return Border()
                .StrokeShape(new RoundRectangle().CornerRadius(12))
                .StrokeThickness(1)
                .Stroke(ApplicationTheme.Gray400)
                .HeightRequest(24)
                .WidthRequest(24)
                .BackgroundColor(Colors.Transparent);
        }
    }
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
        
        try 
        {
            // Use the resource Id if available, or fallback to null
            var resourceId = Props.Resource?.Id ?? 0;
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Loading sentences for resource {resourceId}, skill {Props.Skill?.Id}");
            
            var sentences = await _translationService.GetTranslationSentences(resourceId, 2, Props.Skill.Id);
            await Task.Delay(100);
            
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Received {sentences?.Count ?? 0} sentences from translation service");
            
            if (sentences?.Any() == true)
            {
                SetState(s => {
                    foreach(var sentence in sentences)
                    {
                        Debug.WriteLine($"🏴‍☠️ TranslationPage: Adding sentence: '{sentence.SentenceText}' -> '{sentence.RecommendedTranslation}'");
                        Debug.WriteLine($"🏴‍☠️ TranslationPage: Vocabulary count: {sentence.Vocabulary?.Count ?? 0}");
                        if (sentence.Vocabulary?.Any() == true)
                        {
                            Debug.WriteLine($"🏴‍☠️ TranslationPage: Vocabulary words: [{string.Join(", ", sentence.Vocabulary.Select(v => $"{v.TargetLanguageTerm}({v.NativeLanguageTerm})"))}]");
                        }
                        s.Sentences.Add(sentence);
                    }
                });
                
                SetState(s => s.IsBusy = false);
                
                SetCurrentSentence();

                if(State.Sentences.Count < 10)
                {
                    Debug.WriteLine($"🏴‍☠️ TranslationPage: Loading additional sentences in background");
                    SetState(s => s.IsBuffering = true);
                    var moreSentences = await _translationService.GetTranslationSentences(resourceId, 8, Props.Skill.Id);
                    SetState(s => {
                        foreach(var sentence in moreSentences)
                        {
                            s.Sentences.Add(sentence);
                        }
                        s.IsBuffering = false;
                    });
                }
            }
            else
            {
                Debug.WriteLine("🏴‍☠️ TranslationPage: No sentences returned from translation service");
                SetState(s => {
                    s.CurrentSentence = "No sentences available for this skill. Check yer resource configuration, matey!";
                    s.IsBusy = false;
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Error loading sentences: {ex.Message}");
            SetState(s => {
                s.CurrentSentence = $"Error loading sentences: {ex.Message}";
                s.IsBusy = false;
            });
        }
    }

    void SetCurrentSentence()
    {
        if (State.Sentences != null && State.Sentences.Count > 0 && _currentSentenceIndex < State.Sentences.Count)
        {
            SetState(s => {
                // 🏴‍☠️ CRITICAL FIX: Reset input mode to Text/Keyboard when moving to next sentence
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
            
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Set current sentence {_currentSentenceIndex + 1}/{State.Sentences.Count}");
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Input mode reset to: {InputMode.Text}");
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Available vocabulary blocks: [{string.Join(", ", State.VocabBlocks)}]");
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
            
            // Display comprehensive feedback with enhanced context awareness
            var feedbackMessage = await BuildEnhancedFeedbackMessage(feedback);
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

    async Task<string> BuildEnhancedFeedbackMessage(GradeResponse feedback)
    {
        var message = new StringBuilder();
        
        // Context-aware primary feedback
        if (State.UserMode == InputMode.MultipleChoice.ToString())
        {
            // Vocabulary blocks mode feedback
            if (feedback.Accuracy >= 85)
                message.AppendLine("🎉 Perfect! Great use of vocabulary blocks!");
            else if (feedback.Accuracy >= 75)
                message.AppendLine("✅ Excellent! Vocabulary blocks helped you succeed!");
            else if (feedback.Accuracy >= 65)
                message.AppendLine("👍 Good effort with vocabulary blocks!");
            else
                message.AppendLine("💪 Try different combinations with the vocabulary blocks!");
        }
        else
        {
            // Free text entry feedback
            if (feedback.Accuracy >= 90)
                message.AppendLine("🎉 Outstanding free translation!");
            else if (feedback.Accuracy >= 80)
                message.AppendLine("✅ Excellent translation skills!");
            else if (feedback.Accuracy >= 70)
                message.AppendLine("👍 Strong translation attempt!");
            else
                message.AppendLine("💪 Keep developing your translation skills!");
        }
        
        // Add vocabulary achievement feedback
        try
        {
            var allSentenceWords = await GetAllVocabularyFromCurrentSentence();
            var usedWords = await ExtractVocabularyFromUserInput(State.UserInput, feedback);
            
            if (usedWords.Count == allSentenceWords.Count && allSentenceWords.Count > 0)
            {
                message.AppendLine("🌟 Amazing! You used ALL the vocabulary words!");
            }
            else if (usedWords.Count > 0)
            {
                message.AppendLine($"📚 Great! You used {usedWords.Count} vocabulary word{(usedWords.Count > 1 ? "s" : "")}!");
            }
            
            // Check for conjugated forms
            if (feedback.VocabularyAnalysis?.Any(va => !string.Equals(va.DictionaryForm, va.UsedForm, StringComparison.OrdinalIgnoreCase)) == true)
            {
                message.AppendLine("💪 Excellent work with word conjugations!");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Error in vocabulary feedback: {ex.Message}");
        }
        
        // Add accuracy and fluency scores
        message.AppendLine($"\nAccuracy: {feedback.Accuracy}/100");
        if (!string.IsNullOrEmpty(feedback.AccuracyExplanation))
            message.AppendLine(feedback.AccuracyExplanation);
        
        message.AppendLine($"\nFluency: {feedback.Fluency}/100");
        if (!string.IsNullOrEmpty(feedback.FluencyExplanation))
            message.AppendLine(feedback.FluencyExplanation);
        
        // Grammar and improvement notes
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
            
            // Get ALL vocabulary words from the current sentence, not just those in user input
            var allSentenceWords = await GetAllVocabularyFromCurrentSentence();
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Found {allSentenceWords.Count} vocabulary words in current sentence");
            
            // Extract words actually used by the user
            var usedWords = await ExtractVocabularyFromUserInput(userInput, grade);
            Debug.WriteLine($"🏴‍☠️ TranslationPage: User used {usedWords.Count} vocabulary words");
            
            // Calculate base difficulty for this translation
            var baseDifficulty = CalculateTranslationDifficulty(userInput, grade, allSentenceWords.Count);
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Base difficulty calculated: {baseDifficulty}");
            
            // Process ALL vocabulary words from the sentence
            foreach (var word in allSentenceWords)
            {
                try 
                {
                    // Additional safety check - ensure word has valid ID
                    if (word.Id <= 0)
                    {
                        Debug.WriteLine($"🏴‍☠️ TranslationPage: ⚠️ Skipping word '{word.TargetLanguageTerm}' - invalid ID: {word.Id}");
                        continue;
                    }
                    
                    var wasUsedCorrectly = usedWords.Any(uw => uw.Id == word.Id);
                    var contextType = DetermineTranslationContextType(word, userInput, grade);
                    var wordDifficulty = CalculateWordSpecificDifficulty(word, baseDifficulty, contextType);
                    
                    var attempt = new VocabularyAttempt
                    {
                        VocabularyWordId = word.Id,
                        UserId = 1, // Default user
                        Activity = "Translation",
                        InputMode = State.UserMode == InputMode.MultipleChoice.ToString() ? "VocabularyBlocks" : "TextEntry",
                        WasCorrect = DetermineWordCorrectness(wasUsedCorrectly, grade, word),
                        ContextType = contextType,
                        UserInput = userInput,
                        ExpectedAnswer = word.NativeLanguageTerm,
                        ResponseTimeMs = responseTimeMs,
                        DifficultyWeight = wordDifficulty
                    };
                    
                    var progress = await _progressService.RecordAttemptAsync(attempt);
                    
                    Debug.WriteLine($"🏴‍☠️ TranslationPage: ✅ Recorded progress for '{word.TargetLanguageTerm}' " +
                        $"(ID: {word.Id}, Used: {wasUsedCorrectly}, Correct: {attempt.WasCorrect}, Difficulty: {wordDifficulty:F2}, Context: {contextType})");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"🏴‍☠️ TranslationPage: ❌ Error recording progress for word '{word.TargetLanguageTerm}' (ID: {word.Id}): {ex.Message}");
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
            var allVocabulary = resources.SelectMany(r => r.Vocabulary ?? new List<VocabularyWord>())
                .Where(v => v.Id > 0 && !string.IsNullOrEmpty(v.TargetLanguageTerm)) // Only valid words with IDs
                .ToList();
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Loaded {allVocabulary.Count} valid vocabulary words from {resources.Count} resources");
            
            var foundWords = new List<VocabularyWord>();
            
            // First, try to use AI vocabulary analysis if available
            if (grade?.VocabularyAnalysis != null && grade.VocabularyAnalysis.Any())
            {
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Using AI vocabulary analysis - found {grade.VocabularyAnalysis.Count} analyzed words");
                
                foreach (var analysis in grade.VocabularyAnalysis)
                {
                    // Skip particles and invalid words
                    if (await IsValidVocabularyTerm(analysis.DictionaryForm) && 
                        await IsValidVocabularyTerm(analysis.UsedForm))
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
                        
                        if (vocabularyWord != null && !foundWords.Any(fw => fw.Id == vocabularyWord.Id))
                        {
                            foundWords.Add(vocabularyWord);
                            Debug.WriteLine($"🏴‍☠️ TranslationPage: ✅ Found match for '{analysis.UsedForm}' -> '{vocabularyWord.TargetLanguageTerm}' (ID: {vocabularyWord.Id})");
                        }
                        else
                        {
                            Debug.WriteLine($"🏴‍☠️ TranslationPage: ❌ No match found for '{analysis.UsedForm}' (dictionary form: '{analysis.DictionaryForm}')");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"🏴‍☠️ TranslationPage: ⚠️ Skipping invalid vocabulary term: '{analysis.UsedForm}' (dictionary: '{analysis.DictionaryForm}')");
                    }
                }
            }
            
            // Fallback: Simple word extraction for Korean text, but filter out particles
            if (foundWords.Count == 0)
            {
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Using fallback word extraction for Korean text");
                
                var words = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var potentialWords = new List<string>();
                
                foreach (var word in words)
                {
                    if (await IsValidVocabularyTerm(word))
                    {
                        var cleanWord = RemoveKoreanParticles(word);
                        potentialWords.Add(word);
                        if (cleanWord != word && await IsValidVocabularyTerm(cleanWord))
                        {
                            potentialWords.Add(cleanWord);
                        }
                    }
                }
                
                var validWords = potentialWords
                    .Where(word => !string.IsNullOrWhiteSpace(word) && word.Length > 1)
                    .Distinct()
                    .ToList();
                
                foreach (var word in validWords)
                {
                    var vocabularyWord = allVocabulary.FirstOrDefault(v => 
                        v.TargetLanguageTerm?.Contains(word, StringComparison.OrdinalIgnoreCase) == true);
                    
                    if (vocabularyWord != null && !foundWords.Any(fw => fw.Id == vocabularyWord.Id))
                    {
                        foundWords.Add(vocabularyWord);
                        Debug.WriteLine($"🏴‍☠️ TranslationPage: ✅ Fallback found match for '{word}' -> '{vocabularyWord.TargetLanguageTerm}' (ID: {vocabularyWord.Id})");
                    }
                }
            }
            
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Final result: Found {foundWords.Count} valid vocabulary words");
            return foundWords;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Error in ExtractVocabularyFromUserInput: {ex.Message}");
            return new List<VocabularyWord>();
        }
    }

    async Task<bool> IsValidVocabularyWord(VocabularyWord word)
    {
        if (word == null || word.Id <= 0 || string.IsNullOrEmpty(word.TargetLanguageTerm))
            return false;
            
        return await IsValidVocabularyTerm(word.TargetLanguageTerm);
    }

    async Task<bool> IsValidVocabularyTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;
            
        // Filter out Korean particles and common function words
        var koreanParticles = new HashSet<string> 
        { 
            "은", "는", "이", "가", "을", "를", "에", "에서", "으로", "로", 
            "과", "와", "하고", "의", "도", "만", "부터", "까지", "처럼", "같이",
            "에게", "한테", "께", "보다", "보단", "만큼", "대로", "따라", "에서부터",
            "까지만", "조차", "마저", "밖에", "뿐", "라도", "든지", "거나", "든가"
        };
        
        var englishParticles = new HashSet<string>
        {
            "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for",
            "of", "with", "by", "from", "as", "is", "was", "are", "were", "be",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "can", "must", "shall", "ought", "need",
            "dare", "used", "am", "being", "been", "this", "that", "these", "those"
        };
        
        var cleanTerm = term.Trim().ToLower();
        
        // Check if it's a particle or function word
        if (koreanParticles.Contains(cleanTerm) || englishParticles.Contains(cleanTerm))
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Filtered out particle/function word: '{term}'");
            return false;
        }
        
        // Additional check for pure particle words (single character Korean particles)
        if (IsKoreanText(term) && term.Length == 1)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Filtered out single character Korean: '{term}'");
            return false;
        }
        
        // Must be at least 2 characters for Korean, or at least 3 for English
        if (IsKoreanText(term) && term.Length < 2)
            return false;
        if (!IsKoreanText(term) && term.Length < 3)
            return false;
            
        return true;
    }

    async Task<VocabularyWord?> LookupVocabularyWordInDatabase(VocabularyWord word)
    {
        try
        {
            // If the word already has a valid ID, return it
            if (word.Id > 0)
                return word;
                
            // Look up the word in all resources to get the proper database ID
            var resources = await _resourceRepo.GetAllResourcesAsync();
            var allVocabulary = resources.SelectMany(r => r.Vocabulary ?? new List<VocabularyWord>());
            
            var dbWord = allVocabulary.FirstOrDefault(v => 
                v.TargetLanguageTerm?.Equals(word.TargetLanguageTerm, StringComparison.OrdinalIgnoreCase) == true &&
                v.Id > 0);
                
            if (dbWord != null)
            {
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Found database word: '{word.TargetLanguageTerm}' -> ID {dbWord.Id}");
                return dbWord;
            }
            
            Debug.WriteLine($"🏴‍☠️ TranslationPage: No database match found for: '{word.TargetLanguageTerm}'");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Error looking up word '{word.TargetLanguageTerm}': {ex.Message}");
            return null;
        }
    }

    string RemoveKoreanParticles(string word)
    {
        var particles = new[] { 
            "에서부터", "까지만", "처럼", "같이", "에게", "한테", "보다", "만큼", "대로", "따라",
            "은", "는", "이", "가", "을", "를", "에", "에서", "으로", "로", "과", "와", "하고", 
            "의", "도", "만", "부터", "까지", "께", "조차", "마저", "밖에", "뿐", "라도", 
            "든지", "거나", "든가"
        };
        
        foreach (var particle in particles.OrderByDescending(p => p.Length)) // Remove longer particles first
        {
            if (word.EndsWith(particle) && word.Length > particle.Length)
            {
                var cleanWord = word.Substring(0, word.Length - particle.Length);
                if (cleanWord.Length >= 2) // Ensure we don't create too short words
                {
                    Debug.WriteLine($"🏴‍☠️ TranslationPage: Removed particle '{particle}' from '{word}' -> '{cleanWord}'");
                    return cleanWord;
                }
            }
        }
        
        return word;
    }

    bool IsKoreanText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
            
        // Check if text contains Korean characters (Hangul syllables, Jamo, etc.)
        return text.Any(c => 
            (c >= 0xAC00 && c <= 0xD7AF) || // Hangul syllables
            (c >= 0x1100 && c <= 0x11FF) || // Hangul Jamo
            (c >= 0x3130 && c <= 0x318F) || // Hangul Compatibility Jamo
            (c >= 0xA960 && c <= 0xA97F) || // Hangul Jamo Extended-A
            (c >= 0xD7B0 && c <= 0xD7FF));  // Hangul Jamo Extended-B
    }

    async Task<List<VocabularyWord>> GetAllVocabularyFromCurrentSentence()
    {
        try
        {
            if (_currentSentenceIndex >= 0 && _currentSentenceIndex < State.Sentences.Count)
            {
                var currentSentence = State.Sentences[_currentSentenceIndex];
                var sentenceVocab = currentSentence.Vocabulary?.ToList() ?? new List<VocabularyWord>();
                
                // Filter out invalid vocabulary and ensure we have valid IDs
                var validVocab = new List<VocabularyWord>();
                foreach (var word in sentenceVocab)
                {
                    if (await IsValidVocabularyWord(word))
                    {
                        // Look up the word in the database to get the proper ID
                        var dbWord = await LookupVocabularyWordInDatabase(word);
                        if (dbWord != null)
                        {
                            validVocab.Add(dbWord);
                        }
                    }
                }
                
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Filtered vocabulary - {sentenceVocab.Count} -> {validVocab.Count} valid words");
                return validVocab;
            }
            return new List<VocabularyWord>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Error getting sentence vocabulary: {ex.Message}");
            return new List<VocabularyWord>();
        }
    }

    private float CalculateTranslationDifficulty(string userInput, GradeResponse grade, int vocabularyWordCount)
    {
        float difficulty = 1.0f; // Base difficulty for translation
        
        // Input mode adjustment - vocabulary blocks are easier
        if (State.UserMode == InputMode.MultipleChoice.ToString())
        {
            difficulty *= 0.7f; // Vocabulary blocks are significantly easier
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Vocabulary blocks mode - difficulty reduced to {difficulty:F2}");
        }
        else
        {
            difficulty *= 1.2f; // Free text entry is harder
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Text entry mode - difficulty increased to {difficulty:F2}");
        }
        
        // Sentence complexity based on word count
        var wordCount = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 10)
        {
            difficulty *= 1.3f;
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Long sentence ({wordCount} words) - difficulty increased to {difficulty:F2}");
        }
        else if (wordCount > 15)
        {
            difficulty *= 1.5f;
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Very long sentence ({wordCount} words) - difficulty increased to {difficulty:F2}");
        }
        
        // Translation quality impact on difficulty
        if (grade != null)
        {
            var qualityMultiplier = Math.Max(0.8f, (float)(grade.Accuracy / 100.0));
            difficulty *= qualityMultiplier;
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Quality adjustment ({grade.Accuracy}%) - difficulty adjusted to {difficulty:F2}");
        }
        
        // Vocabulary density - more vocab words = harder
        if (vocabularyWordCount > 3)
        {
            difficulty *= 1.2f;
            Debug.WriteLine($"🏴‍☠️ TranslationPage: High vocabulary density ({vocabularyWordCount} words) - difficulty increased to {difficulty:F2}");
        }
        
        // Clamp difficulty between reasonable bounds
        var finalDifficulty = Math.Min(2.5f, Math.Max(0.5f, difficulty));
        Debug.WriteLine($"🏴‍☠️ TranslationPage: Final difficulty (clamped): {finalDifficulty:F2}");
        
        return finalDifficulty;
    }

    private string DetermineTranslationContextType(VocabularyWord word, string userInput, GradeResponse grade)
    {
        // Check if the word appears in a conjugated or modified form
        if (grade?.VocabularyAnalysis != null)
        {
            var analysis = grade.VocabularyAnalysis.FirstOrDefault(va => 
                va.DictionaryForm?.Equals(word.TargetLanguageTerm, StringComparison.OrdinalIgnoreCase) == true ||
                va.UsedForm?.Equals(word.TargetLanguageTerm, StringComparison.OrdinalIgnoreCase) == true);
            
            if (analysis != null && !string.Equals(analysis.DictionaryForm, analysis.UsedForm, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Word '{word.TargetLanguageTerm}' identified as Conjugated (dictionary: {analysis.DictionaryForm}, used: {analysis.UsedForm})");
                return "Conjugated";
            }
        }
        
        // Check for grammar complexity indicators
        if (grade?.GrammarNotes?.Explanation?.ToLower().Contains("conjugation") == true ||
            grade?.GrammarNotes?.Explanation?.ToLower().Contains("verb form") == true ||
            grade?.GrammarNotes?.Explanation?.ToLower().Contains("tense") == true)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Complex grammar detected for '{word.TargetLanguageTerm}'");
            return "Complex";
        }
        
        // Check sentence length for complexity
        var wordCount = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 12)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Long sentence context for '{word.TargetLanguageTerm}'");
            return "Complex";
        }
        
        Debug.WriteLine($"🏴‍☠️ TranslationPage: Standard sentence context for '{word.TargetLanguageTerm}'");
        return "Sentence";
    }

    private float CalculateWordSpecificDifficulty(VocabularyWord word, float baseDifficulty, string contextType)
    {
        float wordDifficulty = baseDifficulty;
        
        // Apply context type multipliers (similar to Clozure)
        switch (contextType)
        {
            case "Conjugated":
                wordDifficulty *= 1.8f;
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Conjugated context for '{word.TargetLanguageTerm}' - difficulty: {wordDifficulty:F2}");
                break;
            case "Complex":
                wordDifficulty *= 1.4f;
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Complex context for '{word.TargetLanguageTerm}' - difficulty: {wordDifficulty:F2}");
                break;
            case "Sentence":
                wordDifficulty *= 1.2f;
                Debug.WriteLine($"🏴‍☠️ TranslationPage: Sentence context for '{word.TargetLanguageTerm}' - difficulty: {wordDifficulty:F2}");
                break;
        }
        
        // Word-specific difficulty based on length/complexity
        if (word.TargetLanguageTerm?.Length > 6)
        {
            wordDifficulty *= 1.1f;
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Long word '{word.TargetLanguageTerm}' - difficulty: {wordDifficulty:F2}");
        }
        
        // Clamp final difficulty
        var finalDifficulty = Math.Min(3.0f, Math.Max(0.3f, wordDifficulty));
        Debug.WriteLine($"🏴‍☠️ TranslationPage: Final word difficulty for '{word.TargetLanguageTerm}': {finalDifficulty:F2}");
        
        return finalDifficulty;
    }

    private bool DetermineWordCorrectness(bool wasUsedByUser, GradeResponse grade, VocabularyWord word)
    {
        // If the user didn't use the word at all, it's considered incorrect for vocabulary tracking
        if (!wasUsedByUser)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Word '{word.TargetLanguageTerm}' not used by user - marked incorrect");
            return false;
        }
        
        // If translation accuracy is very low, consider vocabulary usage incorrect
        if (grade?.Accuracy < 50)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Low accuracy ({grade.Accuracy}%) - word '{word.TargetLanguageTerm}' marked incorrect");
            return false;
        }
        
        // For vocabulary blocks mode, be more lenient since they're guided
        if (State.UserMode == InputMode.MultipleChoice.ToString() && grade?.Accuracy >= 60)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Vocabulary blocks mode with decent accuracy - word '{word.TargetLanguageTerm}' marked correct");
            return true;
        }
        
        // For text entry, require higher accuracy
        if (State.UserMode == InputMode.Text.ToString() && grade?.Accuracy >= 70)
        {
            Debug.WriteLine($"🏴‍☠️ TranslationPage: Text entry mode with good accuracy - word '{word.TargetLanguageTerm}' marked correct");
            return true;
        }
        
        Debug.WriteLine($"🏴‍☠️ TranslationPage: Word '{word.TargetLanguageTerm}' marked incorrect (accuracy: {grade?.Accuracy}%, mode: {State.UserMode})");
        return false;
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