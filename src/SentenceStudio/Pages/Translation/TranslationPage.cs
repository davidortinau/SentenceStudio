using MauiReactor.Shapes;
using SentenceStudio.Services;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Clozure;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Components;

namespace SentenceStudio.Pages.Translation;

/// <summary>
/// Translation Activity Page - Sentence Translation Practice
/// 
/// USAGE CONTEXTS (CRITICAL - This page serves multiple purposes!):
/// 
/// 1. FROM DAILY PLAN (Structured Learning):
///    - Entry: Dashboard → Today's Plan → Click "Translation" activity
///    - Props.FromTodaysPlan = true, Props.PlanItemId = set
///    - Content: Pre-selected sentences for translation practice
///    - Timer: ActivityTimerBar visible in Shell.TitleView
///    - Completion: Updates plan progress, returns to dashboard
///    - User Expectation: "I'm completing my daily translation practice"
/// 
/// 2. MANUAL RESOURCE SELECTION (Free Practice):
///    - Entry: Resources → Browse → Select resource → Start Translation
///    - Props.FromTodaysPlan = false, Props.PlanItemId = null
///    - Content: User-selected resource sentences
///    - Timer: No timer displayed
///    - Completion: Shows summary, offers continue/return options
///    - User Expectation: "I'm practicing translation with this specific resource"
/// 
/// 3. FUTURE CONTEXTS (Update this section as new uses are added!):
///    - Bidirectional Translation: Both target→native and native→target
///    - AI Feedback: Detailed translation quality analysis
///    - Collaborative Translation: Compare with other learners
/// 
/// IMPORTANT: When modifying this page, ensure changes work correctly for ALL contexts!
/// Test both daily plan flow AND manual resource selection before committing.
/// </summary>

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
    public string TargetLanguage { get; set; } = "Korean"; // Default, will be set from resource or user profile

    // Session summary tracking
    public bool ShowSessionSummary { get; set; }
    public int SessionGradedCount { get; set; }
    public double SessionAccuracySum { get; set; }
    public double SessionFluencySum { get; set; }
}

partial class TranslationPage : Component<TranslationPageState, ActivityProps>
{
    [Inject] TranslationService _translationService;
    [Inject] AiService _aiService;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] VocabularyProgressService _progressService;
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] UserProfileRepository _userProfileRepository;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
    [Inject] ILogger<TranslationPage> _logger;
    [Inject] NativeThemeService _themeService;

    LocalizationManager _localize => LocalizationManager.Instance;

    int _currentSentenceIndex = 0;

    string GetInputPlaceholder() =>
        string.Format($"{_localize["TranslationInputPlaceholder"]}", State.TargetLanguage);

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

                RenderLoadingOverlay(),

                RenderSessionSummary()
            )
        )
        .Set(MauiControls.Shell.TitleViewProperty, Props?.FromTodaysPlan == true ? new ActivityTimerBar() : null)
        .BackgroundColor(BootstrapTheme.Current.GetBackground())
        .OnAppearing(LoadSentences);

    VisualNode RenderLoadingOverlay()
    {
        var theme = BootstrapTheme.Current;
        return Grid(
            VStack(spacing: 12,
                ActivityIndicator()
                    .IsRunning(true)
                    .Color(theme.Primary)
                    .HCenter(),
                Label($"{_localize["LoadingSentences"]}")
                    .TextColor(BootstrapTheme.Current.OnPrimary)
                    .FontSize(16)
                    .HCenter()
            )
            .VCenter()
        )
            .Background(Color.FromArgb("#80000000"))
            .GridRowSpan(2)
            .IsVisible(State.IsBusy);
    }

    VisualNode RenderSessionSummary()
    {
        var theme = BootstrapTheme.Current;
        var avgAccuracy = State.SessionGradedCount > 0
            ? (int)(State.SessionAccuracySum / State.SessionGradedCount) : 0;
        var avgFluency = State.SessionGradedCount > 0
            ? (int)(State.SessionFluencySum / State.SessionGradedCount) : 0;

        return Grid(
            ScrollView(
                VStack(spacing: 16,
                    // Check icon
                    Image()
                        .Source(BootstrapIcons.Create(BootstrapIcons.CheckCircleFill, theme.Success, 48))
                        .HCenter(),

                    // Header
                    Label($"{_localize["SessionComplete"]}")
                        .H3()
                        .TextColor(theme.Primary)
                        .Center(),

                    // Stats card
                    Border(
                        VStack(spacing: 8,
                            Label($"{_localize["SessionResults"]}")
                                .H5()
                                .Center()
                                .TextColor(theme.Primary),

                            HStack(spacing: 24,
                                VStack(spacing: 4,
                                    Label($"{State.SessionGradedCount}")
                                        .H4()
                                        .TextColor(theme.GetOnBackground())
                                        .Center(),
                                    Label($"{_localize["Graded"]}")
                                        .FontSize(14)
                                        .Center()
                                        .TextColor(theme.GetOnBackground().WithAlpha(0.6f))
                                ),
                                VStack(spacing: 4,
                                    Label($"{avgAccuracy}%")
                                        .H4()
                                        .TextColor(theme.Success)
                                        .Center(),
                                    Label($"{_localize["AvgAccuracy"]}")
                                        .FontSize(14)
                                        .Center()
                                        .TextColor(theme.GetOnBackground().WithAlpha(0.6f))
                                ),
                                VStack(spacing: 4,
                                    Label($"{avgFluency}%")
                                        .H4()
                                        .TextColor(theme.Primary)
                                        .Center(),
                                    Label($"{_localize["AvgFluency"]}")
                                        .FontSize(14)
                                        .Center()
                                        .TextColor(theme.GetOnBackground().WithAlpha(0.6f))
                                )
                            ).Center()
                        )
                        .Padding(16)
                    )
                    .Class("card")
                    .Margin(0, 16),

                    // Action buttons
                    Button($"{_localize["ContinuePractice"]}")
                        .OnClicked(ContinuePractice)
                        .Class("btn-primary"),

                    Button($"{_localize["Done"]}")
                        .OnClicked(DoneWithSession)
                        .Class("btn-outline-secondary")
                        .Background(new SolidColorBrush(Colors.Transparent))
                )
                .Padding(new Thickness(16))
            )
        )
        .Background(theme.GetBackground())
        .GridRowSpan(2)
        .IsVisible(State.ShowSessionSummary);
    }

    VisualNode RenderSentenceContent()
    {
        var theme = BootstrapTheme.Current;
        return VStack(spacing: 16,
            Label()
                .Text(State.CurrentSentence)
                .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 32 : 64)
                .TextColor(theme.GetOnBackground())
                .HStart(),

            // Add vocabulary progress scoreboard
            RenderVocabularyScoreboard(),

            State.ShowFeedback ?
                Border(
                    Label(State.FeedbackMessage)
                        .FontSize(16)
                        .Padding(16)
                        .Center()
                )
                .Background(GetFeedbackBackgroundColor(State.FeedbackType))
                .StrokeShape(new RoundRectangle().CornerRadius(8))
                .StrokeThickness(0)
                .Margin(0, 8)
                : null
        )
        .GridRow(1)
        .Margin(24);
    }

    VisualNode RenderInputUI() =>
        Grid("*,*", "*,auto,auto,auto",
            State.UserMode == InputMode.MultipleChoice.ToString() ?
                RenderVocabBlocks() : null,
                RenderUserInput()
        )
        .RowSpacing(40)
        .Padding(24)
        .ColumnSpacing(16)
        .GridRow(2);

    VisualNode RenderUserInput()
    {
        var theme = BootstrapTheme.Current;
        return Border(
            Entry()
                .Class("form-control")
                .FontSize(32)
                .ReturnType(ReturnType.Go)
                .Text(State.UserInput)
                .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                .OnCompleted(GradeMe)
                .Placeholder(GetInputPlaceholder())
        )
        .Stroke(theme.GetOutline())
        .StrokeShape(new RoundRectangle().CornerRadius(6))
        .StrokeThickness(1)
        .Padding(8, 0)
        .Background(Colors.Transparent)
        .GridRow(1)
        .GridColumnSpan(4);
    }

    VisualNode RenderVocabBlocks()
    {
        var theme = BootstrapTheme.Current;
        return HStack(
            State.VocabBlocks.Select(word =>
                Button()
                    .Text(word)
                    .FontSize(DeviceInfo.Idiom == DeviceIdiom.Phone ? 18 : 24)
                    .Padding(40)
                    .Background(new SolidColorBrush(theme.GetSurface()))
                    .TextColor(theme.GetOnBackground())
                    .OnClicked(() => UseVocab(word))
            )
        )
        .Spacing(4)
        .GridRow(0)
        .GridColumnSpan(4);
    }

    VisualNode RenderProgress()
    {
        var theme = BootstrapTheme.Current;
        return HStack(
            ActivityIndicator()
                .IsRunning(State.IsBuffering)
                .IsVisible(State.IsBuffering)
                .Color(theme.GetOnBackground())
                .VCenter(),
            Label()
                .Text(State.Progress)
                .VCenter()
                .TextColor(theme.GetOnBackground())
        )
        .Spacing(8)
        .Padding(24)
        .HEnd()
        .VStart()
        .GridRowSpan(2);
    }

    VisualNode RenderBottomNavigation()
    {
        var theme = BootstrapTheme.Current;
        return Grid("1,*", "60,1,*,1,60,1,60",
            Button(State.IsBusy ? $"{_localize["Grading"]}" : "GO")
                .Class("btn-primary")
                .GridRow(1).GridColumn(4)
                .IsEnabled(!State.IsBusy)
                .OnClicked(GradeMe),

            new ModeSelector()
                .SelectedMode(State.UserMode)
                .OnSelectedModeChanged(mode => SetState(s => s.UserMode = mode))
                .GridRow(1).GridColumn(2),

            ImageButton()
                .Background(Colors.Transparent)
                .Aspect(Aspect.Center)
                .Source(BootstrapIcons.Create(BootstrapIcons.ChevronLeft, theme.GetOnBackground(), 24))
                .GridRow(1).GridColumn(0)
                .IsEnabled(_currentSentenceIndex > 0)
                .Opacity(_currentSentenceIndex > 0 ? 1.0 : 0.3)
                .OnClicked(PreviousSentence),

            ImageButton()
                .Background(Colors.Transparent)
                .Aspect(Aspect.Center)
                .Source(BootstrapIcons.Create(BootstrapIcons.ChevronRight, theme.GetOnBackground(), 24))
                .GridRow(1).GridColumn(6)
                .OnClicked(NextSentence),

            BoxView()
                .Color(theme.GetOutline())
                .HeightRequest(1)
                .GridColumnSpan(7),

            BoxView()
                .Color(theme.GetOutline())
                .WidthRequest(1)
                .GridRow(1).GridColumn(1),

            BoxView()
                .Color(theme.GetOutline())
                .WidthRequest(1)
                .GridRow(1).GridColumn(3),

            BoxView()
                .Color(theme.GetOutline())
                .WidthRequest(1)
                .GridRow(1).GridColumn(5)
        ).GridRow(1);
    }

    VisualNode RenderVocabularyScoreboard() =>
        _currentSentenceIndex >= 0 && _currentSentenceIndex < State.Sentences.Count ?
            HStack(
                State.Sentences[_currentSentenceIndex].Vocabulary?
                    .Select(word => RenderVocabularyWordStatusSync(word))
                    .ToArray() ?? Array.Empty<VisualNode>()
            )
            .Spacing(8)
            .Margin(0, 8)
            .HCenter()
            : null;

    VisualNode RenderVocabularyWordStatusSync(VocabularyWord word)
    {
        var theme = BootstrapTheme.Current;
        try
        {
            // Use a simple visual indicator for now - can be enhanced with real-time progress later
            return Border(
                    Label("◦")
                        .FontSize(16)
                        .TextColor(theme.Primary)
                        .Center()
                )
                .StrokeShape(new RoundRectangle().CornerRadius(12))
                .StrokeThickness(1)
                .Stroke(theme.Primary)
                .HeightRequest(24)
                .WidthRequest(24)
                .Background(Colors.Transparent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TranslationPage: Error rendering word status for '{TargetTerm}': {ErrorMessage}", word.TargetLanguageTerm, ex.Message);
            return Border()
                .StrokeShape(new RoundRectangle().CornerRadius(12))
                .StrokeThickness(1)
                .Stroke(theme.GetOutline())
                .HeightRequest(24)
                .WidthRequest(24)
                .Background(Colors.Transparent);
        }
    }
    VisualNode RenderPopOverLabel()
    {
        var theme = BootstrapTheme.Current;
        return Label()
            .Padding(8)
            .LineHeight(1)
            .IsVisible(false)
            .ZIndex(10)
            .FontSize(64)
            .HStart()
            .VStart()
            .Background(new SolidColorBrush(theme.GetSurface()))
            .TextColor(theme.GetOnBackground());
    }

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
            // Get target language from resource or user profile
            var targetLanguage = Props.Resource?.Language;
            if (string.IsNullOrEmpty(targetLanguage))
            {
                var profile = await _userProfileRepository.GetAsync();
                targetLanguage = profile?.TargetLanguage ?? "Korean";
            }
            SetState(s => s.TargetLanguage = targetLanguage);

            // Use the resource Id if available, or fallback to null
            var resourceId = Props.Resource?.Id ?? 0;
            _logger.LogDebug("TranslationPage: Loading sentences for resource {ResourceId}, skill {SkillId}", resourceId, Props.Skill?.Id);

            var sentences = await _translationService.GetTranslationSentences(resourceId, 2, Props.Skill.Id);
            await Task.Delay(100);

            _logger.LogDebug("TranslationPage: Received {SentenceCount} sentences from translation service", sentences?.Count ?? 0);

            if (sentences?.Any() == true)
            {
                SetState(s =>
                {
                    foreach (var sentence in sentences)
                    {
                        _logger.LogDebug("TranslationPage: Adding sentence: '{SentenceText}' -> '{RecommendedTranslation}'", sentence.SentenceText, sentence.RecommendedTranslation);
                        _logger.LogDebug("TranslationPage: Vocabulary count: {Count}", sentence.Vocabulary?.Count ?? 0);
                        if (sentence.Vocabulary?.Any() == true)
                        {
                            _logger.LogDebug("TranslationPage: Vocabulary words: [{VocabWords}]", string.Join(", ", sentence.Vocabulary.Select(v => $"{v.TargetLanguageTerm}({v.NativeLanguageTerm})")));
                        }
                        s.Sentences.Add(sentence);
                    }
                });

                SetState(s => s.IsBusy = false);

                SetCurrentSentence();

                if (State.Sentences.Count < 10)
                {
                    _logger.LogDebug("TranslationPage: Loading additional sentences in background");
                    SetState(s => s.IsBuffering = true);
                    var moreSentences = await _translationService.GetTranslationSentences(resourceId, 8, Props.Skill.Id);
                    SetState(s =>
                    {
                        foreach (var sentence in moreSentences)
                        {
                            s.Sentences.Add(sentence);
                        }
                        s.IsBuffering = false;
                    });
                }
            }
            else
            {
                _logger.LogWarning("TranslationPage: No sentences returned from translation service");
                SetState(s =>
                {
                    s.CurrentSentence = "No sentences available for this skill. Check yer resource configuration, matey!";
                    s.IsBusy = false;
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TranslationPage: Error loading sentences");
            SetState(s =>
            {
                s.CurrentSentence = $"Error loading sentences: {ex.Message}";
                s.IsBusy = false;
            });
        }
    }

    void SetCurrentSentence()
    {
        if (State.Sentences != null && State.Sentences.Count > 0 && _currentSentenceIndex < State.Sentences.Count)
        {
            SetState(s =>
            {
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

            _logger.LogDebug("TranslationPage: Set current sentence {CurrentIndex}/{TotalCount}", _currentSentenceIndex + 1, State.Sentences.Count);
            _logger.LogDebug("TranslationPage: Input mode reset to: {InputMode}", InputMode.Text);
            _logger.LogDebug("TranslationPage: Available vocabulary blocks: [{VocabBlocks}]", string.Join(", ", State.VocabBlocks));
        }
    }

    async Task GradeMe()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput))
        {
            SetState(s =>
            {
                s.FeedbackMessage = "Please enter your translation before grading.";
                s.FeedbackType = "hint";
                s.ShowFeedback = true;
            });
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        SetState(s =>
        {
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

            SetState(s =>
            {
                s.HasFeedback = true;
                s.Feedback = FormatGradeResponse(feedback);
                s.FeedbackMessage = feedbackMessage;
                s.FeedbackType = feedbackType;
                s.ShowFeedback = true;
                s.IsBusy = false;
                s.SessionGradedCount++;
                s.SessionAccuracySum += feedback.Accuracy;
                s.SessionFluencySum += feedback.Fluency;
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TranslationPage: Error in GradeMe: {ErrorMessage}", ex.Message);
            SetState(s =>
            {
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
    }
    async Task<string> BuildGradePrompt()
    {
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("GradeTranslation.scriban-txt");
        using StreamReader reader = new StreamReader(templateStream);
        var template = Template.Parse(await reader.ReadToEndAsync());
        return await template.RenderAsync(new
        {
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
        else
        {
            // All sentences completed - show session summary
            SetState(s => s.ShowSessionSummary = true);
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
            message.AppendLine("Excellent translation!");
        else if (feedback.Accuracy >= 80)
            message.AppendLine("Great work!");
        else if (feedback.Accuracy >= 70)
            message.AppendLine("Good effort!");
        else
            message.AppendLine("Keep practicing!");

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
                message.AppendLine("Perfect! Great use of vocabulary blocks!");
            else if (feedback.Accuracy >= 75)
                message.AppendLine("Excellent! Vocabulary blocks helped you succeed!");
            else if (feedback.Accuracy >= 65)
                message.AppendLine("Good effort with vocabulary blocks!");
            else
                message.AppendLine("Try different combinations with the vocabulary blocks!");
        }
        else
        {
            // Free text entry feedback
            if (feedback.Accuracy >= 90)
                message.AppendLine("Outstanding free translation!");
            else if (feedback.Accuracy >= 80)
                message.AppendLine("Excellent translation skills!");
            else if (feedback.Accuracy >= 70)
                message.AppendLine("Strong translation attempt!");
            else
                message.AppendLine("Keep developing your translation skills!");
        }

        // Add vocabulary achievement feedback
        try
        {
            var allSentenceWords = await GetAllVocabularyFromCurrentSentence();
            var usedWords = await ExtractVocabularyFromUserInput(State.UserInput, feedback);

            if (usedWords.Count == allSentenceWords.Count && allSentenceWords.Count > 0)
            {
                message.AppendLine("Amazing! You used ALL the vocabulary words!");
            }
            else if (usedWords.Count > 0)
            {
                message.AppendLine($"Great! You used {usedWords.Count} vocabulary word{(usedWords.Count > 1 ? "s" : "")}!");
            }

            // Check for conjugated forms
            if (feedback.VocabularyAnalysis?.Any(va => !string.Equals(va.DictionaryForm, va.UsedForm, StringComparison.OrdinalIgnoreCase)) == true)
            {
                message.AppendLine("Excellent work with word conjugations!");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TranslationPage: Error in vocabulary feedback: {ErrorMessage}", ex.Message);
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
            _logger.LogDebug("TranslationPage: Starting vocabulary processing for: '{UserInput}'", userInput);

            // Get ALL vocabulary words from the current sentence, not just those in user input
            var allSentenceWords = await GetAllVocabularyFromCurrentSentence();
            _logger.LogDebug("TranslationPage: Found {WordCount} vocabulary words in current sentence", allSentenceWords.Count);

            // Extract words actually used by the user
            var usedWords = await ExtractVocabularyFromUserInput(userInput, grade);
            _logger.LogDebug("TranslationPage: User used {WordCount} vocabulary words", usedWords.Count);

            // Calculate base difficulty for this translation
            var baseDifficulty = CalculateTranslationDifficulty(userInput, grade, allSentenceWords.Count);
            _logger.LogDebug("TranslationPage: Base difficulty calculated: {BaseDifficulty}", baseDifficulty);

            // Process ALL vocabulary words from the sentence
            foreach (var word in allSentenceWords)
            {
                try
                {
                    // Additional safety check - ensure word has valid ID
                    if (word.Id <= 0)
                    {
                        _logger.LogDebug("TranslationPage: ⚠️ Skipping word '{TargetTerm}' - invalid ID: {WordId}", word.TargetLanguageTerm, word.Id);
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
                        InputMode = State.UserMode, // Use the actual UserMode (MultipleChoice or Text)
                        WasCorrect = DetermineWordCorrectness(wasUsedCorrectly, grade, word),
                        ContextType = contextType,
                        UserInput = userInput,
                        ExpectedAnswer = word.NativeLanguageTerm,
                        ResponseTimeMs = responseTimeMs,
                        DifficultyWeight = wordDifficulty
                    };

                    var progress = await _progressService.RecordAttemptAsync(attempt);

                    _logger.LogDebug("TranslationPage: ✅ Recorded progress for '{TargetTerm}' (ID: {WordId}, Used: {WasUsed}, Correct: {WasCorrect}, Difficulty: {Difficulty:F2}, Context: {Context})",
                        word.TargetLanguageTerm, word.Id, wasUsedCorrectly, attempt.WasCorrect, wordDifficulty, contextType);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("TranslationPage: ❌ Error recording progress for word '{TargetTerm}' (ID: {WordId}): {ErrorMessage}", word.TargetLanguageTerm, word.Id, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TranslationPage: Error in ProcessVocabularyFromTranslation: {ErrorMessage}", ex.Message);
        }
    }

    async Task<List<VocabularyWord>> ExtractVocabularyFromUserInput(string userInput, GradeResponse grade)
    {
        try
        {
            _logger.LogDebug("TranslationPage: Extracting vocabulary from user input: '{UserInput}'", userInput);

            // Get available vocabulary from learning resources
            var resources = await _resourceRepo.GetAllResourcesAsync();
            var allVocabulary = resources.SelectMany(r => r.Vocabulary ?? new List<VocabularyWord>())
                .Where(v => v.Id > 0 && !string.IsNullOrEmpty(v.TargetLanguageTerm)) // Only valid words with IDs
                .ToList();
            _logger.LogDebug("TranslationPage: Loaded {VocabCount} valid vocabulary words from {ResourceCount} resources", allVocabulary.Count, resources.Count);

            var foundWords = new List<VocabularyWord>();

            // First, try to use AI vocabulary analysis if available
            if (grade?.VocabularyAnalysis != null && grade.VocabularyAnalysis.Any())
            {
                _logger.LogDebug("TranslationPage: Using AI vocabulary analysis - found {AnalysisCount} analyzed words", grade.VocabularyAnalysis.Count);

                foreach (var analysis in grade.VocabularyAnalysis)
                {
                    // Skip particles and invalid words
                    if (await IsValidVocabularyTerm(analysis.DictionaryForm) &&
                        await IsValidVocabularyTerm(analysis.UsedForm))
                    {
                        _logger.LogDebug("TranslationPage: Looking for dictionary form '{DictionaryForm}' (used as '{UsedForm}')", analysis.DictionaryForm, analysis.UsedForm);

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
                            _logger.LogDebug("TranslationPage: ✅ Found match for '{UsedForm}' -> '{TargetTerm}' (ID: {WordId})", analysis.UsedForm, vocabularyWord.TargetLanguageTerm, vocabularyWord.Id);
                        }
                        else
                        {
                            _logger.LogDebug("TranslationPage: ❌ No match found for '{UsedForm}' (dictionary form: '{DictionaryForm}')", analysis.UsedForm, analysis.DictionaryForm);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("TranslationPage: ⚠️ Skipping invalid vocabulary term: '{UsedForm}' (dictionary: '{DictionaryForm}')", analysis.UsedForm, analysis.DictionaryForm);
                    }
                }
            }

            // Fallback: Simple word extraction for Korean text, but filter out particles
            if (foundWords.Count == 0)
            {
                _logger.LogDebug("TranslationPage: Using fallback word extraction for Korean text");

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
                        _logger.LogDebug("TranslationPage: ✅ Fallback found match for '{Word}' -> '{TargetTerm}' (ID: {WordId})", word, vocabularyWord.TargetLanguageTerm, vocabularyWord.Id);
                    }
                }
            }

            _logger.LogDebug("TranslationPage: Final result: Found {WordCount} valid vocabulary words", foundWords.Count);
            return foundWords;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TranslationPage: Error in ExtractVocabularyFromUserInput: {ErrorMessage}", ex.Message);
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
            _logger.LogDebug("TranslationPage: Filtered out particle/function word: '{Term}'", term);
            return false;
        }

        // Additional check for pure particle words (single character Korean particles)
        if (IsKoreanText(term) && term.Length == 1)
        {
            _logger.LogDebug("TranslationPage: Filtered out single character Korean: '{Term}'", term);
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
                _logger.LogDebug("TranslationPage: Found database word: '{TargetTerm}' -> ID {WordId}", word.TargetLanguageTerm, dbWord.Id);
                return dbWord;
            }

            _logger.LogDebug("TranslationPage: No database match found for: '{TargetTerm}'", word.TargetLanguageTerm);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TranslationPage: Error looking up word '{TargetTerm}': {ErrorMessage}", word.TargetLanguageTerm, ex.Message);
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
                    _logger.LogDebug("TranslationPage: Removed particle '{Particle}' from '{Word}' -> '{CleanWord}'", particle, word, cleanWord);
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

                _logger.LogDebug("TranslationPage: Filtered vocabulary - {OriginalCount} -> {ValidCount} valid words", sentenceVocab.Count, validVocab.Count);
                return validVocab;
            }
            return new List<VocabularyWord>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("TranslationPage: Error getting sentence vocabulary: {ErrorMessage}", ex.Message);
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
            _logger.LogDebug("TranslationPage: Vocabulary blocks mode - difficulty reduced to {Difficulty:F2}", difficulty);
        }
        else
        {
            difficulty *= 1.2f; // Free text entry is harder
            _logger.LogDebug("TranslationPage: Text entry mode - difficulty increased to {Difficulty:F2}", difficulty);
        }

        // Sentence complexity based on word count
        var wordCount = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 10)
        {
            difficulty *= 1.3f;
            _logger.LogDebug("TranslationPage: Long sentence ({WordCount} words) - difficulty increased to {Difficulty:F2}", wordCount, difficulty);
        }
        else if (wordCount > 15)
        {
            difficulty *= 1.5f;
            _logger.LogDebug("TranslationPage: Very long sentence ({WordCount} words) - difficulty increased to {Difficulty:F2}", wordCount, difficulty);
        }

        // Translation quality impact on difficulty
        if (grade != null)
        {
            var qualityMultiplier = Math.Max(0.8f, (float)(grade.Accuracy / 100.0));
            difficulty *= qualityMultiplier;
            _logger.LogDebug("TranslationPage: Quality adjustment ({Accuracy}%) - difficulty adjusted to {Difficulty:F2}", grade.Accuracy, difficulty);
        }

        // Vocabulary density - more vocab words = harder
        if (vocabularyWordCount > 3)
        {
            difficulty *= 1.2f;
            _logger.LogDebug("TranslationPage: High vocabulary density ({VocabCount} words) - difficulty increased to {Difficulty:F2}", vocabularyWordCount, difficulty);
        }

        // Clamp difficulty between reasonable bounds
        var finalDifficulty = Math.Min(2.5f, Math.Max(0.5f, difficulty));
        _logger.LogDebug("TranslationPage: Final difficulty (clamped): {FinalDifficulty:F2}", finalDifficulty);

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
                _logger.LogDebug("TranslationPage: Word '{TargetTerm}' identified as Conjugated (dictionary: {DictionaryForm}, used: {UsedForm})", word.TargetLanguageTerm, analysis.DictionaryForm, analysis.UsedForm);
                return "Conjugated";
            }
        }

        // Check for grammar complexity indicators
        if (grade?.GrammarNotes?.Explanation?.ToLower().Contains("conjugation") == true ||
            grade?.GrammarNotes?.Explanation?.ToLower().Contains("verb form") == true ||
            grade?.GrammarNotes?.Explanation?.ToLower().Contains("tense") == true)
        {
            _logger.LogDebug("TranslationPage: Complex grammar detected for '{TargetTerm}'", word.TargetLanguageTerm);
            return "Complex";
        }

        // Check sentence length for complexity
        var wordCount = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 12)
        {
            _logger.LogDebug("TranslationPage: Long sentence context for '{TargetTerm}'", word.TargetLanguageTerm);
            return "Complex";
        }

        _logger.LogDebug("TranslationPage: Standard sentence context for '{TargetTerm}'", word.TargetLanguageTerm);
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
                _logger.LogDebug("TranslationPage: Conjugated context for '{TargetTerm}' - difficulty: {Difficulty:F2}", word.TargetLanguageTerm, wordDifficulty);
                break;
            case "Complex":
                wordDifficulty *= 1.4f;
                _logger.LogDebug("TranslationPage: Complex context for '{TargetTerm}' - difficulty: {Difficulty:F2}", word.TargetLanguageTerm, wordDifficulty);
                break;
            case "Sentence":
                wordDifficulty *= 1.2f;
                _logger.LogDebug("TranslationPage: Sentence context for '{TargetTerm}' - difficulty: {Difficulty:F2}", word.TargetLanguageTerm, wordDifficulty);
                break;
        }

        // Word-specific difficulty based on length/complexity
        if (word.TargetLanguageTerm?.Length > 6)
        {
            wordDifficulty *= 1.1f;
            _logger.LogDebug("TranslationPage: Long word '{TargetTerm}' - difficulty: {Difficulty:F2}", word.TargetLanguageTerm, wordDifficulty);
        }

        // Clamp final difficulty
        var finalDifficulty = Math.Min(3.0f, Math.Max(0.3f, wordDifficulty));
        _logger.LogDebug("TranslationPage: Final word difficulty for '{TargetTerm}': {FinalDifficulty:F2}", word.TargetLanguageTerm, finalDifficulty);

        return finalDifficulty;
    }

    private bool DetermineWordCorrectness(bool wasUsedByUser, GradeResponse grade, VocabularyWord word)
    {
        // If the user didn't use the word at all, it's considered incorrect for vocabulary tracking
        if (!wasUsedByUser)
        {
            _logger.LogDebug("TranslationPage: Word '{TargetTerm}' not used by user - marked incorrect", word.TargetLanguageTerm);
            return false;
        }

        // If translation accuracy is very low, consider vocabulary usage incorrect
        if (grade?.Accuracy < 50)
        {
            _logger.LogDebug("TranslationPage: Low accuracy ({Accuracy}%) - word '{TargetTerm}' marked incorrect", grade.Accuracy, word.TargetLanguageTerm);
            return false;
        }

        // For vocabulary blocks mode, be more lenient since they're guided
        if (State.UserMode == InputMode.MultipleChoice.ToString() && grade?.Accuracy >= 60)
        {
            _logger.LogDebug("TranslationPage: Vocabulary blocks mode with decent accuracy - word '{TargetTerm}' marked correct", word.TargetLanguageTerm);
            return true;
        }

        // For text entry, require higher accuracy
        if (State.UserMode == InputMode.Text.ToString() && grade?.Accuracy >= 70)
        {
            _logger.LogDebug("TranslationPage: Text entry mode with good accuracy - word '{TargetTerm}' marked correct", word.TargetLanguageTerm);
            return true;
        }

        _logger.LogDebug("TranslationPage: Word '{TargetTerm}' marked incorrect (accuracy: {Accuracy}%, mode: {UserMode})", word.TargetLanguageTerm, grade?.Accuracy, State.UserMode);
        return false;
    }

    async void ContinuePractice()
    {
        SetState(s =>
        {
            s.ShowSessionSummary = false;
            s.SessionGradedCount = 0;
            s.SessionAccuracySum = 0;
            s.SessionFluencySum = 0;
            s.Sentences.Clear();
        });
        _currentSentenceIndex = 0;
        await LoadSentences();
    }

    async void DoneWithSession()
    {
        await MauiControls.Shell.Current.GoToAsync("..");
    }

    protected override void OnMounted()
    {
        _themeService.ThemeChanged += OnThemeChanged;
        base.OnMounted();

        // Start activity timer if launched from Today's Plan
        if (Props?.FromTodaysPlan == true)
        {
            _timerService.StartSession("Translation", Props.PlanItemId);
        }

        LoadSentences();
    }

    protected override void OnWillUnmount()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        base.OnWillUnmount();

        // Pause timer when leaving activity
        if (Props?.FromTodaysPlan == true && _timerService.IsActive)
        {
            _timerService.Pause();
        }
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();
}

partial class FeedbackPanel : Component
{
    public bool IsVisible { get; set; }
    public string Feedback { get; set; }

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;
        return Border(
            VScrollView(
                VStack(
                    Label()
                        .Text(Feedback)
                        .TextColor(theme.GetOnBackground())
                        .FontSize(24)
                )
            )
        )
        .Background(new SolidColorBrush(theme.GetSurface()))
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .Padding(24)
        .IsVisible(IsVisible);
    }
}