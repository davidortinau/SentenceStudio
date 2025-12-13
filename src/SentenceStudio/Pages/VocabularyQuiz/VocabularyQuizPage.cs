using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Pages.Dashboard;
using System.Timers;
using System.Diagnostics;
using SentenceStudio.Components;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;

namespace SentenceStudio.Pages.VocabularyQuiz;

/// <summary>
/// Vocabulary Quiz Activity - Enhanced Progress Tracking System
/// 
/// USAGE CONTEXTS (CRITICAL - This page serves multiple purposes!):
/// 
/// 1. FROM DAILY PLAN (Structured Learning):
///    - Entry: Dashboard → Today's Plan → Click "Review 20 vocabulary words"
///    - Props.FromTodaysPlan = true, Props.PlanItemId = set
///    - Content: Pre-selected by DeterministicPlanBuilder (20 SRS-due words)
///    - Progress Bar: Shows "Question X of 20" (plan goal)
///    - Timer: ActivityTimerBar visible in Shell.TitleView
///    - Completion: Updates plan progress, returns to dashboard
///    - User Expectation: "I'm completing my daily vocabulary review"
/// 
/// 2. MANUAL RESOURCE SELECTION (Free Practice):
///    - Entry: Resources → Browse → Select resource → Start Vocabulary Quiz
///    - Props.FromTodaysPlan = false, Props.PlanItemId = null
///    - Content: ALL due words from selected resource(s) (SRS-filtered)
///    - Progress Bar: Shows "Question X of Y" (Y = actual due words in resource)
///    - Timer: No timer displayed
///    - Completion: Shows summary, offers continue/return options
///    - User Expectation: "I'm practicing this specific resource"
/// 
/// 3. FUTURE CONTEXTS (Update this section as new uses are added!):
///    - Study Mode: Custom session goals (e.g., "Review 50 words")
///    - Test Prep: Focus on weak areas across multiple resources
///    - Challenge Mode: Time-limited, gamified vocabulary sessions
///    - Review Mode: Revisit previously mastered content
/// 
/// IMPORTANT: When modifying this page, ensure changes work correctly for ALL contexts!
/// Test both daily plan flow AND manual resource selection before committing.
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
/// - Context-aware session sizing (plan goal vs. all due words)
/// - SRS-first word selection (excludes mastered, respects NextReviewDate)
/// 
/// Key Improvements:
/// - Uses VocabularyAttempt model for detailed attempt recording
/// - Enhanced feedback based on mastery scores vs. simple counters
/// - Backward compatible with existing 3-correct-answer thresholds
/// - Supports multiple users and learning contexts
/// - Progress bars reflect current session progress (context-aware)
/// - Clear distinction between session performance and true mastery
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

    // Session management for vocabulary rounds
    public int CurrentTurn { get; set; } = 1;
    public int MaxTurnsPerSession { get; set; } = 20;  // Match DeterministicPlanBuilder's pedagogical selection (15-20 words)
    public int ActualWordsInSession { get; set; } = 0;  // Actual words loaded for this session (may be less than max)
    public int WordsCompleted { get; set; } = 0;  // Count of unique words that have been reviewed at least once
    public bool IsSessionComplete { get; set; }

    // Term status tracking across entire learning resource
    public int NotStartedCount { get; set; } // Terms not yet included in quiz activity
    public int UnknownTermsCount { get; set; } // 0 correct answers yet (in current activity)
    public int LearningTermsCount { get; set; } // >0 correct answers but not fully learned
    public int KnownTermsCount { get; set; } // 3 MC + 3 text entry correct (across entire resource)
    public int TotalResourceTermsCount { get; set; } // Total vocabulary in learning resource

    // Vocabulary Quiz Preferences
    public VocabularyQuizPreferences UserPreferences { get; set; }
    public bool ShowPreferencesSheet { get; set; }

    // Audio playback
    public IAudioPlayer VocabularyAudioPlayer { get; set; }
}

partial class VocabularyQuizPage : Component<VocabularyQuizPageState, ActivityProps>
{
    [Inject] ILogger<VocabularyQuizPage> _logger;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] LearningResourceRepository _resourceRepo;
    [Inject] VocabularyProgressService _vocabProgressService;
    [Inject] Services.Progress.IProgressService _planProgressService;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
    [Inject] SmartResourceService _smartResourceService;
    [Inject] VocabularyQuizPreferences _preferences;
    [Inject] Plugin.Maui.Audio.IAudioManager _audioManager;
    [Inject] Services.ElevenLabsSpeechService _speechService;

    // Enhanced tracking: Response timer for measuring user response time
    private Stopwatch _responseTimer = new Stopwatch();

    LocalizationManager _localize => LocalizationManager.Instance;

    private MauiControls.ContentPage? _pageRef;
    private MauiControls.Grid? _mainGridRef;

    public override VisualNode Render()
    {
        return ContentPage(pageRef => _pageRef = pageRef,
            Grid(rows: "60,Auto,*", columns: "*",
                RenderTitleView(),// this must be present for mac catalyst especially
                LearningProgressBar(),
                ScrollView(
                    Grid(rows: "*,Auto", columns: "*",
                        TermDisplay(),
                        UserInputSection()
                    ).RowSpacing(MyTheme.ComponentSpacing)
                ).GridRow(2),
                AutoTransitionBar(),
                LoadingOverlay(),
                SessionSummaryOverlay(),

                // Preferences bottom sheet overlay
                RenderPreferencesBottomSheet()
            ).RowSpacing(MyTheme.CardMargin)
        )
        .TitleView(RenderTitleView())
        .Title($"{_localize["VocabularyQuiz"]}")
        .OnAppearing(LoadVocabulary);
    }

    private VisualNode RenderTitleView()
    {
        return Grid("*", "*,Auto,Auto",
            // Timer (if from daily plan)
            Props?.FromTodaysPlan == true ?
                Grid(mainGridRef => _mainGridRef = mainGridRef, new ActivityTimerBar())
                    .GridColumn(1)
                    .HEnd()
                    .VCenter() : null,

            // Preferences icon
            ImageButton()
                .Source(MyTheme.IconSettings)
                .OnClicked(OpenPreferences)
                .HeightRequest(32)
                .WidthRequest(32)
                .GridColumn(2)
                .HEnd()
                .VCenter()
        );
    }

    private void TrySetShellTitleView()
    {
        if (_pageRef != null && _mainGridRef != null)
        {
            _pageRef.Dispatcher.Dispatch(() =>
            {
                MauiControls.Shell.SetTitleView(_pageRef, Props?.FromTodaysPlan == true ? _mainGridRef : null);
            });
        }
    }

    VisualNode AutoTransitionBar() =>
        ProgressBar()
            .Progress(State.AutoTransitionProgress)
            .HeightRequest(4)
            .BackgroundColor(Colors.Transparent)
            .ProgressColor(MyTheme.HighlightDarkest)
            .VStart();

    VisualNode LoadingOverlay() =>
        Grid(
            Label($"{_localize["LoadingVocabulary"]}")
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

                    Label($"{_localize["ReviewVocabularyStudied"]}")
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
                            // Daily progress indicator
                            VStack(spacing: 4,
                                Label($"{_localize["TodaysVocabularyReview"]}")
                                    .FontSize(16)
                                    .FontAttributes(FontAttributes.Bold)
                                    .Center()
                                    .TextColor(MyTheme.HighlightDarkest),
                                Label($"{State.SessionSummaryItems.Count} of {State.ActualWordsInSession} words reviewed")
                                    .FontSize(12)
                                    .Center()
                                    .TextColor(MyTheme.SecondaryText)
                            )
                            .Margin(0, 0, 0, 16),

                            Label($"{_localize["SessionPerformance"]}")
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
                                    Label($"{_localize["Mastered"]}")
                                        .FontSize(12)
                                        .Center()
                                ),
                                VStack(spacing: 4,
                                    Label($"{State.SessionSummaryItems.Count(i => i.Progress?.Accuracy >= 0.5f && i.Progress?.Accuracy < 0.8f)}")
                                        .FontSize(20)
                                        .FontAttributes(FontAttributes.Bold)
                                        .TextColor(MyTheme.Warning)
                                        .Center(),
                                    Label($"{_localize["Learning"]}")
                                        .FontSize(12)
                                        .Center()
                                ),
                                VStack(spacing: 4,
                                    Label($"{State.SessionSummaryItems.Count(i => i.Progress?.Accuracy < 0.5f)}")
                                        .FontSize(20)
                                        .FontAttributes(FontAttributes.Bold)
                                        .TextColor(MyTheme.Error)
                                        .Center(),
                                    Label($"{_localize["ReviewNeeded"]}")
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

                    // Buttons - show different options based on context
                    Props.FromTodaysPlan
                        ? VStack(spacing: MyTheme.ComponentSpacing,
                            // Next Activity button (for plan mode)
                            Button($"{_localize["PlanNextActivityButton"]}")
                                .OnClicked(async () => await NavigateToNextPlanActivity())
                                .Background(MyTheme.HighlightDarkest)
                                .TextColor(Colors.White)
                                .CornerRadius(8)
                                .Padding(MyTheme.SectionSpacing, MyTheme.CardPadding)
                                .IsEnabled(IsSessionGoalMet()),

                            // Continue practicing button (secondary option)
                            Button($"{_localize["ContinueSessionButton"]}")
                                .OnClicked(() => SetState(s => s.ShowSessionSummary = false))
                                .Background(Colors.Transparent)
                                .TextColor(MyTheme.HighlightDarkest)
                                .CornerRadius(8)
                                .Padding(MyTheme.SectionSpacing, MyTheme.CardPadding / 2)
                        )
                        : Button($"{_localize["ContinueToNextSession"]}")
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
        var sessionPercentage = accuracy * 100;
        var masteryPercentage = masteryScore * 100;

        // Calculate SRS status
        var nextReview = item.Progress?.NextReviewDate ?? DateTime.Today;
        var daysSinceReview = item.Progress?.LastPracticedAt != null
            ? (DateTime.Today - item.Progress.LastPracticedAt).Days
            : 0;
        var daysUntilNext = (nextReview - DateTime.Today).Days;
        var totalAttempts = item.Progress?.TotalAttempts ?? 0;
        var isCompleted = item.Progress?.IsCompleted ?? false;

        string srsStatus = isCompleted ? "🏆 Mastered" :
                          daysUntilNext > 30 ? $"📅 Next: {daysUntilNext}d" :
                          daysUntilNext > 0 ? $"📅 Next: {daysUntilNext}d" :
                          daysUntilNext == 0 ? "📌 Due today" :
                          "📌 Overdue";

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
                        .TextColor(MyTheme.HighlightDarkest),

                    Label($"Session: {sessionPercentage:F0}% | Mastery: {masteryPercentage:F0}%")
                        .FontSize(12)
                        .TextColor(MyTheme.SecondaryDarkText),

                    Label($"{srsStatus} • {totalAttempts} attempts" + (daysSinceReview > 0 ? $" • Last: {daysSinceReview}d ago" : ""))
                        .FontSize(10)
                        .TextColor(isCompleted ? MyTheme.Success : MyTheme.SecondaryDarkText)
                )
                .HStart(),

                Label(statusIcon)
                    .FontSize(20)
                    .HEnd()
                    .VCenter()
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

    VisualNode LearningProgressBar()
    {
        return Grid(rows: "Auto", columns: "Auto,*,Auto",
            // Left bubble shows words reviewed (not turn count, which can exceed word count due to repetition)
            Border(
                Label($"{State.WordsCompleted}")
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

            // Center progress bar shows word completion (not turn count)
            ProgressBar()
                .Progress(State.ActualWordsInSession > 0 ?
                    (double)State.WordsCompleted / State.ActualWordsInSession : 0)
                .ProgressColor(MyTheme.Success)
                .BackgroundColor(Colors.LightGray)
                .HeightRequest(6)
                .GridColumn(1)
                .VCenter()
                .Margin(MyTheme.CardMargin, 0),

            // Right bubble shows THIS SESSION'S total (from plan: 20, or manual: actual due words)
            Border(
                Label($"{State.ActualWordsInSession}")
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
    }

    VisualNode TermDisplay() =>
        VStack(spacing: 16,
            Label($"{_localize["WhatIsThisInKorean"]}")
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
        .Hint(State.RequireCorrectTyping ? $"{_localize["TypeCorrectAnswerHint"]}" : $"{_localize["TypeYourAnswerHint"]}")
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
        _logger.LogDebug("📝 LoadCurrentItem: {NativeTerm} -> {TargetTerm}", item.Word.NativeLanguageTerm, item.Word.TargetLanguageTerm);

        // Stop any playing audio before loading new item
        StopAllAudio();

        // CRITICAL: Generate options FIRST before updating state
        // This prevents UI from rendering with wrong options
        string[] newOptions = Array.Empty<string>();
        if (!item.IsPromotedInQuiz)
        {
            newOptions = await GenerateMultipleChoiceOptionsSync(item);
            _logger.LogDebug("📝 Generated {OptionCount} options for {NativeTerm}", newOptions.Length, item.Word.NativeLanguageTerm);
        }

        // Reset state for new item - now includes options in same state update
        SetState(s =>
        {
            s.CurrentTerm = GetQuestionText(item.Word);
            s.CurrentTargetLanguageTerm = GetCorrectAnswer(item.Word);
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
            s.ChoiceOptions = newOptions; // CRITICAL: Set options atomically with term
        });

        // Enhanced tracking: Start response timer
        _responseTimer.Restart();

        // Play vocabulary audio if enabled
        await PlayVocabularyAudioAsync(item.Word);
    }

    // NEW: Streak-based mode determination using MasteryScore threshold
    private string GetUserModeForItem(VocabularyQuizItem item)
    {
        // CRITICAL: Check quiz-specific progress FIRST
        // If they've completed recognition in THIS quiz session, promote to production
        if (item.IsPromotedInQuiz)
        {
            _logger.LogDebug("🎯 GetUserModeForItem: {NativeTerm} promoted in quiz (streak={Streak}) → Text mode", item.Word.NativeLanguageTerm, item.QuizRecognitionStreak);
            return InputMode.Text.ToString();
        }

        // NEW: Use MasteryScore threshold instead of phase-based logic
        // MasteryScore >= 0.50 means they've demonstrated enough progress for text mode
        var masteryScore = item.Progress?.MasteryScore ?? 0f;

        if (masteryScore >= 0.50f)
        {
            // Set the recognition streak to trigger promotion automatically
            // This makes IsPromotedInQuiz return true without modifying the read-only property
            item.QuizRecognitionStreak = VocabularyQuizItem.RequiredCorrectAnswers;
            _logger.LogDebug("🎯 GetUserModeForItem: {NativeTerm} has MasteryScore={MasteryScore:F2} >= 0.50 → Text mode", item.Word.NativeLanguageTerm, masteryScore);
            return InputMode.Text.ToString();
        }

        // Otherwise start with multiple choice (building streak)
        _logger.LogDebug("🎯 GetUserModeForItem: {NativeTerm} has MasteryScore={MasteryScore:F2} < 0.50 (streak={Streak}) → MultipleChoice mode", item.Word.NativeLanguageTerm, masteryScore, item.QuizRecognitionStreak);
        return InputMode.MultipleChoice.ToString();
    }

    async Task<string[]> GenerateMultipleChoiceOptionsSync(VocabularyQuizItem currentItem)
    {
        var correctAnswer = GetCorrectAnswer(currentItem.Word);

        if (string.IsNullOrEmpty(correctAnswer))
        {
            _logger.LogWarning("⚠️ Warning: Current item {Term} has no answer in selected direction!", GetQuestionText(currentItem.Word));
            return new[] { "Error: No answer available" };
        }

        // Get words in the same language as the correct answer (opposite of question language)
        var allWords = State.VocabularyItems
            .Where(i => i != currentItem)
            .Select(i => GetCorrectAnswer(i.Word))
            .Where(term => !string.IsNullOrEmpty(term))
            .OrderBy(x => Guid.NewGuid())
            .Take(3)
            .ToList();

        // Always ensure the correct answer is included
        allWords.Add(correctAnswer);

        // Shuffle the options
        allWords = allWords.OrderBy(x => Guid.NewGuid()).ToArray().ToList();

        // Debug logging to verify correct answer is present
        _logger.LogDebug("🎯 Generated options for {Term}: {Options}", GetQuestionText(currentItem.Word), string.Join(", ", allWords));
        _logger.LogDebug("🎯 Correct answer {CorrectAnswer} is included: {IsIncluded}", correctAnswer, allWords.Contains(correctAnswer));

        return allWords.ToArray();
    }

    async Task CompleteSession()
    {
        _logger.LogInformation("✅ Session completed - Turn {CurrentTurn}/{MaxTurns}", State.CurrentTurn, State.MaxTurnsPerSession);

        // Capture vocabulary items for session summary before removing them
        var sessionItems = State.VocabularyItems.ToList();

        // Remove words that have completed BOTH recognition AND production phases in THIS quiz
        var completedTerms = State.VocabularyItems.Where(item => item.ReadyToRotateOut).ToList();
        foreach (var term in completedTerms)
        {
            State.VocabularyItems.Remove(term);
            _logger.LogDebug("Removed completed term: {NativeTerm} (MC: {MCStreak}/{MCRequired}, Text: {TextStreak}/{TextRequired})",
                term.Word.NativeLanguageTerm,
                term.QuizRecognitionStreak, VocabularyQuizItem.RequiredCorrectAnswers,
                term.QuizProductionStreak, VocabularyQuizItem.RequiredCorrectAnswers);
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
            _logger.LogInformation("🎊 Rotating out {Count} mastered words during session:", masteredWords.Count);
            foreach (var masteredWord in masteredWords)
            {
                _logger.LogDebug("  - {NativeTerm} (MC: {MCStreak}, Text: {TextStreak})", masteredWord.Word.NativeLanguageTerm, masteredWord.QuizRecognitionStreak, masteredWord.QuizProductionStreak);
                State.VocabularyItems.Remove(masteredWord);
            }

            // Add new words to replace the mastered ones
            await AddNewTermsToMaintainSet();

            // Update term counts to reflect changes
            UpdateTermCounts();

            // Show feedback to user
            if (masteredWords.Count == 1)
            {
                await AppShell.DisplayToastAsync(string.Format($"{_localize["WordMasteredNewAdded"]}", masteredWords.First().Word.NativeLanguageTerm));
            }
            else
            {
                await AppShell.DisplayToastAsync(string.Format($"{_localize["WordsMasteredNewAdded"]}", masteredWords.Count));
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

        _logger.LogDebug("🏴‍☠️ Need to add {NeededTerms} new terms to maintain set size", neededTerms);

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
            _logger.LogInformation("🏴‍☠️ No more new words available in learning resources! You've worked through all vocabulary!");
            await AppShell.DisplayToastAsync($"{_localize["AllVocabularyCompleted"]}");
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

        _logger.LogDebug("🏴‍☠️ Adding {Count} new terms (prioritizing unmastered words):", sortedWords.Count);

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
            _logger.LogDebug("  + {NativeTerm} (Global mastery: {Mastery:F0}%)", word.NativeLanguageTerm, progress.MasteryScore * 100);
        }
    }

    // 🏴‍☠️ INTELLIGENT WORD SELECTION: Prioritize learning and resume progress
    async Task<List<VocabularyWord>> SelectWordsIntelligently(List<VocabularyWord> allVocabulary)
    {
        // From plan: Use explicit target from plan for perfect alignment
        // Manual: Use default session size
        var targetSetSize = Props.FromTodaysPlan && Props.TargetWordCount.HasValue
            ? Props.TargetWordCount.Value
            : State.MaxTurnsPerSession;

        if (allVocabulary.Count <= targetSetSize)
        {
            _logger.LogDebug("🎯 Small vocabulary set ({Count} words) - using all words", allVocabulary.Count);
            return allVocabulary.OrderBy(x => Guid.NewGuid()).ToList();
        }

        // Get progress for all vocabulary words
        var allWordIds = allVocabulary.Select(w => w.Id).ToList();
        var progressDict = await _vocabProgressService.GetProgressForWordsAsync(allWordIds);

        // �‍☠️ FROM DAILY PLAN: Respect pedagogical goals, allow non-due words to reach target
        // 🎯 MANUAL SELECTION: Apply strict SRS filtering (only due words)
        List<VocabularyWord> candidateWords;

        if (Props.FromTodaysPlan)
        {
            // From plan: Exclude only completed words, allow not-yet-due words
            _logger.LogDebug("📚 FROM PLAN: Targeting {Target} words (respecting plan goal, not strict SRS)", targetSetSize);

            candidateWords = allVocabulary.Where(word =>
            {
                if (!progressDict.ContainsKey(word.Id))
                    return true; // New words always included

                var progress = progressDict[word.Id];

                // Only exclude truly mastered/completed words
                if (progress.IsCompleted)
                {
                    _logger.LogDebug("🏆 Excluding completed word: {Word}", word.NativeLanguageTerm);
                    return false;
                }

                return true; // Include all non-completed words (even if not yet due)
            }).ToList();

            _logger.LogDebug("🎯 Plan-based filter: {CandidateCount} available words from {TotalCount} total",
                candidateWords.Count, allVocabulary.Count);
        }
        else
        {
            // Manual: Strict SRS filtering (only due words)
            _logger.LogDebug("🎯 MANUAL: Applying strict SRS filtering (due words only)");

            candidateWords = allVocabulary.Where(word =>
            {
                if (!progressDict.ContainsKey(word.Id))
                    return true; // New words always included

                var progress = progressDict[word.Id];

                // Exclude truly mastered/completed words
                if (progress.IsCompleted)
                {
                    _logger.LogDebug("🏆 Excluding completed word: {Word}", word.NativeLanguageTerm);
                    return false;
                }

                // Include if due today or overdue
                var isDue = progress.NextReviewDate <= DateTime.Today;
                if (!isDue)
                {
                    _logger.LogDebug("⏰ Skipping not-yet-due word: {Word} (next: {Date:yyyy-MM-dd})",
                        word.NativeLanguageTerm, progress.NextReviewDate);
                }
                return isDue;
            }).ToList();

            _logger.LogDebug("🎯 SRS Filter: {DueCount} due words from {TotalCount} total",
                candidateWords.Count, allVocabulary.Count);
        }

        // If we have fewer candidate words than target, use what we have
        if (candidateWords.Count <= targetSetSize)
        {
            _logger.LogDebug("🎯 Using all {Count} available words (target: {Target})", candidateWords.Count, targetSetSize);
            return candidateWords.OrderBy(x => Guid.NewGuid()).ToList();
        }

        // SECOND: Categorize candidate words by mastery level for intelligent prioritization
        var unmasteredWords = new List<VocabularyWord>();
        var learningWords = new List<VocabularyWord>();
        var reviewWords = new List<VocabularyWord>();
        var masteredWords = new List<VocabularyWord>();

        // Categorize the candidate words by mastery level
        foreach (var word in candidateWords)
        {
            var progress = progressDict.ContainsKey(word.Id) ? progressDict[word.Id] : null;
            var masteryScore = progress?.MasteryScore ?? 0f;
            var isCompleted = progress?.IsCompleted ?? false;

            // Note: isCompleted words already filtered out above, but keep check for safety
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

        _logger.LogDebug("🎯 Word categorization:");
        _logger.LogDebug("  📚 Unmastered: {UnmasteredCount}", unmasteredWords.Count);
        _logger.LogDebug("  📖 Learning: {LearningCount}", learningWords.Count);
        _logger.LogDebug("  🔄 Review: {ReviewCount}", reviewWords.Count);
        _logger.LogDebug("  ✅ Mastered: {MasteredCount}", masteredWords.Count);

        // Smart selection algorithm: Prioritize learning > unmastered > review > mastered
        var selectedWords = new List<VocabularyWord>();

        // 1. First priority: Words currently being learned (have some progress but not mastered)
        var shuffledLearning = learningWords.OrderBy(x => Guid.NewGuid()).ToList();
        var learningToTake = Math.Min(6, shuffledLearning.Count); // Up to 60% learning words
        selectedWords.AddRange(shuffledLearning.Take(learningToTake));
        _logger.LogDebug("🎯 Added {Count} learning words", learningToTake);

        // 2. Second priority: Completely new words (unmastered)
        if (selectedWords.Count < targetSetSize)
        {
            var shuffledUnmastered = unmasteredWords.OrderBy(x => Guid.NewGuid()).ToList();
            var unmasteredToTake = Math.Min(targetSetSize - selectedWords.Count, shuffledUnmastered.Count);
            selectedWords.AddRange(shuffledUnmastered.Take(unmasteredToTake));
            _logger.LogDebug("🎯 Added {Count} unmastered words", unmasteredToTake);
        }

        // 3. Third priority: Review words (some attempts but low mastery)
        if (selectedWords.Count < targetSetSize)
        {
            var shuffledReview = reviewWords.OrderBy(x => Guid.NewGuid()).ToList();
            var reviewToTake = Math.Min(targetSetSize - selectedWords.Count, shuffledReview.Count);
            selectedWords.AddRange(shuffledReview.Take(reviewToTake));
            _logger.LogDebug("🎯 Added {Count} review words", reviewToTake);
        }

        // 4. Last resort: Include some mastered words if we don't have enough
        if (selectedWords.Count < targetSetSize)
        {
            var shuffledMastered = masteredWords.OrderBy(x => Guid.NewGuid()).ToList();
            var masteredToTake = targetSetSize - selectedWords.Count;
            selectedWords.AddRange(shuffledMastered.Take(masteredToTake));
            _logger.LogDebug("🎯 Added {Count} mastered words (last resort)", masteredToTake);
        }

        // Final shuffle to avoid predictable ordering
        var finalSelection = selectedWords.OrderBy(x => Guid.NewGuid()).ToList();

        _logger.LogDebug("🎯 Final selection: {Count} words", finalSelection.Count);
        _logger.LogDebug("🎯 Selected words: {Words}", string.Join(", ", finalSelection.Select(w => w.NativeLanguageTerm)));

        return finalSelection;
    }

    async Task LoadVocabulary()
    {
        // TrySetShellTitleView();

        SetState(s => s.IsBusy = true);

        try
        {
            // Debug logging
            _logger.LogDebug("VocabularyQuizPage - LoadVocabulary started");
            _logger.LogDebug("Props.Resources count: {ResourcesCount}", Props.Resources?.Count ?? 0);
            _logger.LogDebug("Props.Resource: {ResourceTitle}", Props.Resource?.Title ?? "null");

            // Refresh smart resources before loading vocabulary
            if (Props.Resources?.Any() == true)
            {
                foreach (var resourceRef in Props.Resources)
                {
                    if (resourceRef?.Id > 0)
                    {
                        var resource = await _resourceRepo.GetResourceAsync(resourceRef.Id);
                        if (resource?.IsSmartResource == true)
                        {
                            _logger.LogInformation("🔄 Refreshing smart resource: {Title}", resource.Title);
                            await _smartResourceService.RefreshSmartResourceAsync(resource.Id);
                        }
                    }
                }
            }
            else if (Props.Resource?.Id > 0)
            {
                var resource = await _resourceRepo.GetResourceAsync(Props.Resource.Id);
                if (resource?.IsSmartResource == true)
                {
                    _logger.LogInformation("🔄 Refreshing smart resource: {Title}", resource.Title);
                    await _smartResourceService.RefreshSmartResourceAsync(resource.Id);
                }
            }

            List<VocabularyWord> vocabulary = new List<VocabularyWord>();

            // Combine vocabulary from all selected resources like VocabularyMatchingPage does
            if (Props.Resources?.Any() == true)
            {
                _logger.LogDebug("Using Props.Resources with {Count} resources", Props.Resources.Count);
                foreach (var resourceRef in Props.Resources)
                {
                    _logger.LogDebug("Processing resource: {Title} (ID: {Id})", resourceRef?.Title ?? "null", resourceRef?.Id ?? -1);
                    if (resourceRef?.Id > 0)
                    {
                        var resource = await _resourceRepo.GetResourceAsync(resourceRef.Id);
                        if (resource?.Vocabulary?.Any() == true)
                        {
                            vocabulary.AddRange(resource.Vocabulary);
                            _logger.LogDebug("Added {Count} words from resource {Title}", resource.Vocabulary.Count, resource.Title);
                        }
                        else
                        {
                            _logger.LogDebug("Resource {Title} has no vocabulary", resource?.Title ?? "null");
                        }
                    }
                }
            }
            else
            {
                _logger.LogDebug("No resources provided, falling back to Props.Resource");
                // Fallback to Props.Resource for backward compatibility
                var resourceId = Props.Resource?.Id ?? 0;
                _logger.LogDebug("Fallback resource ID: {ResourceId}", resourceId);
                if (resourceId > 0)
                {
                    var resource = await _resourceRepo.GetResourceAsync(resourceId);
                    if (resource?.Vocabulary?.Any() == true)
                    {
                        vocabulary.AddRange(resource.Vocabulary);
                        _logger.LogDebug("Added {Count} words from fallback resource {Title}", resource.Vocabulary.Count, resource.Title);
                    }
                    else
                    {
                        _logger.LogDebug("Fallback resource {Title} has no vocabulary", resource?.Title ?? "null");
                    }
                }
                else
                {
                    _logger.LogDebug("No fallback resource ID available");
                }
            }

            _logger.LogDebug("Total vocabulary count: {Count}", vocabulary.Count);

            if (!vocabulary.Any())
            {
                SetState(s => s.IsBusy = false);
                _logger.LogWarning("No vocabulary found - showing alert");
                await Application.Current.MainPage.DisplayAlert(
                    $"{_localize["NoVocabulary"]}",
                    $"{_localize["NoVocabularyMessage"]}",
                    $"{_localize["OK"]}");
                return;
            }

            // 🏴‍☠️ SMART WORD SELECTION: Prioritize unmastered words and resume progress
            var smartSelectedWords = await SelectWordsIntelligently(vocabulary);
            var setSize = smartSelectedWords.Count;
            var totalSets = (int)Math.Ceiling(vocabulary.Count / (double)Math.Min(10, vocabulary.Count));

            _logger.LogDebug("🎯 Smart selection: {SelectedCount} words chosen from {TotalCount} total", smartSelectedWords.Count, vocabulary.Count);

            // Update state with actual session size (from plan: 20 words, manual: all due words)
            SetState(s => s.ActualWordsInSession = smartSelectedWords.Count);

            // Create quiz items with global progress
            var wordIds = smartSelectedWords.Select(w => w.Id).ToList();
            _logger.LogDebug("Getting progress for {Count} word IDs: [{WordIds}]", wordIds.Count, string.Join(", ", wordIds));

            try
            {
                var progressDict = await _vocabProgressService.GetProgressForWordsAsync(wordIds);
                _logger.LogDebug("Retrieved progress for {Count} words", progressDict?.Count ?? 0);

                var quizItems = smartSelectedWords.Select(word =>
                {
                    if (progressDict?.ContainsKey(word.Id) == true)
                    {
                        var progress = progressDict[word.Id];
                        _logger.LogDebug("Word {NativeTerm}: Progress exists, IsCompleted: {IsCompleted}", word.NativeLanguageTerm, progress.IsCompleted);
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
                        _logger.LogDebug("Word {NativeTerm}: No progress found, creating new", word.NativeLanguageTerm);
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

                _logger.LogDebug("Created {Count} quiz items", quizItems.Count);

                // Filter out completed words from QUIZ perspective (not global mastery)
                var incompleteItems = quizItems.Where(item => !item.ReadyToRotateOut).ToList();
                _logger.LogDebug("Found {IncompleteCount} quiz-incomplete items out of {TotalCount} total", incompleteItems.Count, quizItems.Count);

                if (!incompleteItems.Any())
                {
                    // All words are completed in THIS quiz - add more words or show completion
                    await AppShell.DisplayToastAsync($"{_localize["AllWordsCompletedAddingNew"]}");
                    // Continue with empty set to trigger new word addition in AddNewTermsToMaintainSet
                }

                // Use incomplete items for the quiz
                quizItems = incompleteItems;

                // Set first item as current
                if (quizItems.Any())
                {
                    quizItems[0].IsCurrent = true;
                    _logger.LogDebug("Set first item as current: {NativeTerm}", quizItems[0].Word.NativeLanguageTerm);
                }

                SetState(s =>
                {
                    s.VocabularyItems = new ObservableCollection<VocabularyQuizItem>(quizItems);
                    s.CurrentRound = 1;
                    s.CorrectAnswersInRound = 0;
                    s.CurrentSetNumber = 1;
                    s.TotalSets = totalSets;
                });

                _logger.LogDebug("Created {Count} quiz items", quizItems.Count);

                if (quizItems.Any())
                {
                    await LoadCurrentItem(quizItems[0]);
                }
            }
            catch (Exception progressEx)
            {
                _logger.LogError(progressEx, "Error loading progress");
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
            _logger.LogError(ex, "Error loading vocabulary");
        }
        finally
        {
            SetState(s => s.IsBusy = false);
            UpdateTermCounts(); // Initialize term counts
        }
    }

    async Task CheckAnswer()
    {
        _logger.LogDebug("🔍 CheckAnswer() START");

        var currentItem = State.VocabularyItems.FirstOrDefault(i => i.IsCurrent);
        if (currentItem == null)
        {
            _logger.LogDebug("❌ CheckAnswer: No current item found");
            return;
        }

        var answer = State.UserMode == InputMode.MultipleChoice.ToString() ?
            State.UserGuess : State.UserInput;

        if (string.IsNullOrWhiteSpace(answer))
        {
            _logger.LogDebug("❌ CheckAnswer: Answer is empty");
            return;
        }

        _logger.LogDebug("🔍 CheckAnswer: answer='{Answer}', expected='{Expected}'", answer, State.CurrentTargetLanguageTerm);

        var isCorrect = string.Equals(answer.Trim(), State.CurrentTargetLanguageTerm.Trim(),
            StringComparison.OrdinalIgnoreCase);

        _logger.LogDebug("🔍 CheckAnswer: isCorrect={IsCorrect}", isCorrect);

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
            _logger.LogDebug("💾 CheckAnswer: Saving user activity...");
            await _userActivityRepository.SaveAsync(activity);
            _logger.LogDebug("✅ CheckAnswer: User activity saved");

            // Enhanced tracking: Record answer with detailed context
            _logger.LogDebug("📊 CheckAnswer: Recording enhanced tracking...");
            await RecordAnswerWithEnhancedTracking(currentItem, isCorrect, answer);
            _logger.LogDebug("✅ CheckAnswer: Enhanced tracking recorded");

            // Update quiz-specific streak counters based on correct/incorrect answers
            _logger.LogDebug("🔄 CheckAnswer: Updating quiz progress...");
            await UpdateQuizSpecificProgress(currentItem, isCorrect);
            _logger.LogDebug("✅ CheckAnswer: Quiz progress updated");

            // Enhanced feedback: Update UI based on enhanced progress
            _logger.LogDebug("🎨 CheckAnswer: Updating UI feedback...");
            await UpdateUIBasedOnEnhancedProgress(currentItem, isCorrect);
            _logger.LogDebug("✅ CheckAnswer: UI feedback updated");

            // 🏴‍☠️ CHECK FOR IMMEDIATE MASTERY: If word is now mastered, prepare for rotation
            if (currentItem.ReadyToRotateOut)
            {
                _logger.LogDebug("🎯 Word '{NativeTerm}' is now ready to rotate out!", currentItem.Word.NativeLanguageTerm);
                // Note: Actual rotation happens in NextItem() to ensure proper flow
            }

            // Increment turn counter and update term counts
            _logger.LogDebug("🔢 CheckAnswer: Incrementing turn counter...");
            SetState(s => s.CurrentTurn++);
            UpdateTermCounts();
            _logger.LogDebug("✅ CheckAnswer: Turn counter = {Current}/{Max} (Actual session size: {Actual})",
                State.CurrentTurn, State.MaxTurnsPerSession, State.ActualWordsInSession);

            // Check for session completion based on actual session size or max turns (whichever comes first)
            // Note: CurrentTurn can exceed ActualWordsInSession because words are repeated until mastered
            var effectiveMaxTurns = State.ActualWordsInSession > 0 ? State.ActualWordsInSession : State.MaxTurnsPerSession;
            if (State.CurrentTurn > effectiveMaxTurns)
            {
                _logger.LogDebug("🏁 CheckAnswer: Session complete! (Turn {Turn} > Effective max {Max})",
                    State.CurrentTurn, effectiveMaxTurns);
                await CompleteSession();
                return;
            }

            // Check if we can proceed to next round
            _logger.LogDebug("🔄 CheckAnswer: Checking round completion...");
            await CheckRoundCompletion();
            _logger.LogDebug("✅ CheckAnswer: Round check complete");

            // Auto-advance after showing feedback
            if (State.ShowAnswer)
            {
                _logger.LogDebug("➡️ CheckAnswer: Auto-advancing to next item...");
                TransitionToNextItem();
            }
            else
            {
                _logger.LogDebug("⚠️ CheckAnswer: ShowAnswer is FALSE - not auto-advancing");
            }

            _logger.LogDebug("✅ CheckAnswer() COMPLETE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ CheckAnswer: EXCEPTION");
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
                _logger.LogDebug("🎯 Recognition streak for {NativeTerm}: {Streak}", currentItem.Word.NativeLanguageTerm, currentItem.QuizRecognitionStreak);

                if (currentItem.QuizRecognitionComplete)
                {
                    _logger.LogDebug("🎉 Recognition phase completed for {NativeTerm}! Moving to production phase.", currentItem.Word.NativeLanguageTerm);
                }
            }
            else
            {
                // Reset streak on incorrect answer
                var previousStreak = currentItem.QuizRecognitionStreak;
                currentItem.QuizRecognitionStreak = 0;
                _logger.LogDebug("❌ Recognition streak reset for {NativeTerm}: {Previous} → 0", currentItem.Word.NativeLanguageTerm, previousStreak);
            }
        }
        else if (currentMode == InputMode.Text.ToString())
        {
            // Production phase (text entry)
            if (isCorrect)
            {
                currentItem.QuizProductionStreak++;
                _logger.LogDebug("🎯 Production streak for {NativeTerm}: {Streak}", currentItem.Word.NativeLanguageTerm, currentItem.QuizProductionStreak);

                if (currentItem.QuizProductionComplete)
                {
                    _logger.LogDebug("✅ Production phase completed for {NativeTerm}! Ready to rotate out.", currentItem.Word.NativeLanguageTerm);
                }
            }
            else
            {
                // Reset streak on incorrect answer
                var previousStreak = currentItem.QuizProductionStreak;
                currentItem.QuizProductionStreak = 0;
                _logger.LogDebug("❌ Production streak reset for {NativeTerm}: {Previous} → 0", currentItem.Word.NativeLanguageTerm, previousStreak);
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
            var currentStreak = progress.CurrentStreak;
            var masteryScore = progress.MasteryScore;

            if (currentMode == InputMode.MultipleChoice.ToString())
            {
                // Multiple choice feedback - using streak-based messages
                if (masteryScore >= 0.50f)
                {
                    // Ready to promote to text entry
                    SetState(s =>
                    {
                        s.IsCorrect = true;
                        s.ShowAnswer = true;
                        s.FeedbackMessage = $"🎯 {_localize["CorrectWithStreak"].ToString().Replace("{0}", currentStreak.ToString())} Ready to type!";
                        s.CorrectAnswersInRound++;
                    });
                }
                else
                {
                    SetState(s =>
                    {
                        s.IsCorrect = true;
                        s.ShowAnswer = true;
                        s.FeedbackMessage = $"{_localize["CorrectWithStreak"].ToString().Replace("{0}", currentStreak.ToString())}";
                        s.CorrectAnswersInRound++;
                    });
                }
            }
            else if (currentMode == InputMode.Text.ToString())
            {
                // Text entry feedback - using streak-based mastery
                if (progress.IsKnown)
                {
                    SetState(s =>
                    {
                        s.IsCorrect = true;
                        s.ShowAnswer = true;
                        s.FeedbackMessage = $"{_localize["MasteredWithStreak"].ToString().Replace("{0}", currentStreak.ToString())}";
                        s.CorrectAnswersInRound++;
                    });
                    await AppShell.DisplayToastAsync($"{_localize["WordMasteredInQuiz"]}");
                }
                else
                {
                    SetState(s =>
                    {
                        s.IsCorrect = true;
                        s.ShowAnswer = true;
                        s.FeedbackMessage = $"{_localize["CorrectWithStreak"].ToString().Replace("{0}", currentStreak.ToString())}";
                        s.CorrectAnswersInRound++;
                    });
                }
            }

            // Show spaced repetition info if relevant
            if (progress.NextReviewDate.HasValue)
            {
                var nextReview = progress.NextReviewDate.Value;
                var daysUntilReview = (nextReview - DateTime.Now).Days;
                _logger.LogDebug("VocabularyQuizPage: Next review for word {WordId} in {Days} days", item.Word.Id, daysUntilReview);
            }
        }
        else
        {
            // Handle incorrect answers with streak-based feedback
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
                    s.FeedbackMessage = $"❌ {_localize["IncorrectStreakReset"]} Type the correct answer:";
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
                    s.FeedbackMessage = $"❌ {_localize["IncorrectStreakReset"]}";
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

            // Count unique words reviewed in current session
            // A word is "completed" if it has been attempted at least once (has any quiz streak)
            s.WordsCompleted = s.VocabularyItems.Count(item =>
                item.ReadyToRotateOut ||
                item.QuizRecognitionStreak > 0 ||
                item.QuizProductionStreak > 0);

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
            await AppShell.DisplayToastAsync($"{_localize["AllVocabularyCompletedInQuiz"]}");
            return;
        }

        // Shuffle items if everyone has been attempted once for variety
        if (ShouldShuffleForRoundVariety())
        {
            ShuffleIncompleteItems();
            _logger.LogDebug("Shuffled items for round variety - all items attempted once");
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

        _logger.LogDebug("Shuffled {Count} incomplete items for variety", incompleteItems.Count);
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

        AppShell.DisplayToastAsync(string.Format($"{_localize["RoundTimeForTyping"]}", State.CurrentRound));
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
                SetState(s => s.FeedbackMessage = $"{_localize["TypeCorrectAnswerExactly"]}");
                return;
            }
            else
            {
                // They typed it correctly, now move on
                SetState(s =>
                {
                    s.RequireCorrectTyping = false;
                    s.ShowCorrectAnswer = false;
                    s.FeedbackMessage = $"{_localize["GoodNowContinue"]}";
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
                await AppShell.DisplayToastAsync($"{_localize["AllVocabularyCompletedInQuizShort"]}");
                return;
            }
            else
            {
                // All incomplete items are ready to skip in current phase - advance to next phase
                _logger.LogDebug("All incomplete items ready to skip - shuffling for next round");
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

            _logger.LogDebug("Wrapped around - selecting random item needing practice");
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

    /// <summary>
    /// Check if session goals are met (either word count OR time goal).
    /// Used to enable "Next Activity" button in plan mode.
    /// </summary>
    bool IsSessionGoalMet()
    {
        // Word goal: reviewed the target number of words
        var wordGoalMet = Props.TargetWordCount.HasValue
            && State.WordsCompleted >= Props.TargetWordCount.Value;

        // Time goal: spent estimated minutes (if we have plan item)
        var timeGoalMet = false;
        if (Props.FromTodaysPlan && _timerService.IsActive)
        {
            var minutesSpent = (int)_timerService.ElapsedTime.TotalMinutes;
            // Consider 5+ minutes as reasonable session time
            timeGoalMet = minutesSpent >= 5;
        }

        return wordGoalMet || timeGoalMet;
    }

    /// <summary>
    /// Navigate to the next activity in today's plan.
    /// Called when user clicks "Next Activity" button after completing session goals.
    /// </summary>
    async Task NavigateToNextPlanActivity()
    {
        _logger.LogInformation("🎯 Navigating to next plan activity");

        // Stop the timer for current activity
        if (Props.FromTodaysPlan)
        {
            _timerService.StopSession();
        }

        // Pop back to dashboard - it will automatically show next available item
        await Navigation.PopAsync();
    }


    protected override void OnMounted()
    {
        _logger.LogDebug("🚀 VocabularyQuizPage.OnMounted() START");
        base.OnMounted();

        _logger.LogDebug("🏴‍☠️ Props.FromTodaysPlan = {FromTodaysPlan}", Props?.FromTodaysPlan);
        _logger.LogDebug("🏴‍☠️ Props.PlanItemId = {PlanItemId}", Props?.PlanItemId);

        // Start activity timer if launched from Today's Plan
        if (Props?.FromTodaysPlan == true)
        {
            _logger.LogDebug("⏱️ Starting timer session for VocabularyQuiz");
            _timerService.StartSession("VocabularyQuiz", Props.PlanItemId);
            _logger.LogDebug("✅ Timer session started - IsActive={IsActive}, IsRunning={IsRunning}", _timerService.IsActive, _timerService.IsRunning);
        }
        else
        {
            _logger.LogDebug("⚠️ NOT starting timer - FromTodaysPlan is false");
        }

        // Load user preferences
        Task.Run(async () => await LoadUserPreferencesAsync());

    }

    protected override void OnWillUnmount()
    {
        _logger.LogDebug("🛑 VocabularyQuizPage.OnWillUnmount() START");
        base.OnWillUnmount();

        // Stop all audio playback
        StopAllAudio();

        // Pause timer when leaving activity
        if (Props?.FromTodaysPlan == true && _timerService.IsActive)
        {
            _logger.LogDebug("⏱️ Pausing timer");
            _timerService.Pause();
            _logger.LogDebug("✅ Timer paused - IsRunning={IsRunning}", _timerService.IsRunning);
        }
    }

    // ============================================================================
    // VOCABULARY QUIZ PREFERENCES METHODS
    // ============================================================================

    /// <summary>
    /// Loads user preferences from VocabularyQuizPreferences service.
    /// Called in OnMounted lifecycle.
    /// </summary>
    async Task LoadUserPreferencesAsync()
    {
        await Task.Run(() =>
        {
            SetState(s => s.UserPreferences = _preferences);
            _logger.LogInformation("📋 Loaded vocabulary quiz preferences: DisplayDirection={Direction}",
                _preferences.DisplayDirection);
        });
    }

    /// <summary>
    /// Opens preferences bottom sheet overlay.
    /// </summary>
    void OpenPreferences()
    {
        _logger.LogInformation("⚙️ Opening vocabulary quiz preferences");
        SetState(s => s.ShowPreferencesSheet = true);
    }

    /// <summary>
    /// Closes preferences bottom sheet.
    /// </summary>
    void ClosePreferences()
    {
        _logger.LogInformation("⚙️ Closing vocabulary quiz preferences");
        SetState(s => s.ShowPreferencesSheet = false);
    }

    /// <summary>
    /// Callback invoked when preferences are saved.
    /// Reloads preferences to ensure state is in sync.
    /// </summary>
    void OnPreferencesSaved()
    {
        _logger.LogInformation("✅ Preferences saved, reloading");
        SetState(s => s.UserPreferences = _preferences);
    }

    /// <summary>
    /// Determines question text based on display direction preference.
    /// </summary>
    string GetQuestionText(VocabularyWord word)
    {
        if (State.UserPreferences?.DisplayDirection == "TargetToNative")
        {
            // Show target language (Korean), user answers in native (English)
            return word.TargetLanguageTerm;
        }
        else
        {
            // Show native language (English), user answers in target (Korean)
            return word.NativeLanguageTerm;
        }
    }

    /// <summary>
    /// Determines correct answer based on display direction preference.
    /// </summary>
    string GetCorrectAnswer(VocabularyWord word)
    {
        if (State.UserPreferences?.DisplayDirection == "TargetToNative")
        {
            // Question showed target language, correct answer is native
            return word.NativeLanguageTerm;
        }
        else
        {
            // Question showed native language, correct answer is target
            return word.TargetLanguageTerm;
        }
    }

    /// <summary>
    /// Renders the preferences bottom sheet overlay.
    /// Uses SfBottomSheet with IsOpen binding and OnStateChanged handler.
    /// Content is inlined to avoid component mounting issues.
    /// </summary>
    VisualNode RenderPreferencesBottomSheet()
    {
        return new SfBottomSheet(
            VStack(spacing: MyTheme.SectionSpacing,
                // Header
                HStack(spacing: MyTheme.MicroSpacing,
                    Label($"{_localize["VocabQuizPreferences"]}")
                        .ThemeKey(MyTheme.Title2)
                        .HStart()
                        .VCenter(),
                        
                    ImageButton()
                        .Source(MyTheme.IconClose)
                        .OnClicked(ClosePreferences)
                        .HeightRequest(32)
                        .WidthRequest(32)
                        .HEnd()
                ).Padding(MyTheme.LayoutSpacing),
                
                // Content
                ScrollView(
                    VStack(spacing: MyTheme.SectionSpacing,
                        RenderDisplayDirectionSection(),
                        RenderAudioPlaybackSection(),
                        
                        // Save button
                        Button($"{_localize["SavePreferences"]}")
                            .ThemeKey(MyTheme.PrimaryButton)
                            .OnClicked(SavePreferencesAsync)
                    ).Padding(MyTheme.LayoutSpacing)
                )
            )
        )
        .IsOpen(State.ShowPreferencesSheet)
        .OnStateChanged((sender, args) => SetState(s => s.ShowPreferencesSheet = !s.ShowPreferencesSheet));
    }
    
    VisualNode RenderDisplayDirectionSection() =>
        VStack(spacing: MyTheme.ComponentSpacing,
            Label($"{_localize["DisplayDirection"]}")
                .ThemeKey(MyTheme.SubHeadline),
                
            RadioButton()
                .Content($"{_localize["ShowTargetLanguage"]}")
                .IsChecked(State.UserPreferences?.DisplayDirection == "TargetToNative")
                .OnCheckedChanged((s, e) => 
                {
                    if (e.Value) 
                    {
                        if (State.UserPreferences != null)
                            State.UserPreferences.DisplayDirection = "TargetToNative";
                    }
                }),
                
            RadioButton()
                .Content($"{_localize["ShowNativeLanguage"]}")
                .IsChecked(State.UserPreferences?.DisplayDirection == "NativeToTarget")
                .OnCheckedChanged((s, e) => 
                {
                    if (e.Value)
                    {
                        if (State.UserPreferences != null)
                            State.UserPreferences.DisplayDirection = "NativeToTarget";
                    }
                })
        );
    
    VisualNode RenderAudioPlaybackSection() =>
        VStack(spacing: MyTheme.ComponentSpacing,
            Label($"{_localize["AudioPlayback"]}")
                .ThemeKey(MyTheme.SubHeadline),
                
            CheckBox()
                .IsChecked(State.UserPreferences?.AutoPlayVocabAudio ?? false)
                .OnCheckedChanged((s, e) =>
                {
                    if (State.UserPreferences != null)
                        State.UserPreferences.AutoPlayVocabAudio = e.Value;
                }),
                
            Label($"{_localize["AutoPlayVocabAudio"]}")
                .ThemeKey(MyTheme.Body1)
        );
    
    async Task SavePreferencesAsync()
    {
        _logger.LogInformation("✅ Vocabulary quiz preferences saved");
        OnPreferencesSaved();
        ClosePreferences();
    }

    // ============================================================================
    // AUDIO PLAYBACK METHODS
    // ============================================================================

    /// <summary>
    /// Plays vocabulary word audio if auto-play is enabled.
    /// Uses cached audio from StreamHistoryRepository or generates via ElevenLabsSpeechService.
    /// </summary>
    async Task PlayVocabularyAudioAsync(VocabularyWord word)
    {
        if (word == null)
        {
            _logger.LogWarning("⚠️ PlayVocabularyAudioAsync: word is null");
            return;
        }

        // Check if auto-play is enabled
        if (State.UserPreferences?.AutoPlayVocabAudio != true)
        {
            _logger.LogDebug("🎧 Auto-play vocabulary audio is disabled");
            return;
        }

        try
        {
            // Get the target language term (Korean word)
            var targetTerm = word.TargetLanguageTerm;

            if (string.IsNullOrWhiteSpace(targetTerm))
            {
                _logger.LogWarning("⚠️ PlayVocabularyAudioAsync: TargetLanguageTerm is null/empty for word ID {WordId}", word.Id);
                return;
            }

            _logger.LogInformation("🎧 Playing vocabulary audio for: {Term}", targetTerm);

            // Check if we have cached audio
            string audioUri = word.AudioPronunciationUri;

            if (string.IsNullOrWhiteSpace(audioUri))
            {
                _logger.LogDebug("🎧 No cached audio, generating via ElevenLabs for: {Term}", targetTerm);

                // Generate audio via ElevenLabs using Korean voice
                var audioStream = await _speechService.TextToSpeechAsync(
                    targetTerm,
                    "echo", // Voice ID - using default echo voice
                    0.5f,   // stability
                    0.75f,  // similarityBoost
                    1.0f    // speed
                );

                if (audioStream == null)
                {
                    _logger.LogWarning("⚠️ ElevenLabs returned null audio stream for: {Term}", targetTerm);
                    return;
                }

                // Create audio player from stream
                var player = _audioManager.CreatePlayer(audioStream);

                // Subscribe to playback ended event
                player.PlaybackEnded += OnVocabularyAudioEnded;

                SetState(s => s.VocabularyAudioPlayer = player);

                player.Play();
                _logger.LogInformation("✅ Playing generated audio for: {Term}", targetTerm);
            }
            else
            {
                _logger.LogDebug("🎧 Using cached audio from: {Uri}", audioUri);

                // Use cached audio
                var player = _audioManager.CreatePlayer(audioUri);

                // Subscribe to playback ended event
                player.PlaybackEnded += OnVocabularyAudioEnded;

                SetState(s => s.VocabularyAudioPlayer = player);

                player.Play();
                _logger.LogInformation("✅ Playing cached audio for: {Term}", targetTerm);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Exception playing vocabulary audio for: {Term}", word.TargetLanguageTerm);
        }
    }

    /// <summary>
    /// Event handler for vocabulary audio playback ended.
    /// Cleans up audio player and unsubscribes event.
    /// </summary>
    void OnVocabularyAudioEnded(object sender, EventArgs e)
    {
        _logger.LogDebug("🎧 Vocabulary audio playback ended");

        if (State.VocabularyAudioPlayer != null)
        {
            State.VocabularyAudioPlayer.PlaybackEnded -= OnVocabularyAudioEnded;
            State.VocabularyAudioPlayer.Dispose();
            SetState(s => s.VocabularyAudioPlayer = null);
        }
    }

    /// <summary>
    /// Stops all audio playback and cleans up resources.
    /// Called when navigating to next question or unmounting page.
    /// </summary>
    void StopAllAudio()
    {
        _logger.LogDebug("🎧 Stopping all audio playback");

        if (State.VocabularyAudioPlayer != null)
        {
            try
            {
                State.VocabularyAudioPlayer.PlaybackEnded -= OnVocabularyAudioEnded;
                State.VocabularyAudioPlayer.Stop();
                State.VocabularyAudioPlayer.Dispose();
                SetState(s => s.VocabularyAudioPlayer = null);
                _logger.LogDebug("✅ Audio player stopped and disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exception stopping audio player");
            }
        }
    }
}
