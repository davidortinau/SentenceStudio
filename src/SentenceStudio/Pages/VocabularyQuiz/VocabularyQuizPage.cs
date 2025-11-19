using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Pages.Dashboard;
using System.Timers;
using System.Diagnostics;
using SentenceStudio.Components;

namespace SentenceStudio.Pages.VocabularyQuiz;

/// <summary>
/// Vocabulary Quiz Activity - Enhanced Progress Tracking System
/// 
/// Learning Flow:
/// 1. Recognition Phase: Users practice multiple choice recognition until proficient
/// 2. Production Phase: Users practice text entry (typing) until proficient  
/// 3. Application Phase: Advanced contextual usage (future enhancement)
/// 
/// Enhanced Features:
/// - Activity-independent progress tracking with mastery scores (0.0-1.0)
/// - Phase-based progression (Recognition → Production → Application)
/// - Response time tracking for performance analytics
/// - Difficulty weighting based on context and word characteristics
/// - Spaced repetition scheduling for optimal review timing
/// - Rich context tracking for cross-activity learning insights
/// 
/// Key Improvements:
/// - Uses VocabularyAttempt model for detailed attempt recording
/// - Enhanced feedback based on mastery scores vs. simple counters
/// - Backward compatible with existing 3-correct-answer thresholds
/// - Supports multiple users and learning contexts
/// - Progress bars reflect overall mastery rather than just completion
/// </summary>
class VocabularyQuizPageState
{
    public bool IsBusy { get; set; }
    public bool IsBuffering { get; set; }
    public string UserInput { get; set; } = string.Empty;
    public string UserGuess { get; set; } = string.Empty;
    public string UserMode { get; set; } = InputMode.MultipleChoice.ToString();
    public string CurrentTerm { get; set; } = string.Empty;
    public string CurrentTargetLanguageTerm { get; set; } = string.Empty;
    public double AutoTransitionProgress { get; set; }
    public ObservableCollection<VocabularyQuizItem> VocabularyItems { get; set; } = new();
    public string[] ChoiceOptions { get; set; } = Array.Empty<string>();
    public bool ShowAnswer { get; set; }
    public string FeedbackMessage { get; set; } = string.Empty;
    public bool IsCorrect { get; set; }
    public bool ShowSessionSummary { get; set; }
    public List<VocabularyQuizItem> SessionSummaryItems { get; set; } = new();
    public int CurrentRound { get; set; } = 1;
    public int CorrectAnswersInRound { get; set; }
    public bool IsRoundComplete { get; set; }
    public bool RequireCorrectTyping { get; set; } // For text entry when incorrect
    public string CorrectAnswerToType { get; set; } = string.Empty;
    public int CurrentSetNumber { get; set; } = 1;
    public int TotalSets { get; set; } = 1;
    public bool ShowCorrectAnswer { get; set; }
    public bool IsAutoAdvancing { get; set; } // Show auto-advance progress

    // Session management for 10-turn rounds
    public int CurrentTurn { get; set; } = 1;
    public int MaxTurnsPerSession { get; set; } = 10;
    public bool IsSessionComplete { get; set; }

    // Term status tracking across entire learning resource
    public int NotStartedCount { get; set; } // Terms not yet included in quiz activity
    public int UnknownTermsCount { get; set; } // 0 correct answers yet (in current activity)
    public int LearningTermsCount { get; set; } // >0 correct answers but not fully learned
    public int KnownTermsCount { get; set; } // 3 MC + 3 text entry correct (across entire resource)
    public int TotalResourceTermsCount { get; set; } // Total vocabulary in learning resource
}

partial class VocabularyQuizPage : Component<VocabularyQuizPageState, ActivityProps>
{
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] VocabularyProgressService _vocabProgressService;
    [Inject] Services.Progress.IProgressService _planProgressService;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;

    // Enhanced tracking: Response timer for measuring user response time
    private Stopwatch _responseTimer = new Stopwatch();

    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["VocabularyQuiz"]}",
            Grid(rows: "60,Auto,*", columns: "*",
                Props?.FromTodaysPlan == true ? new ActivityTimerBar() : null,
                LearningProgressBar(),
                ScrollView(
                    Grid(rows: "*,Auto", columns: "*",
                        TermDisplay(),
                        UserInputSection()
                    ).RowSpacing(MyTheme.ComponentSpacing)
                ).GridRow(2),
                AutoTransitionBar(),
                LoadingOverlay(),
                SessionSummaryOverlay()
            ).RowSpacing(MyTheme.CardMargin)
        )
        .OnAppearing(LoadVocabulary);
    }

    // public override VisualNode Render()
    // {
    //     System.Diagnostics.Debug.WriteLine($"🎯 VocabularyQuizPage.Render() CALLED");
    //     System.Diagnostics.Debug.WriteLine($"🎯 Props.FromTodaysPlan = {Props?.FromTodaysPlan}");
    //     System.Diagnostics.Debug.WriteLine($"🎯 Timer service IsActive = {_timerService.IsActive}");

    //     // Main content grid
    //     var mainContent = Grid(rows: "Auto,*", columns: "*",
    //         Grid(rows: "Auto", columns: "Auto,*,Auto",
    //             // Left bubble shows learning count with enhanced status
    //             Border(
    //                 Label($"{State.LearningTermsCount}")
    //                     .FontSize(16)
    //                     .FontAttributes(FontAttributes.Bold)
    //                     .TextColor(Colors.White)
    //                     .TranslationY(-4)
    //                     .Center()
    //             )
    //             .Background(MyTheme.Success)
    //             .StrokeShape(new RoundRectangle().CornerRadius(15))
    //             .StrokeThickness(0)
    //             .HeightRequest(30)
    //             .Padding(MyTheme.Size160, 2)
    //             .GridColumn(0)
    //             .VCenter(),

    //             // Center progress bar shows overall mastery
    //             ProgressBar()
    //                 .Progress(State.TotalResourceTermsCount > 0 ?
    //                     CalculateOverallMasteryProgress() : 0)
    //                 .ProgressColor(MyTheme.Success)
    //                 .BackgroundColor(Colors.LightGray)
    //                 .HeightRequest(6)
    //                 .GridColumn(1)
    //                 .VCenter()
    //                 .Margin(MyTheme.CardMargin, 0),

    //             // Right bubble shows total count
    //             Border(
    //                 Label($"{State.TotalResourceTermsCount}")
    //                     .FontSize(16)
    //                     .FontAttributes(FontAttributes.Bold)
    //                     .TextColor(Colors.White)
    //                     .TranslationY(-4)
    //                     .Center()
    //             )
    //             .Background(MyTheme.DarkOnLightBackground)
    //             .StrokeShape(new RoundRectangle().CornerRadius(15))
    //             .StrokeThickness(0)
    //             .HeightRequest(30)
    //             .Padding(MyTheme.Size160, 2)
    //             .GridColumn(2)
    //             .VCenter()
    //         )
    //         .Margin(MyTheme.CardMargin)
    //         .GridRow(0),
    //         ScrollView(
    //             Grid(rows: "*,Auto", columns: "*",
    //                 TermDisplay(),
    //                 UserInputSection()
    //             ).RowSpacing(MyTheme.ComponentSpacing)
    //         ).GridRow(1),
    //         AutoTransitionBar(),
    //         LoadingOverlay(),
    //         SessionSummaryOverlay()
    //     ).RowSpacing(MyTheme.CardMargin);

    //     // Wrap content with timer overlay if from Today's Plan
    //     VisualNode pageContent;
    //     if (Props?.FromTodaysPlan == true)
    //     {
    //         System.Diagnostics.Debug.WriteLine("🎯 Adding timer overlay to page content");
    //         pageContent = Grid(
    //             mainContent,
    //             // Timer overlay - top right corner
    //             HStack(
    //                 new ActivityTimerBar()
    //             )
    //             .HEnd()
    //             .VStart()
    //             .Margin(16)
    //         );
    //     }
    //     else
    //     {
    //         System.Diagnostics.Debug.WriteLine("🎯 No timer overlay - FromTodaysPlan is false");
    //         pageContent = mainContent;
    //     }

    //     return ContentPage($"{_localize["VocabularyQuiz"]}", pageContent)
    //         // .Set(MauiControls.Shell.TitleViewProperty, Props?.FromTodaysPlan == true ? new ActivityTimerBar() : null)
    //         .OnAppearing(LoadVocabulary);
    // }

    VisualNode AutoTransitionBar() =>
        ProgressBar()
            .Progress(State.AutoTransitionProgress)
            .HeightRequest(4)
            .BackgroundColor(Colors.Transparent)
            .ProgressColor(MyTheme.HighlightDarkest)
            .VStart();

    VisualNode LoadingOverlay() =>
        Grid(
            Label("Loading vocabulary...")
                .FontSize(DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 32)
                .TextColor(Theme.IsLightTheme ?
                    MyTheme.DarkOnLightBackground :
                    MyTheme.LightOnDarkBackground)
                .Center()
        )
        .Background(Color.FromArgb("#80000000"))
        .GridRowSpan(3)
        .IsVisible(State.IsBusy);

    VisualNode SessionSummaryOverlay() =>
        Grid(
            ScrollView(
                VStack(spacing: MyTheme.LayoutSpacing,
                    // Header
                    Label($"📚 Session {State.CurrentSetNumber - 1} Summary")
                        .FontSize(24)
                        .FontAttributes(FontAttributes.Bold)
                        .TextColor(MyTheme.HighlightDarkest)
                        .Center(),

                    Label("Review the vocabulary you just studied:")
                        .FontSize(16)
                        .Center()
                        .TextColor(Theme.IsLightTheme ?
                            MyTheme.DarkOnLightBackground :
                            MyTheme.LightOnDarkBackground),

                    // Vocabulary list
                    VStack(spacing: 8,
                        State.SessionSummaryItems.Select(item => RenderSummaryItem(item))
                    ),

                    // Session stats
                    Border(
                        VStack(spacing: 8,
                            Label("📊 Session Statistics")
                                .FontSize(18)
                                .FontAttributes(FontAttributes.Bold)
                                .Center()
                                .TextColor(MyTheme.HighlightDarkest),

                            HStack(spacing: 20,
                                VStack(spacing: 4,
                                    Label($"{State.SessionSummaryItems.Count(i => i.Progress?.Accuracy >= 0.8f)}")
                                        .FontSize(20)
                                        .FontAttributes(FontAttributes.Bold)
                                        .TextColor(MyTheme.Success)
                                        .Center(),
                                    Label("Mastered")
                                        .FontSize(12)
                                        .Center()
                                ),
                                VStack(spacing: 4,
                                    Label($"{State.SessionSummaryItems.Count(i => i.Progress?.Accuracy >= 0.5f && i.Progress?.Accuracy < 0.8f)}")
                                        .FontSize(20)
                                        .FontAttributes(FontAttributes.Bold)
                                        .TextColor(MyTheme.Warning)
                                        .Center(),
                                    Label("Learning")
                                        .FontSize(12)
                                        .Center()
                                ),
                                VStack(spacing: 4,
                                    Label($"{State.SessionSummaryItems.Count(i => i.Progress?.Accuracy < 0.5f)}")
                                        .FontSize(20)
                                        .FontAttributes(FontAttributes.Bold)
                                        .TextColor(MyTheme.Error)
                                        .Center(),
                                    Label("Review Needed")
                                        .FontSize(12)
                                        .Center()
                                )
                            ).Center()
                        )
                        .Padding(MyTheme.LayoutSpacing)
                    )
                    .Background(Theme.IsLightTheme ?
                        MyTheme.LightSecondaryBackground :
                        MyTheme.DarkSecondaryBackground)
                    .StrokeShape(new RoundRectangle().CornerRadius(8))
                    .Margin(0, MyTheme.LayoutSpacing),

                    // Continue button
                    Button("Continue to Next Session")
                        .OnClicked(() => SetState(s => s.ShowSessionSummary = false))
                        .Background(MyTheme.HighlightDarkest)
                        .TextColor(Colors.White)
                        .CornerRadius(8)
                        .Padding(MyTheme.SectionSpacing, MyTheme.CardPadding)
                        .Margin(0, 16)
                )
                .Padding(MyTheme.LayoutPadding)
            )
        )
        .Background(Theme.IsLightTheme ?
                            MyTheme.LightBackground :
                            MyTheme.DarkBackground)
        .GridRowSpan(3)
        .IsVisible(State.ShowSessionSummary);

    VisualNode RenderSummaryItem(VocabularyQuizItem item)
    {
        var accuracy = item.Progress?.Accuracy ?? 0f;
        var masteryScore = item.Progress?.MasteryScore ?? 0f;

        Color statusColor = accuracy >= 0.8f ? MyTheme.Success :
                           accuracy >= 0.5f ? MyTheme.Warning :
                           MyTheme.Error;

        string statusIcon = accuracy >= 0.8f ? "✅" :
                           accuracy >= 0.5f ? "🔄" :
                           "❌";

        return Border(
            HStack(spacing: 12,
                Label(statusIcon)
                    .FontSize(16),

                VStack(spacing: 4,
                    Label(item.Word.NativeLanguageTerm ?? "")
                        .FontSize(16)
                        .FontAttributes(FontAttributes.Bold)
                        .TextColor(Theme.IsLightTheme ?
                            MyTheme.DarkOnLightBackground :
                            MyTheme.LightOnDarkBackground),

                    Label(item.Word.TargetLanguageTerm ?? "")
                        .FontSize(14)
                        .TextColor(MyTheme.HighlightDarkest)
                )
                .HStart(),

                VStack(spacing: 2,
                    Label($"{(int)(masteryScore * 100)}%")
                        .FontSize(14)
                        .FontAttributes(FontAttributes.Bold)
                        .TextColor(statusColor)
                        .HEnd(),

                    Label($"{item.Progress?.TotalAttempts ?? 0} attempts")
                        .FontSize(10)
                        .TextColor(MyTheme.SecondaryDarkText)
                        .HEnd()
                )
                .HEnd()
            )
            .Padding(MyTheme.CardPadding)
        )
        .Background(Theme.IsLightTheme ?
            Colors.White :
            MyTheme.DarkSecondaryBackground)
        .Stroke(statusColor.WithAlpha(0.3f))
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(6))
        .Margin(0, MyTheme.MicroSpacing);
    }

    VisualNode LearningProgressBar() =>
        Grid(rows: "Auto", columns: "Auto,*,Auto",
            // Left bubble shows learning count with enhanced status
            Border(
                Label($"{State.LearningTermsCount}")
                    .FontSize(16)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(Colors.White)
                    .TranslationY(-4)
                    .Center()
            )
            .Background(MyTheme.Success)
            .StrokeShape(new RoundRectangle().CornerRadius(15))
            .StrokeThickness(0)
            .HeightRequest(30)
            .Padding(MyTheme.Size160, 2)
            .GridColumn(0)
            .VCenter(),

            // Center progress bar shows overall mastery
            ProgressBar()
                .Progress(State.TotalResourceTermsCount > 0 ?
                    CalculateOverallMasteryProgress() : 0)
                .ProgressColor(MyTheme.Success)
                .BackgroundColor(Colors.LightGray)
                .HeightRequest(6)
                .GridColumn(1)
                .VCenter()
                .Margin(MyTheme.CardMargin, 0),

            // Right bubble shows total count
            Border(
                Label($"{State.TotalResourceTermsCount}")
                    .FontSize(16)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(Colors.White)
                    .TranslationY(-4)
                    .Center()
            )
            .Background(Colors.Gray)
            .StrokeShape(new RoundRectangle().CornerRadius(15))
            .StrokeThickness(0)
            .HeightRequest(30)
            .Padding(MyTheme.Size160, 2)
            .GridColumn(2)
            .VCenter()
        ).Padding(MyTheme.LayoutSpacing).GridRow(1);

    VisualNode TermDisplay() =>
        VStack(spacing: 16,
            Label("What is this in Korean?")
                .FontSize(18)
                .FontAttributes(FontAttributes.Bold)
                .Center(),
            Label(State.CurrentTerm)
                .FontSize(DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 32)
                .Center()
                .FontAttributes(FontAttributes.Bold),


            Label(State.CurrentTargetLanguageTerm)
                .FontSize(24)
                .Center()
                .FontAttributes(FontAttributes.Bold)
                .TextColor(MyTheme.HighlightDarkest)
                .IsVisible(State.ShowAnswer || State.ShowCorrectAnswer),
            Label(State.RequireCorrectTyping ? "Type the correct answer to continue:" : "")
                .FontSize(14)
                .Center()
                .TextColor(MyTheme.Warning)
                .IsVisible(State.RequireCorrectTyping)

        // Auto-advance countdown for multiple choice
        // Label($"Next question in {State.AutoAdvanceCountdown}...")
        //     .FontSize(14)
        //     .Center()
        //     .TextColor(MyTheme.HighlightMedium)
        //     .IsVisible(State.IsAutoAdvancing)
        )
        .Margin(MyTheme.SectionSpacing)
        .GridRow(0)
        // Allow manual advance by tapping during countdown
        .OnTapped(async () =>
        {
            if (State.IsAutoAdvancing)
            {
                SetState(s => s.IsAutoAdvancing = false);
                await NextItem();
            }
        });

    VisualNode UserInputSection() =>
        Grid(rows: "*, *", columns: "*, Auto, Auto, Auto",
            State.UserMode == InputMode.MultipleChoice.ToString() ?
                RenderMultipleChoice() :
                RenderTextInput()
        )
        .RowSpacing(DeviceInfo.Platform == DevicePlatform.WinUI ? 0 : 5)
        .Padding(DeviceInfo.Platform == DevicePlatform.WinUI ? new Thickness(30) : new Thickness(15, 0))
        .GridRow(1);

    VisualNode RenderTextInput() =>
        new SfTextInputLayout(
            Entry()
                .FontSize(32)
                .Text(State.UserInput)
                .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                .ReturnType(ReturnType.Go)
                .OnCompleted(() =>
                {
                    if (State.RequireCorrectTyping)
                        NextItem();
                    else
                        CheckAnswer();
                })
                .IsEnabled(!State.ShowAnswer || State.RequireCorrectTyping)
        )
        .Hint(State.RequireCorrectTyping ? "Type the correct answer" : "Type your answer")
        .GridRow(1)
        .GridColumn(0)
        .GridColumnSpan(DeviceInfo.Idiom == DeviceIdiom.Phone ? 4 : 1)
        .Margin(0, 0, 0, MyTheme.CardMargin);

    VisualNode RenderMultipleChoice() =>
        VStack(spacing: 8,
            State.ChoiceOptions.Select(option => RenderChoiceOption(option))
        )
        .GridRow(0);

    VisualNode RenderChoiceOption(string option)
    {
        var isSelected = State.UserGuess == option;
        var showFeedback = State.ShowAnswer;
        var isCorrect = option == State.CurrentTargetLanguageTerm;

        Color backgroundColor = Colors.Transparent;
        Color borderColor = MyTheme.Gray200;
        Color textColor = Theme.IsLightTheme ?
            MyTheme.DarkOnLightBackground :
            MyTheme.LightOnDarkBackground;

        if (showFeedback)
        {
            if (isCorrect)
            {
                backgroundColor = MyTheme.Success;
                borderColor = MyTheme.Success;
                textColor = Colors.White;
            }
            else if (isSelected && !isCorrect)
            {
                backgroundColor = MyTheme.Error;
                borderColor = MyTheme.Error;
                textColor = Colors.White;
            }
        }
        else if (isSelected)
        {
            borderColor = MyTheme.HighlightDarkest;
            backgroundColor = MyTheme.HighlightDarkest.WithAlpha(0.1f);
        }

        return Border(
            Label(option)
                .FontSize(20)
                .Center()
                .TextColor(textColor)
                .Padding(MyTheme.LayoutSpacing, MyTheme.CardPadding)
        )
        .Background(backgroundColor)
        .Stroke(borderColor)
        .StrokeThickness(2)
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .Margin(0, MyTheme.MicroSpacing)
        .OnTapped(async () =>
        {
            if (!State.ShowAnswer)
            {
                SetState(s => s.UserGuess = option);
                // Immediately grade the answer when user selects multiple choice option
                await CheckAnswer();
            }
        });
    }

    ImageSource VocabularyItemToImageSource(VocabularyQuizItem item)
    {
        if (item.IsCompleted)
            return MyTheme.IconCircleCheckmark;

        if (item.IsPromoted)
            return MyTheme.IconEdit;

        return MyTheme.IconStatus;
    }

    private double CalculateOverallMasteryProgress()
    {
        if (!State.VocabularyItems.Any()) return 0;

        var averageMastery = State.VocabularyItems
            .Where(item => item.Progress != null)
            .Average(item => item.Progress!.MasteryScore);

        return averageMastery;
    }

    Color GetItemBackgroundColor(VocabularyQuizItem item)
    {
        if (item.IsCompleted)
            return MyTheme.Success.WithAlpha(0.2f);

        if (item.IsPromoted)
            return MyTheme.Warning.WithAlpha(0.2f);

        return Colors.Transparent;
    }

    async Task JumpTo(VocabularyQuizItem item)
    {
        var currentIndex = State.VocabularyItems.IndexOf(item);
        if (currentIndex < 0) return;

        foreach (var vocabItem in State.VocabularyItems)
        {
            vocabItem.IsCurrent = false;
        }
        item.IsCurrent = true;

        await LoadCurrentItem(item);
    }

    async Task LoadCurrentItem(VocabularyQuizItem item)
    {
        // Reset state for new item
        SetState(s =>
        {
            s.CurrentTerm = item.Word.NativeLanguageTerm ?? "";
            s.CurrentTargetLanguageTerm = item.Word.TargetLanguageTerm ?? "";
            s.UserInput = "";
            s.UserGuess = "";
            s.ShowAnswer = false;
            s.ShowCorrectAnswer = false;
            s.FeedbackMessage = "";
            s.IsCorrect = false;
            s.RequireCorrectTyping = false;
            s.CorrectAnswerToType = "";
            s.UserMode = GetUserModeForItem(item); // Enhanced mode determination
            s.IsAutoAdvancing = false; // Reset auto-advance state
        });

        // Enhanced tracking: Start response timer
        _responseTimer.Restart();

        // Generate multiple choice options if needed
        if (!item.IsPromotedInQuiz)
        {
            await GenerateMultipleChoiceOptions(item);
        }
    }

    // Enhanced mode determination based on global progress across all activities
    private string GetUserModeForItem(VocabularyQuizItem item)
    {
        // Respect global progress from previous attempts across all activities
        var globalPhase = item.Progress?.CurrentPhase ?? LearningPhase.Recognition;

        // If globally in Production phase or beyond, start with text input
        if (globalPhase >= LearningPhase.Production)
        {
            // Set the recognition streak to trigger promotion automatically
            // This makes IsPromotedInQuiz return true without modifying the read-only property
            item.QuizRecognitionStreak = VocabularyQuizItem.RequiredCorrectAnswers;
            return InputMode.Text.ToString();
        }

        // Otherwise start with multiple choice (Recognition phase)
        return InputMode.MultipleChoice.ToString();
    }

    async Task GenerateMultipleChoiceOptions(VocabularyQuizItem currentItem)
    {
        var correctAnswer = currentItem.Word.TargetLanguageTerm ?? "";

        if (string.IsNullOrEmpty(correctAnswer))
        {
            System.Diagnostics.Debug.WriteLine($"❌ Warning: Current item {currentItem.Word.NativeLanguageTerm} has no target language term!");
            SetState(s => s.ChoiceOptions = new[] { "Error: No answer available" });
            return;
        }

        var allWords = State.VocabularyItems
            .Where(i => i != currentItem)
            .Select(i => i.Word.TargetLanguageTerm)
            .Where(term => !string.IsNullOrEmpty(term))
            .OrderBy(x => Guid.NewGuid())
            .Take(3)
            .ToList();

        // Always ensure the correct answer is included
        allWords.Add(correctAnswer);

        // Shuffle the options
        allWords = allWords.OrderBy(x => Guid.NewGuid()).ToArray().ToList();

        // Debug logging to verify correct answer is present
        System.Diagnostics.Debug.WriteLine($"🎯 Generated options for '{currentItem.Word.NativeLanguageTerm}': {string.Join(", ", allWords)}");
        System.Diagnostics.Debug.WriteLine($"🎯 Correct answer '{correctAnswer}' is included: {allWords.Contains(correctAnswer)}");

        SetState(s => s.ChoiceOptions = allWords.ToArray());
    }

    async Task CompleteSession()
    {
        System.Diagnostics.Debug.WriteLine($"Session completed - Turn {State.CurrentTurn}/{State.MaxTurnsPerSession}");

        // Capture vocabulary items for session summary before removing them
        var sessionItems = State.VocabularyItems.ToList();

        // Remove words that have completed BOTH recognition AND production phases in THIS quiz
        var completedTerms = State.VocabularyItems.Where(item => item.ReadyToRotateOut).ToList();
        foreach (var term in completedTerms)
        {
            State.VocabularyItems.Remove(term);
            System.Diagnostics.Debug.WriteLine($"Removed completed term: {term.Word.NativeLanguageTerm} " +
                $"(MC: {term.QuizRecognitionStreak}/{VocabularyQuizItem.RequiredCorrectAnswers}, " +
                $"Text: {term.QuizProductionStreak}/{VocabularyQuizItem.RequiredCorrectAnswers})");
        }

        // Add new terms if we need to maintain a full set
        await AddNewTermsToMaintainSet();

        // Reset session for next round
        SetState(s =>
        {
            s.CurrentTurn = 1;
            s.CurrentSetNumber++;
            s.IsSessionComplete = false;
            s.SessionSummaryItems = sessionItems; // Store session items for summary
        });

        // Shuffle all terms for randomization
        ShuffleIncompleteItems();
        UpdateTermCounts();

        // Show session summary instead of celebration
        SetState(s => s.ShowSessionSummary = true);

        // Jump to first term (for when they continue)
        var firstTerm = State.VocabularyItems.FirstOrDefault();
        if (firstTerm != null)
        {
            await JumpTo(firstTerm);
        }
    }

    // 🏴‍☠️ NEW METHOD: Immediate rotation during session for continuous word flow
    async Task RotateOutMasteredWordsAndAddNew()
    {
        // Find words that are ready to rotate out (mastered in this quiz)
        var masteredWords = State.VocabularyItems.Where(item => item.ReadyToRotateOut).ToList();

        if (masteredWords.Any())
        {
            System.Diagnostics.Debug.WriteLine($"🎊 Rotating out {masteredWords.Count} mastered words during session:");
            foreach (var masteredWord in masteredWords)
            {
                System.Diagnostics.Debug.WriteLine($"  - {masteredWord.Word.NativeLanguageTerm} (MC: {masteredWord.QuizRecognitionStreak}, Text: {masteredWord.QuizProductionStreak})");
                State.VocabularyItems.Remove(masteredWord);
            }

            // Add new words to replace the mastered ones
            await AddNewTermsToMaintainSet();

            // Update term counts to reflect changes
            UpdateTermCounts();

            // Show feedback to user
            if (masteredWords.Count == 1)
            {
                await AppShell.DisplayToastAsync($"🌟 '{masteredWords.First().Word.NativeLanguageTerm}' mastered! New word added.");
            }
            else
            {
                await AppShell.DisplayToastAsync($"🌟 {masteredWords.Count} words mastered! New words added.");
            }
        }
    }

    async Task AddNewTermsToMaintainSet()
    {
        // Target set size (can be configurable)
        int targetSetSize = 10;
        int currentCount = State.VocabularyItems.Count;
        int neededTerms = targetSetSize - currentCount;

        if (neededTerms <= 0) return;

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Need to add {neededTerms} new terms to maintain set size");

        // Get new vocabulary from resources that aren't already in the current set
        var currentWords = State.VocabularyItems.Select(item => item.Word.Id).ToHashSet();
        var availableWords = new List<VocabularyWord>();

        if (Props.Resources?.Any() == true)
        {
            foreach (var resourceRef in Props.Resources)
            {
                var resource = await _resourceRepo.GetResourceAsync(resourceRef.Id);
                if (resource?.Vocabulary?.Any() == true)
                {
                    var newWords = resource.Vocabulary
                        .Where(word => !currentWords.Contains(word.Id))
                        .ToList();
                    availableWords.AddRange(newWords);
                }
            }
        }

        if (!availableWords.Any())
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ No more new words available in learning resources! You've worked through all vocabulary!");
            await AppShell.DisplayToastAsync("🎊 Congratulations! You've worked through all vocabulary in this learning resource!");
            return;
        }

        // Prioritize words that haven't been mastered globally yet
        var wordIds = availableWords.Select(w => w.Id).ToList();
        var progressDict = await _vocabProgressService.GetProgressForWordsAsync(wordIds);

        // Sort words by mastery level - prioritize unmastered words first
        var sortedWords = availableWords
            .OrderBy(word =>
            {
                var progress = progressDict.ContainsKey(word.Id) ? progressDict[word.Id] : null;
                return progress?.MasteryScore ?? 0f; // Unmastered words (low mastery) come first
            })
            .ThenBy(x => Guid.NewGuid()) // Random within same mastery level
            .Take(neededTerms)
            .ToList();

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Adding {sortedWords.Count} new terms (prioritizing unmastered words):");

        foreach (var word in sortedWords)
        {
            var progress = progressDict.ContainsKey(word.Id) ? progressDict[word.Id] :
                new SentenceStudio.Shared.Models.VocabularyProgress
                {
                    VocabularyWordId = word.Id,
                    IsCompleted = false,
                    MasteryScore = 0.0f,
                    CurrentPhase = LearningPhase.Recognition,
                    TotalAttempts = 0,
                    CorrectAttempts = 0
                };

            var newItem = new VocabularyQuizItem
            {
                Word = word,
                IsCurrent = false,
                Progress = progress,
                // Initialize quiz-specific counters to 0 (fresh start in this quiz)
                QuizRecognitionStreak = 0,
                QuizProductionStreak = 0
            };

            State.VocabularyItems.Add(newItem);
            System.Diagnostics.Debug.WriteLine($"  + {word.NativeLanguageTerm} (Global mastery: {(progress.MasteryScore * 100):F0}%)");
        }
    }

    // 🏴‍☠️ INTELLIGENT WORD SELECTION: Prioritize learning and resume progress
    async Task<List<VocabularyWord>> SelectWordsIntelligently(List<VocabularyWord> allVocabulary)
    {
        const int targetSetSize = 10;

        if (allVocabulary.Count <= targetSetSize)
        {
            System.Diagnostics.Debug.WriteLine($"🎯 Small vocabulary set ({allVocabulary.Count} words) - using all words");
            return allVocabulary.OrderBy(x => Guid.NewGuid()).ToList();
        }

        // Get progress for all vocabulary words
        var allWordIds = allVocabulary.Select(w => w.Id).ToList();
        var progressDict = await _vocabProgressService.GetProgressForWordsAsync(allWordIds);

        // Categorize words by mastery level
        var unmasteredWords = new List<VocabularyWord>();
        var learningWords = new List<VocabularyWord>();
        var reviewWords = new List<VocabularyWord>();
        var masteredWords = new List<VocabularyWord>();

        foreach (var word in allVocabulary)
        {
            var progress = progressDict.ContainsKey(word.Id) ? progressDict[word.Id] : null;
            var masteryScore = progress?.MasteryScore ?? 0f;
            var isCompleted = progress?.IsCompleted ?? false;

            if (isCompleted || masteryScore >= 0.9f)
            {
                masteredWords.Add(word);
            }
            else if (masteryScore >= 0.5f)
            {
                learningWords.Add(word);
            }
            else if (masteryScore > 0f)
            {
                reviewWords.Add(word);
            }
            else
            {
                unmasteredWords.Add(word);
            }
        }

        System.Diagnostics.Debug.WriteLine($"🎯 Word categorization:");
        System.Diagnostics.Debug.WriteLine($"  📚 Unmastered: {unmasteredWords.Count}");
        System.Diagnostics.Debug.WriteLine($"  📖 Learning: {learningWords.Count}");
        System.Diagnostics.Debug.WriteLine($"  🔄 Review: {reviewWords.Count}");
        System.Diagnostics.Debug.WriteLine($"  ✅ Mastered: {masteredWords.Count}");

        // Smart selection algorithm: Prioritize learning > unmastered > review > mastered
        var selectedWords = new List<VocabularyWord>();

        // 1. First priority: Words currently being learned (have some progress but not mastered)
        var shuffledLearning = learningWords.OrderBy(x => Guid.NewGuid()).ToList();
        var learningToTake = Math.Min(6, shuffledLearning.Count); // Up to 60% learning words
        selectedWords.AddRange(shuffledLearning.Take(learningToTake));
        System.Diagnostics.Debug.WriteLine($"🎯 Added {learningToTake} learning words");

        // 2. Second priority: Completely new words (unmastered)
        if (selectedWords.Count < targetSetSize)
        {
            var shuffledUnmastered = unmasteredWords.OrderBy(x => Guid.NewGuid()).ToList();
            var unmasteredToTake = Math.Min(targetSetSize - selectedWords.Count, shuffledUnmastered.Count);
            selectedWords.AddRange(shuffledUnmastered.Take(unmasteredToTake));
            System.Diagnostics.Debug.WriteLine($"🎯 Added {unmasteredToTake} unmastered words");
        }

        // 3. Third priority: Review words (some attempts but low mastery)
        if (selectedWords.Count < targetSetSize)
        {
            var shuffledReview = reviewWords.OrderBy(x => Guid.NewGuid()).ToList();
            var reviewToTake = Math.Min(targetSetSize - selectedWords.Count, shuffledReview.Count);
            selectedWords.AddRange(shuffledReview.Take(reviewToTake));
            System.Diagnostics.Debug.WriteLine($"🎯 Added {reviewToTake} review words");
        }

        // 4. Last resort: Include some mastered words if we don't have enough
        if (selectedWords.Count < targetSetSize)
        {
            var shuffledMastered = masteredWords.OrderBy(x => Guid.NewGuid()).ToList();
            var masteredToTake = targetSetSize - selectedWords.Count;
            selectedWords.AddRange(shuffledMastered.Take(masteredToTake));
            System.Diagnostics.Debug.WriteLine($"🎯 Added {masteredToTake} mastered words (last resort)");
        }

        // Final shuffle to avoid predictable ordering
        var finalSelection = selectedWords.OrderBy(x => Guid.NewGuid()).ToList();

        System.Diagnostics.Debug.WriteLine($"🎯 Final selection: {finalSelection.Count} words");
        System.Diagnostics.Debug.WriteLine($"🎯 Selected words: {string.Join(", ", finalSelection.Select(w => w.NativeLanguageTerm))}");

        return finalSelection;
    }

    async Task LoadVocabulary()
    {
        SetState(s => s.IsBusy = true);

        try
        {
            // Debug logging
            System.Diagnostics.Debug.WriteLine($"VocabularyQuizPage - LoadVocabulary started");
            System.Diagnostics.Debug.WriteLine($"Props.Resources count: {Props.Resources?.Count ?? 0}");
            System.Diagnostics.Debug.WriteLine($"Props.Resource: {Props.Resource?.Title ?? "null"}");

            List<VocabularyWord> vocabulary = new List<VocabularyWord>();

            // Combine vocabulary from all selected resources like VocabularyMatchingPage does
            if (Props.Resources?.Any() == true)
            {
                System.Diagnostics.Debug.WriteLine($"Using Props.Resources with {Props.Resources.Count} resources");
                foreach (var resourceRef in Props.Resources)
                {
                    System.Diagnostics.Debug.WriteLine($"Processing resource: {resourceRef?.Title ?? "null"} (ID: {resourceRef?.Id ?? -1})");
                    if (resourceRef?.Id > 0)
                    {
                        var resource = await _resourceRepo.GetResourceAsync(resourceRef.Id);
                        if (resource?.Vocabulary?.Any() == true)
                        {
                            vocabulary.AddRange(resource.Vocabulary);
                            System.Diagnostics.Debug.WriteLine($"Added {resource.Vocabulary.Count} words from resource {resource.Title}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Resource {resource?.Title ?? "null"} has no vocabulary");
                        }
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("No resources provided, falling back to Props.Resource");
                // Fallback to Props.Resource for backward compatibility
                var resourceId = Props.Resource?.Id ?? 0;
                System.Diagnostics.Debug.WriteLine($"Fallback resource ID: {resourceId}");
                if (resourceId > 0)
                {
                    var resource = await _resourceRepo.GetResourceAsync(resourceId);
                    if (resource?.Vocabulary?.Any() == true)
                    {
                        vocabulary.AddRange(resource.Vocabulary);
                        System.Diagnostics.Debug.WriteLine($"Added {resource.Vocabulary.Count} words from fallback resource {resource.Title}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Fallback resource {resource?.Title ?? "null"} has no vocabulary");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("No fallback resource ID available");
                }
            }

            System.Diagnostics.Debug.WriteLine($"Total vocabulary count: {vocabulary.Count}");

            if (!vocabulary.Any())
            {
                SetState(s => s.IsBusy = false);
                System.Diagnostics.Debug.WriteLine("No vocabulary found - showing alert");
                await Application.Current.MainPage.DisplayAlert(
                    $"{_localize["No Vocabulary"]}",
                    $"{_localize["This resource has no vocabulary to study."]}",
                    $"{_localize["OK"]}");
                return;
            }

            // 🏴‍☠️ SMART WORD SELECTION: Prioritize unmastered words and resume progress
            var smartSelectedWords = await SelectWordsIntelligently(vocabulary);
            var setSize = smartSelectedWords.Count;
            var totalSets = (int)Math.Ceiling(vocabulary.Count / (double)Math.Min(10, vocabulary.Count));

            System.Diagnostics.Debug.WriteLine($"🎯 Smart selection: {smartSelectedWords.Count} words chosen from {vocabulary.Count} total");

            // Create quiz items with global progress
            var wordIds = smartSelectedWords.Select(w => w.Id).ToList();
            System.Diagnostics.Debug.WriteLine($"Getting progress for {wordIds.Count} word IDs: [{string.Join(", ", wordIds)}]");

            try
            {
                var progressDict = await _vocabProgressService.GetProgressForWordsAsync(wordIds);
                System.Diagnostics.Debug.WriteLine($"Retrieved progress for {progressDict?.Count ?? 0} words");

                var quizItems = smartSelectedWords.Select(word =>
                {
                    if (progressDict?.ContainsKey(word.Id) == true)
                    {
                        var progress = progressDict[word.Id];
                        System.Diagnostics.Debug.WriteLine($"Word {word.NativeLanguageTerm}: Progress exists, IsCompleted: {progress.IsCompleted}");
                        return new VocabularyQuizItem
                        {
                            Word = word,
                            IsCurrent = false,
                            Progress = progress,
                            // Initialize quiz-specific counters
                            QuizRecognitionStreak = 0,
                            QuizProductionStreak = 0
                        };
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Word {word.NativeLanguageTerm}: No progress found, creating new");
                        // Create default progress if none exists
                        var defaultProgress = new SentenceStudio.Shared.Models.VocabularyProgress
                        {
                            VocabularyWordId = word.Id,
                            IsCompleted = false,
                            MasteryScore = 0.0f,
                            CurrentPhase = LearningPhase.Recognition,
                            TotalAttempts = 0,
                            CorrectAttempts = 0
                        };
                        return new VocabularyQuizItem
                        {
                            Word = word,
                            IsCurrent = false,
                            Progress = defaultProgress,
                            // Initialize quiz-specific counters
                            QuizRecognitionStreak = 0,
                            QuizProductionStreak = 0
                        };
                    }
                }).ToList();

                System.Diagnostics.Debug.WriteLine($"Created {quizItems.Count} quiz items");

                // Filter out completed words from QUIZ perspective (not global mastery)
                var incompleteItems = quizItems.Where(item => !item.ReadyToRotateOut).ToList();
                System.Diagnostics.Debug.WriteLine($"Found {incompleteItems.Count} quiz-incomplete items out of {quizItems.Count} total");

                if (!incompleteItems.Any())
                {
                    // All words are completed in THIS quiz - add more words or show completion
                    await AppShell.DisplayToastAsync("🎊 All words in this set completed! Adding new words...");
                    // Continue with empty set to trigger new word addition in AddNewTermsToMaintainSet
                }

                // Use incomplete items for the quiz
                quizItems = incompleteItems;

                // Set first item as current
                if (quizItems.Any())
                {
                    quizItems[0].IsCurrent = true;
                    System.Diagnostics.Debug.WriteLine($"Set first item as current: {quizItems[0].Word.NativeLanguageTerm}");
                }

                SetState(s =>
                {
                    s.VocabularyItems = new ObservableCollection<VocabularyQuizItem>(quizItems);
                    s.CurrentRound = 1;
                    s.CorrectAnswersInRound = 0;
                    s.CurrentSetNumber = 1;
                    s.TotalSets = totalSets;
                });

                System.Diagnostics.Debug.WriteLine($"Created {quizItems.Count} quiz items");

                if (quizItems.Any())
                {
                    await LoadCurrentItem(quizItems[0]);
                }
            }
            catch (Exception progressEx)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading progress: {progressEx.Message}");
                // Create quiz items without progress for now
                var quizItems = smartSelectedWords.Select(word => new VocabularyQuizItem
                {
                    Word = word,
                    IsCurrent = false,
                    Progress = new SentenceStudio.Shared.Models.VocabularyProgress
                    {
                        VocabularyWordId = word.Id,
                        IsCompleted = false,
                        MasteryScore = 0.0f,
                        CurrentPhase = LearningPhase.Recognition,
                        TotalAttempts = 0,
                        CorrectAttempts = 0
                    },
                    // Initialize quiz-specific counters
                    QuizRecognitionStreak = 0,
                    QuizProductionStreak = 0
                }).ToList();

                if (quizItems.Any())
                {
                    quizItems[0].IsCurrent = true;
                    SetState(s =>
                    {
                        s.VocabularyItems = new ObservableCollection<VocabularyQuizItem>(quizItems);
                        s.CurrentRound = 1;
                        s.CorrectAnswersInRound = 0;
                        s.CurrentSetNumber = 1;
                        s.TotalSets = totalSets;
                    });

                    await LoadCurrentItem(quizItems[0]);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading vocabulary: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        finally
        {
            SetState(s => s.IsBusy = false);
            UpdateTermCounts(); // Initialize term counts
        }
    }

    async Task CheckAnswer()
    {
        System.Diagnostics.Debug.WriteLine("🔍 CheckAnswer() START");

        var currentItem = State.VocabularyItems.FirstOrDefault(i => i.IsCurrent);
        if (currentItem == null)
        {
            System.Diagnostics.Debug.WriteLine("❌ CheckAnswer: No current item found");
            return;
        }

        var answer = State.UserMode == InputMode.MultipleChoice.ToString() ?
            State.UserGuess : State.UserInput;

        if (string.IsNullOrWhiteSpace(answer))
        {
            System.Diagnostics.Debug.WriteLine("❌ CheckAnswer: Answer is empty");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"🔍 CheckAnswer: answer='{answer}', expected='{State.CurrentTargetLanguageTerm}'");

        var isCorrect = string.Equals(answer.Trim(), State.CurrentTargetLanguageTerm.Trim(),
            StringComparison.OrdinalIgnoreCase);

        System.Diagnostics.Debug.WriteLine($"🔍 CheckAnswer: isCorrect={isCorrect}");

        // Enhanced tracking: Stop response timer
        _responseTimer.Stop();

        try
        {
            // Save legacy user activity for backward compatibility
            var activity = new UserActivity
            {
                Activity = SentenceStudio.Shared.Models.Activity.VocabularyQuiz.ToString(),
                Input = answer,
                Accuracy = isCorrect ? 100 : 0,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            currentItem.UserActivity = activity;
            System.Diagnostics.Debug.WriteLine("💾 CheckAnswer: Saving user activity...");
            await _userActivityRepository.SaveAsync(activity);
            System.Diagnostics.Debug.WriteLine("✅ CheckAnswer: User activity saved");

            // Enhanced tracking: Record answer with detailed context
            System.Diagnostics.Debug.WriteLine("📊 CheckAnswer: Recording enhanced tracking...");
            await RecordAnswerWithEnhancedTracking(currentItem, isCorrect, answer);
            System.Diagnostics.Debug.WriteLine("✅ CheckAnswer: Enhanced tracking recorded");

            // Update quiz-specific streak counters based on correct/incorrect answers
            System.Diagnostics.Debug.WriteLine("🔄 CheckAnswer: Updating quiz progress...");
            await UpdateQuizSpecificProgress(currentItem, isCorrect);
            System.Diagnostics.Debug.WriteLine("✅ CheckAnswer: Quiz progress updated");

            // Enhanced feedback: Update UI based on enhanced progress
            System.Diagnostics.Debug.WriteLine("🎨 CheckAnswer: Updating UI feedback...");
            await UpdateUIBasedOnEnhancedProgress(currentItem, isCorrect);
            System.Diagnostics.Debug.WriteLine("✅ CheckAnswer: UI feedback updated");

            // 🏴‍☠️ CHECK FOR IMMEDIATE MASTERY: If word is now mastered, prepare for rotation
            if (currentItem.ReadyToRotateOut)
            {
                System.Diagnostics.Debug.WriteLine($"🎯 Word '{currentItem.Word.NativeLanguageTerm}' is now ready to rotate out!");
                // Note: Actual rotation happens in NextItem() to ensure proper flow
            }

            // Increment turn counter and update term counts
            System.Diagnostics.Debug.WriteLine("🔢 CheckAnswer: Incrementing turn counter...");
            SetState(s => s.CurrentTurn++);
            UpdateTermCounts();
            System.Diagnostics.Debug.WriteLine($"✅ CheckAnswer: Turn counter = {State.CurrentTurn}/{State.MaxTurnsPerSession}");

            // Check for session completion (10 turns)
            if (State.CurrentTurn > State.MaxTurnsPerSession)
            {
                System.Diagnostics.Debug.WriteLine("🏁 CheckAnswer: Session complete!");
                await CompleteSession();
                return;
            }

            // Check if we can proceed to next round
            System.Diagnostics.Debug.WriteLine("🔄 CheckAnswer: Checking round completion...");
            await CheckRoundCompletion();
            System.Diagnostics.Debug.WriteLine("✅ CheckAnswer: Round check complete");

            // Auto-advance after showing feedback
            if (State.ShowAnswer)
            {
                System.Diagnostics.Debug.WriteLine("➡️ CheckAnswer: Auto-advancing to next item...");
                TransitionToNextItem();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("⚠️ CheckAnswer: ShowAnswer is FALSE - not auto-advancing");
            }

            System.Diagnostics.Debug.WriteLine("✅ CheckAnswer() COMPLETE");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"❌ CheckAnswer: EXCEPTION - {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"❌ Stack trace: {ex.StackTrace}");
        }
    }

    private async Task UpdateQuizSpecificProgress(VocabularyQuizItem currentItem, bool isCorrect)
    {
        var currentMode = State.UserMode;

        if (currentMode == InputMode.MultipleChoice.ToString())
        {
            // Recognition phase (multiple choice)
            if (isCorrect)
            {
                currentItem.QuizRecognitionStreak++;
                System.Diagnostics.Debug.WriteLine($"🎯 Recognition streak for {currentItem.Word.NativeLanguageTerm}: {currentItem.QuizRecognitionStreak}");

                if (currentItem.QuizRecognitionComplete)
                {
                    System.Diagnostics.Debug.WriteLine($"🎉 Recognition phase completed for {currentItem.Word.NativeLanguageTerm}! Moving to production phase.");
                }
            }
            else
            {
                // Reset streak on incorrect answer
                var previousStreak = currentItem.QuizRecognitionStreak;
                currentItem.QuizRecognitionStreak = 0;
                System.Diagnostics.Debug.WriteLine($"❌ Recognition streak reset for {currentItem.Word.NativeLanguageTerm}: {previousStreak} → 0");
            }
        }
        else if (currentMode == InputMode.Text.ToString())
        {
            // Production phase (text entry)
            if (isCorrect)
            {
                currentItem.QuizProductionStreak++;
                System.Diagnostics.Debug.WriteLine($"🎯 Production streak for {currentItem.Word.NativeLanguageTerm}: {currentItem.QuizProductionStreak}");

                if (currentItem.QuizProductionComplete)
                {
                    System.Diagnostics.Debug.WriteLine($"🎉 Production phase completed for {currentItem.Word.NativeLanguageTerm}! Ready to rotate out.");
                }
            }
            else
            {
                // Reset streak on incorrect answer
                var previousStreak = currentItem.QuizProductionStreak;
                currentItem.QuizProductionStreak = 0;
                System.Diagnostics.Debug.WriteLine($"❌ Production streak reset for {currentItem.Word.NativeLanguageTerm}: {previousStreak} → 0");
            }
        }
    }

    // Enhanced tracking method integrated from the example
    private async Task RecordAnswerWithEnhancedTracking(
        VocabularyQuizItem currentItem,
        bool isCorrect,
        string userInput)
    {
        // Get current resource ID for context tracking
        var currentResourceId = GetCurrentResourceId();
        var inputMode = ParseInputMode(State.UserMode);

        // Determine context type based on the quiz mode and word usage
        var contextType = DetermineContextType(currentItem, inputMode);

        // Determine difficulty weight based on various factors
        var difficultyWeight = CalculateDifficultyWeight(currentItem, inputMode, contextType);

        // Create detailed attempt record
        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = currentItem.Word.Id,
            UserId = GetCurrentUserId(), // Default to 1 for now
            Activity = "VocabularyQuiz",
            InputMode = inputMode.ToString(),
            WasCorrect = isCorrect,
            DifficultyWeight = difficultyWeight,
            ContextType = contextType,
            LearningResourceId = currentResourceId,
            UserInput = userInput,
            ExpectedAnswer = GetExpectedAnswer(currentItem, inputMode),
            ResponseTimeMs = (int)_responseTimer.ElapsedMilliseconds,
            UserConfidence = GetUserConfidenceRating() // Optional: from UI slider
        };

        // Record attempt using enhanced service
        var updatedProgress = await _vocabProgressService.RecordAttemptAsync(attempt);

        // Update the quiz item with new progress
        currentItem.Progress = updatedProgress;
    }

    private InputMode ParseInputMode(string userMode)
    {
        return Enum.TryParse<InputMode>(userMode, out var mode) ? mode : InputMode.MultipleChoice;
    }

    private string DetermineContextType(VocabularyQuizItem item, InputMode inputMode)
    {
        // Enhanced context detection logic
        if (inputMode == InputMode.MultipleChoice)
            return "Isolated"; // Multiple choice is always isolated recognition

        // For text entry, check if word appears in conjugated form
        var word = item.Word;
        var expectedAnswer = GetExpectedAnswer(item, inputMode);

        if (word.TargetLanguageTerm != expectedAnswer)
            return "Conjugated"; // Word appears in different form

        return "Isolated"; // Standard text entry
    }

    private float CalculateDifficultyWeight(VocabularyQuizItem item, InputMode inputMode, string contextType)
    {
        float baseWeight = 0.5f;

        // Adjust based on input mode
        if (inputMode == InputMode.Text)
            baseWeight *= 1.2f; // Text entry is harder than multiple choice

        // Adjust based on context
        if (contextType == "Conjugated")
            baseWeight *= 1.5f; // Conjugated forms are more difficult

        // Adjust based on word characteristics (length, complexity, etc.)
        if (item.Word.TargetLanguageTerm?.Length > 10)
            baseWeight *= 1.1f; // Longer words are slightly harder

        // Adjust based on previous performance
        if (item.Progress?.Accuracy < 0.5f)
            baseWeight *= 0.9f; // Lower weight if consistently struggling

        return Math.Min(2.0f, Math.Max(0.5f, baseWeight)); // Clamp between 0.5 and 2.0
    }

    private async Task UpdateUIBasedOnEnhancedProgress(VocabularyQuizItem item, bool wasCorrect)
    {
        var progress = item.Progress;
        if (progress == null) return;

        if (wasCorrect)
        {
            var currentMode = State.UserMode;

            if (currentMode == InputMode.MultipleChoice.ToString())
            {
                // Multiple choice feedback
                if (item.QuizRecognitionComplete)
                {
                    SetState(s =>
                    {
                        s.IsCorrect = true;
                        s.ShowAnswer = true;
                        s.FeedbackMessage = $"� Perfect! Recognition mastered ({item.QuizRecognitionStreak}/{VocabularyQuizItem.RequiredCorrectAnswers})! Moving to typing practice.";
                        s.CorrectAnswersInRound++;
                    });
                }
                else
                {
                    var remaining = VocabularyQuizItem.RequiredCorrectAnswers - item.QuizRecognitionStreak;
                    SetState(s =>
                    {
                        s.IsCorrect = true;
                        s.ShowAnswer = true;
                        s.FeedbackMessage = $"✅ Correct! {remaining} more in a row to advance to typing.";
                        s.CorrectAnswersInRound++;
                    });
                }
            }
            else if (currentMode == InputMode.Text.ToString())
            {
                // Text entry feedback
                if (item.ReadyToRotateOut)
                {
                    SetState(s =>
                    {
                        s.IsCorrect = true;
                        s.ShowAnswer = true;
                        s.FeedbackMessage = $"🎊 Excellent! Word completed ({item.QuizProductionStreak}/{VocabularyQuizItem.RequiredCorrectAnswers})! Ready for new words.";
                        s.CorrectAnswersInRound++;
                    });
                    await AppShell.DisplayToastAsync("✨ Word mastered in this quiz!");
                }
                else
                {
                    var remaining = VocabularyQuizItem.RequiredCorrectAnswers - item.QuizProductionStreak;
                    SetState(s =>
                    {
                        s.IsCorrect = true;
                        s.ShowAnswer = true;
                        s.FeedbackMessage = $"✅ Great typing! {remaining} more in a row to complete this word.";
                        s.CorrectAnswersInRound++;
                    });
                }
            }

            // Show spaced repetition info if relevant
            if (progress.NextReviewDate.HasValue)
            {
                var nextReview = progress.NextReviewDate.Value;
                var daysUntilReview = (nextReview - DateTime.Now).Days;
                Debug.WriteLine($"Next review for word {item.Word.Id} in {daysUntilReview} days");
            }
        }
        else
        {
            // Handle incorrect answers with enhanced feedback
            var currentMode = State.UserMode;

            if (currentMode == InputMode.Text.ToString())
            {
                // Incorrect in text entry - require them to type the correct answer
                SetState(s =>
                {
                    s.IsCorrect = false;
                    s.ShowAnswer = false;
                    s.ShowCorrectAnswer = true;
                    s.RequireCorrectTyping = true;
                    s.CorrectAnswerToType = State.CurrentTargetLanguageTerm;
                    s.FeedbackMessage = $"❌ Incorrect. Streak reset to 0. Type the correct answer to continue:";
                    s.UserInput = ""; // Clear input for retyping
                });
            }
            else
            {
                // Incorrect in multiple choice - show correct answer with auto-advance
                SetState(s =>
                {
                    s.IsCorrect = false;
                    s.ShowAnswer = true;
                    s.FeedbackMessage = $"❌ Not quite. Streak reset to 0. The correct answer is shown above.";
                });
            }
        }
    }

    private LearningPhase GetPreviousPhase(VocabularyQuizItem item)
    {
        // This is a simplified approach - in a real implementation, you might track phase history
        var currentPhase = item.Progress?.CurrentPhase ?? LearningPhase.Recognition;
        return currentPhase switch
        {
            LearningPhase.Production => LearningPhase.Recognition,
            LearningPhase.Application => LearningPhase.Production,
            _ => LearningPhase.Recognition
        };
    }

    private float? GetUserConfidenceRating()
    {
        // This could come from a UI slider asking "How confident were you?"
        // Returning null for now to indicate no confidence rating was provided
        return null;
    }

    private int GetCurrentUserId()
    {
        // For now, return default user ID
        // In a real implementation, this would come from user authentication
        return 1;
    }

    private string GetExpectedAnswer(VocabularyQuizItem item, InputMode inputMode)
    {
        // This logic would depend on quiz configuration
        // For now, return the target language term
        return item.Word.TargetLanguageTerm ?? "";
    }

    private void TransitionToNextItem()
    {
        // Exit if already auto-advancing
        if (State.IsAutoAdvancing) return;

        SetState(s =>
        {
            s.IsAutoAdvancing = true;
            s.AutoTransitionProgress = 0.0;
        });

        // Use System.Timers.Timer for smooth progress animation
        var timer = new System.Timers.Timer(100); // Update every 100ms for smooth animation
        var startTime = DateTime.Now;
        var duration = TimeSpan.FromMilliseconds(5000); // 5-second duration to match ClozurePage

        timer.Elapsed += (sender, e) =>
        {
            var elapsed = DateTime.Now - startTime;
            var progress = Math.Min(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 1.0);

            MainThread.InvokeOnMainThreadAsync(() =>
            {
                SetState(s => s.AutoTransitionProgress = progress);
            });

            if (progress >= 1.0)
            {
                timer.Stop();
                timer.Dispose();

                MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    SetState(s =>
                    {
                        s.IsAutoAdvancing = false;
                        s.AutoTransitionProgress = 0.0;
                    });
                    await NextItem();
                });
            }
        };

        timer.Start();
    }

    async Task SkipCountdown()
    {
        if (State.IsAutoAdvancing)
        {
            SetState(s => s.IsAutoAdvancing = false);
            await NextItem();
        }
    }

    void UpdateTermCounts()
    {
        SetState(s =>
        {
            // Count terms in current activity/session
            s.UnknownTermsCount = s.VocabularyItems.Count(item => item.IsUnknown);
            s.LearningTermsCount = s.VocabularyItems.Count(item => item.IsLearning);

            // Get total terms and calculate comprehensive progress
            var totalResourceTerms = GetTotalTermsInResource();
            s.TotalResourceTermsCount = totalResourceTerms.Count;

            // Count known terms across entire resource (not just current session)
            s.KnownTermsCount = CountKnownTermsInResource(totalResourceTerms);

            // Calculate not started terms (terms not yet introduced into any activity session)
            var allActivityTerms = GetAllTermsEverInActivity(totalResourceTerms);
            s.NotStartedCount = totalResourceTerms.Count - allActivityTerms.Count;
        });
    }

    int? GetCurrentResourceId()
    {
        // Try to get the first resource ID from Props.Resources
        if (Props.Resources?.Any() == true)
        {
            return Props.Resources.First().Id;
        }

        // Fallback to Props.Resource for backward compatibility
        return Props.Resource?.Id;
    }

    List<VocabularyWord> GetTotalTermsInResource()
    {
        // Get all vocabulary terms from the current learning resources
        var allWords = new List<VocabularyWord>();

        if (Props.Resources?.Any() == true)
        {
            foreach (var resourceRef in Props.Resources)
            {
                var resource = _resourceRepo.GetResourceAsync(resourceRef.Id).Result;
                if (resource?.Vocabulary?.Any() == true)
                {
                    allWords.AddRange(resource.Vocabulary);
                }
            }
        }
        else if (Props.Resource?.Id > 0)
        {
            // Fallback to single resource
            var resource = _resourceRepo.GetResourceAsync(Props.Resource.Id).Result;
            if (resource?.Vocabulary?.Any() == true)
            {
                allWords.AddRange(resource.Vocabulary);
            }
        }

        return allWords.Distinct().ToList(); // Remove duplicates if any
    }

    int CountKnownTermsInResource(List<VocabularyWord> allTerms)
    {
        // For now, we'll count known terms based on current session progress
        // In a full implementation, you'd want to store vocabulary progress in a separate table
        // linking vocabulary word IDs to user progress across sessions

        // Currently, we can only track progress within the active session
        return State.VocabularyItems.Count(item => item.IsKnown);
    }

    List<VocabularyWord> GetAllTermsEverInActivity(List<VocabularyWord> allTerms)
    {
        // For now, we'll consider terms "in activity" if they're in the current session
        // In a full implementation, you'd query a separate table that tracks
        // which vocabulary items have been introduced across all sessions

        var currentTermIds = State.VocabularyItems.Select(item => item.Word.Id).ToHashSet();
        return allTerms.Where(term => currentTermIds.Contains(term.Id)).ToList();
    }

    bool ShouldShuffleForRoundVariety()
    {
        // Check if all incomplete items have been attempted at least once in current mode
        var incompleteItems = State.VocabularyItems.Where(i => !i.ReadyToRotateOut).ToList();

        if (incompleteItems.Count <= 2) return false; // Don't shuffle if too few items

        bool allAttempted = incompleteItems.All(i =>
            (i.IsPromotedInQuiz && i.QuizProductionStreak > 0) ||
            (!i.IsPromotedInQuiz && i.QuizRecognitionStreak > 0));

        return allAttempted;
    }

    async Task CheckRoundCompletion()
    {
        var incompleteItems = State.VocabularyItems.Where(i => !i.ReadyToRotateOut).ToList();

        if (!incompleteItems.Any())
        {
            // All items completed in quiz!
            await AppShell.DisplayToastAsync("🎊 All vocabulary completed in this quiz! Adding new words...");
            return;
        }

        // Shuffle items if everyone has been attempted once for variety
        if (ShouldShuffleForRoundVariety())
        {
            ShuffleIncompleteItems();
            System.Diagnostics.Debug.WriteLine("Shuffled items for round variety - all items attempted once");
        }

        // Check if all items ready for promotion (recognition complete -> production)
        var readyToPromote = State.VocabularyItems
            .Where(i => !i.IsPromotedInQuiz && !i.ReadyToRotateOut && i.QuizRecognitionComplete)
            .ToList();

        // If we have items ready to promote, start next round
        if (readyToPromote.Any() && AllCurrentModeItemsAttempted())
        {
            StartNextRound();
        }
    }

    bool AllCurrentModeItemsAttempted()
    {
        return State.VocabularyItems.All(i =>
            i.ReadyToRotateOut ||
            (i.IsPromotedInQuiz && i.QuizProductionStreak > 0) ||
            (!i.IsPromotedInQuiz && i.QuizRecognitionStreak > 0));
    }

    void ShuffleIncompleteItems()
    {
        // Get all incomplete items with their current positions
        var incompleteItems = State.VocabularyItems
            .Where(i => !i.ReadyToRotateOut)
            .ToList();

        if (incompleteItems.Count <= 1) return; // No need to shuffle if 1 or 0 items

        // Shuffle the incomplete items
        var shuffled = incompleteItems.OrderBy(x => Guid.NewGuid()).ToList();

        // Find completed items to keep their positions
        var completedItems = State.VocabularyItems
            .Where(i => i.ReadyToRotateOut)
            .ToList();

        // Create new list maintaining completed items but shuffling incomplete ones
        var newList = new List<VocabularyQuizItem>();
        int shuffledIndex = 0;

        foreach (var item in State.VocabularyItems)
        {
            if (item.ReadyToRotateOut)
            {
                // Keep completed items in place
                newList.Add(item);
            }
            else
            {
                // Replace with shuffled incomplete item
                if (shuffledIndex < shuffled.Count)
                {
                    newList.Add(shuffled[shuffledIndex]);
                    shuffledIndex++;
                }
            }
        }

        // Update the collection
        State.VocabularyItems.Clear();
        foreach (var item in newList)
        {
            State.VocabularyItems.Add(item);
        }

        System.Diagnostics.Debug.WriteLine($"Shuffled {incompleteItems.Count} incomplete items for variety");
    }

    void StartNextRound()
    {
        // Note: Promotion is now handled by quiz-specific progress system
        // No need to manually promote items here

        // Shuffle incomplete items for variety in the new round
        ShuffleIncompleteItems();

        SetState(s =>
        {
            s.CurrentRound++;
            s.CorrectAnswersInRound = 0;
            s.IsRoundComplete = false;
        });

        AppShell.DisplayToastAsync($"🎯 Round {State.CurrentRound} - Time for typing!");
    }

    // Add a counter to track when to shuffle for variety
    int _itemsProcessedSinceLastShuffle = 0;
    const int ShuffleAfterItems = 5; // Shuffle every 5 items for variety

    async Task NextItem()
    {

        // Handle the special case where user must type correct answer
        if (State.RequireCorrectTyping)
        {
            var isCorrectTyping = string.Equals(State.UserInput.Trim(), State.CorrectAnswerToType.Trim(),
                StringComparison.OrdinalIgnoreCase);

            if (!isCorrectTyping)
            {
                SetState(s => s.FeedbackMessage = "Please type the correct answer exactly as shown.");
                return;
            }
            else
            {
                // They typed it correctly, now move on
                SetState(s =>
                {
                    s.RequireCorrectTyping = false;
                    s.ShowCorrectAnswer = false;
                    s.FeedbackMessage = "Good! Now you can continue.";
                });
            }
        }

        // 🏴‍☠️ IMMEDIATE ROTATION: Remove mastered words and add new ones during session
        await RotateOutMasteredWordsAndAddNew();

        var currentIndex = State.VocabularyItems.IndexOf(State.VocabularyItems.FirstOrDefault(i => i.IsCurrent));
        var incompleteItems = State.VocabularyItems.Where(i => !i.ReadyToRotateOut).ToList();

        // Filter items that still need practice in current phase
        var itemsNeedingPractice = incompleteItems.Where(i => !i.IsReadyToSkipInCurrentPhase).ToList();

        if (!itemsNeedingPractice.Any())
        {
            // All items either completed or ready to skip - check if truly all completed
            if (!incompleteItems.Any())
            {
                await AppShell.DisplayToastAsync("🎊 All vocabulary completed in this quiz!");
                return;
            }
            else
            {
                // All incomplete items are ready to skip in current phase - advance to next phase
                System.Diagnostics.Debug.WriteLine("All incomplete items ready to skip - shuffling for next round");
                ShuffleIncompleteItems();
                // Reset skip status for next round
                foreach (var item in incompleteItems)
                {
                    item.IsReadyToSkipInCurrentPhase = false;
                }
                itemsNeedingPractice = incompleteItems.ToList();
            }
        }

        // Find next item that needs practice - try sequential first
        VocabularyQuizItem nextItem = null;
        for (int i = currentIndex + 1; i < State.VocabularyItems.Count; i++)
        {
            var item = State.VocabularyItems[i];
            if (!item.ReadyToRotateOut && !item.IsReadyToSkipInCurrentPhase)
            {
                nextItem = item;
                break;
            }
        }

        // If no item found after current, shuffle and pick a random one for variety
        if (nextItem == null)
        {
            // Shuffle items needing practice when wrapping around for better variety
            var shuffledNeedingPractice = itemsNeedingPractice.OrderBy(x => Guid.NewGuid()).ToList();
            nextItem = shuffledNeedingPractice.FirstOrDefault();

            System.Diagnostics.Debug.WriteLine("Wrapped around - selecting random item needing practice");
        }

        if (nextItem != null)
        {
            // Increment counter and shuffle periodically for variety
            _itemsProcessedSinceLastShuffle++;

            // Shuffle incomplete items every few transitions to keep things fresh
            if (_itemsProcessedSinceLastShuffle >= ShuffleAfterItems && itemsNeedingPractice.Count > 2)
            {
                ShuffleIncompleteItems();
                _itemsProcessedSinceLastShuffle = 0;

                // Find the next item again after shuffling
                var currentItem = State.VocabularyItems.FirstOrDefault(i => i.IsCurrent);
                if (currentItem != null)
                {
                    var newItemsNeedingPractice = State.VocabularyItems.Where(i => !i.IsCompleted && !i.IsReadyToSkipInCurrentPhase).ToList();
                    // Pick a different item than current if possible
                    nextItem = newItemsNeedingPractice.FirstOrDefault(i => i != currentItem) ?? newItemsNeedingPractice.FirstOrDefault();
                }
            }

            await JumpTo(nextItem);
        }

        _itemsProcessedSinceLastShuffle++;

        if (_itemsProcessedSinceLastShuffle >= ShuffleAfterItems)
        {
            _itemsProcessedSinceLastShuffle = 0;
            ShuffleIncompleteItems();
        }
    }

    async Task PreviousItem()
    {
        var currentIndex = State.VocabularyItems.IndexOf(State.VocabularyItems.FirstOrDefault(i => i.IsCurrent));
        var incompleteItems = State.VocabularyItems.Where(i => !i.IsCompleted).ToList();

        // Filter items that still need practice in this phase (not ready to skip)
        var itemsNeedingPractice = incompleteItems.Where(i => !i.IsReadyToSkipInCurrentPhase).ToList();

        if (!itemsNeedingPractice.Any()) return;

        // Find previous item that needs practice
        VocabularyQuizItem prevItem = null;
        for (int i = currentIndex - 1; i >= 0; i--)
        {
            var item = State.VocabularyItems[i];
            if (!item.IsCompleted && !item.IsReadyToSkipInCurrentPhase)
            {
                prevItem = item;
                break;
            }
        }

        // If no item found before current, wrap around to last item needing practice
        if (prevItem == null)
        {
            prevItem = itemsNeedingPractice.LastOrDefault();
        }

        if (prevItem != null)
        {
            await JumpTo(prevItem);
        }
    }

    protected override void OnMounted()
    {
        System.Diagnostics.Debug.WriteLine("🚀 VocabularyQuizPage.OnMounted() START");
        base.OnMounted();

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Props.FromTodaysPlan = {Props?.FromTodaysPlan}");
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ Props.PlanItemId = {Props?.PlanItemId}");

        // Start activity timer if launched from Today's Plan
        if (Props?.FromTodaysPlan == true)
        {
            System.Diagnostics.Debug.WriteLine("⏱️ Starting timer session for VocabularyQuiz");
            _timerService.StartSession("VocabularyQuiz", Props.PlanItemId);
            System.Diagnostics.Debug.WriteLine($"✅ Timer session started - IsActive={_timerService.IsActive}, IsRunning={_timerService.IsRunning}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("⚠️ NOT starting timer - FromTodaysPlan is false");
        }
    }

    protected override void OnWillUnmount()
    {
        System.Diagnostics.Debug.WriteLine("🛑 VocabularyQuizPage.OnWillUnmount() START");
        base.OnWillUnmount();

        // Pause timer when leaving activity
        if (Props?.FromTodaysPlan == true && _timerService.IsActive)
        {
            System.Diagnostics.Debug.WriteLine("⏱️ Pausing timer");
            _timerService.Pause();
            System.Diagnostics.Debug.WriteLine($"✅ Timer paused - IsRunning={_timerService.IsRunning}");
        }
    }
}
