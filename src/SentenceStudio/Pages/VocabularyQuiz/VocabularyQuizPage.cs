using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Pages.Dashboard;
using System.Timers;
using System.Diagnostics;
using SentenceStudio.Components;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;
using System.IO;
using SentenceStudio.Shared.Services;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;
using SentenceStudio.Services;

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
    public bool IsCardTransitioning { get; set; } // Card flip animation state

    // ========================================================================
    // ROUND-BASED SESSION MANAGEMENT
    // ========================================================================
    // Session structure: 10 active words, 10 turns per round (1 per word)
    // Each round: present each word once in randomized order
    // On word mastery: word removed, replacement queued for NEXT round
    // Session ends: pool exhausted + all active words mastered
    // ========================================================================

    public const int ActiveWordCount = 10;        // Max active words at any time
    public const int TurnsPerRound = 10;          // One turn per active word

    public int CurrentTurnInRound { get; set; } = 0;  // 0-based index into RoundWordOrder
    public List<VocabularyQuizItem> RoundWordOrder { get; set; } = new(); // Shuffled items for current round (references, not indices)
    public List<VocabularyQuizItem> PendingReplacements { get; set; } = new(); // Words to add at next round start

    // Session statistics
    public int RoundsCompleted { get; set; } = 0;
    public int WordsMasteredThisSession { get; set; } = 0;
    public int TotalTurnsCompleted { get; set; } = 0;

    // Term status tracking across entire learning resource
    public int NotStartedCount { get; set; } // Terms not yet included in quiz activity
    public int UnknownTermsCount { get; set; } // 0 correct answers yet (in current activity)
    public int LearningTermsCount { get; set; } // >0 correct answers but not fully learned
    public int KnownTermsCount { get; set; } // 3 MC + 3 text entry correct (across entire resource)
    public int TotalResourceTermsCount { get; set; } // Total vocabulary in learning resource

    // Target language for the quiz (from resource)
    public string TargetLanguage { get; set; } = "the target language";

    // Vocabulary Quiz Preferences
    public VocabularyQuizPreferences UserPreferences { get; set; }

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
    [Inject] SpeechVoicePreferences _speechVoicePreferences;
    [Inject] Plugin.Maui.Audio.IAudioManager _audioManager;
    [Inject] Services.ElevenLabsSpeechService _speechService;
    [Inject] StreamHistoryRepository _historyRepo;
    [Inject] NativeThemeService _themeService;

    // Enhanced tracking: Response timer for measuring user response time
    private Stopwatch _responseTimer = new Stopwatch();

    LocalizationManager _localize => LocalizationManager.Instance;

    private MauiControls.ContentPage? _pageRef;
    private MauiControls.Grid? _mainGridRef;
    private MauiControls.Entry? _textInputRef;

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
                    ).RowSpacing(8)
                ).GridRow(2),
                AutoTransitionBar(),
                LoadingOverlay(),
                SessionSummaryOverlay()
            ).RowSpacing(8)
        )
        .TitleView(RenderTitleView())
        .Title($"{_localize["VocabularyQuiz"]}")
        .BackgroundColor(BootstrapTheme.Current.GetBackground())
        .OnAppearing(LoadVocabulary);
    }

    private VisualNode RenderTitleView()
    {
        return Grid("*", "*,Auto",
            // Timer (if from daily plan)
            Props?.FromTodaysPlan == true ?
                Grid(mainGridRef => _mainGridRef = mainGridRef, new ActivityTimerBar())
                    .GridColumn(1)
                    .HEnd()
                    .VCenter() : null
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

    VisualNode AutoTransitionBar()
    {
        var theme = BootstrapTheme.Current;
        return ProgressBar()
            .Progress(State.AutoTransitionProgress)
            .HeightRequest(4)
            .Background(Colors.Transparent)
            .ProgressColor(theme.Primary)
            .VStart();
    }

    VisualNode LoadingOverlay()
    {
        var theme = BootstrapTheme.Current;
        return Grid(
            Label($"{_localize["LoadingVocabulary"]}")
                .FontSize(48).FontAttributes(FontAttributes.Bold)
                .TextColor(theme.GetOnBackground())
                .Center()
        )
        .Background(Color.FromArgb("#80000000"))
        .GridRowSpan(3)
        .IsVisible(State.IsBusy);
    }

    VisualNode SessionSummaryOverlay()
    {
        var theme = BootstrapTheme.Current;
        return Grid(
            ScrollView(
                VStack(spacing: 16,
                    // Header - show round summary or session summary
                    Label($"{(State.CurrentRound > 0 && State.SessionSummaryItems.Count <= VocabularyQuizPageState.TurnsPerRound ? $"Round {State.CurrentRound} Summary" : $"Session Summary")}")
                        .H3()
                        .TextColor(theme.Primary)
                        .Center(),

                    Label($"{_localize["ReviewVocabularyStudied"]}")
                        .Center()
                        .TextColor(theme.GetOnBackground()),

                    // Vocabulary list
                    VStack(spacing: 8,
                        State.SessionSummaryItems.Select(item => RenderSummaryItem(item))
                    ),

                    // Round-based session stats
                    Border(
                        VStack(spacing: 8,
                            // Round progress indicator
                            VStack(spacing: 4,
                                Label($"{_localize["SessionProgress"]}")
                                    .FontAttributes(FontAttributes.Bold)
                                    .Center()
                                    .TextColor(theme.Primary),

                                // Show round-based stats
                                HStack(spacing: 20,
                                    VStack(spacing: 2,
                                        Label($"{State.RoundsCompleted}")
                                            .H5()
                                            .TextColor(theme.Primary)
                                            .Center(),
                                        Label($"{_localize["RoundsCompleted"]}")
                                            .Small()
                                            .Center()
                                            .TextColor(theme.GetOnBackground().WithAlpha(0.6f))
                                    ),
                                    VStack(spacing: 2,
                                        Label($"{State.WordsMasteredThisSession}")
                                            .H5()
                                            .TextColor(theme.Success)
                                            .Center(),
                                        Label($"{_localize["WordsMastered"]}")
                                            .Small()
                                            .Center()
                                            .TextColor(theme.GetOnBackground().WithAlpha(0.6f))
                                    ),
                                    VStack(spacing: 2,
                                        Label($"{State.TotalTurnsCompleted}")
                                            .H5()
                                            .TextColor(theme.Info)
                                            .Center(),
                                        Label($"{_localize["TotalTurns"]}")
                                            .Small()
                                            .Center()
                                            .TextColor(theme.GetOnBackground().WithAlpha(0.6f))
                                    )
                                ).Center()
                            )
                            .Margin(0, 0, 0, 16),

                            Label($"{_localize["RoundPerformance"]}")
                                .H5()
                                .Center()
                                .TextColor(theme.Primary),

                            HStack(spacing: 20,
                                VStack(spacing: 4,
                                    Label($"{State.SessionSummaryItems.Count(i => (i.QuizRecognitionStreak >= 3 && i.QuizProductionStreak >= 3) || i.ReadyToRotateOut)}")
                                        .H4()
                                        .TextColor(theme.Success)
                                        .Center(),
                                    Label($"{_localize["Strong"]}")
                                        .Small()
                                        .Center()
                                ),
                                VStack(spacing: 4,
                                    Label($"{State.SessionSummaryItems.Count(i => !i.ReadyToRotateOut && (i.QuizRecognitionStreak > 0 || i.QuizProductionStreak > 0))}")
                                        .H4()
                                        .TextColor(theme.Warning)
                                        .Center(),
                                    Label($"{_localize["Learning"]}")
                                        .Small()
                                        .Center()
                                ),
                                VStack(spacing: 4,
                                    Label($"{State.SessionSummaryItems.Count(i => i.QuizRecognitionStreak == 0 && i.QuizProductionStreak == 0)}")
                                        .H4()
                                        .TextColor(theme.Danger)
                                        .Center(),
                                    Label($"{_localize["NeedsWork"]}")
                                        .Small()
                                        .Center()
                                )
                            ).Center()
                        )
                        .Padding(16)
                    )
                    .Background(theme.GetSurface())
                    .StrokeShape(new RoundRectangle().CornerRadius(8))
                    .Margin(0, 16),

                    // Buttons - show different options based on context
                    Props.FromTodaysPlan
                        ? VStack(spacing: 8,
                            // Next Activity button (for plan mode)
                            Button($"{_localize["PlanNextActivityButton"]}")
                                .OnClicked(async () => await NavigateToNextPlanActivity())
                                .Class("btn-primary")
                                .Padding(24, 16)
                                .IsEnabled(IsSessionGoalMet()),

                            // Continue practicing button (secondary option)
                            Button($"{_localize["ContinueSessionButton"]}")
                                .OnClicked(async () => await SetupNewRound())
                                .Outlined()
                                .Padding(24, 8)
                        )
                        : Button($"{_localize["ContinueToNextSession"]}")
                            .OnClicked(async () => await SetupNewRound())
                            .Class("btn-primary")
                            .Padding(24, 16)
                            .Margin(0, 16)
                )
                .Padding(new Thickness(16))
            )
        )
        .Background(theme.GetBackground())
        .GridRowSpan(3)
        .IsVisible(State.ShowSessionSummary);
    }

    VisualNode RenderSummaryItem(VocabularyQuizItem item)
    {
        var theme = BootstrapTheme.Current;
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

        string srsStatus = isCompleted ? "Mastered" :
                          daysUntilNext > 30 ? $"Next: {daysUntilNext}d" :
                          daysUntilNext > 0 ? $"Next: {daysUntilNext}d" :
                          daysUntilNext == 0 ? "Due today" :
                          "Overdue";

        Color statusColor = accuracy >= 0.8f ? theme.Success :
                           accuracy >= 0.5f ? theme.Warning :
                           theme.Danger;

        string statusIcon = accuracy >= 0.8f ? "✓" :
                           accuracy >= 0.5f ? "~" :
                           "✗";

        return Border(
            HStack(spacing: 12,
                Label(statusIcon)
                    .FontSize(16),

                VStack(spacing: 4,
                    Label(item.Word.NativeLanguageTerm ?? "")
                        .FontSize(16)
                        .FontAttributes(FontAttributes.Bold)
                        .TextColor(theme.GetOnBackground()),

                    Label(item.Word.TargetLanguageTerm ?? "")
                        .FontSize(14)
                        .TextColor(theme.Primary),

                    Label($"Session: {sessionPercentage:F0}% | Mastery: {masteryPercentage:F0}%")
                        .FontSize(12)
                        .TextColor(theme.GetOnBackground().WithAlpha(0.6f)),

                    Label($"{srsStatus} • {totalAttempts} attempts" + (daysSinceReview > 0 ? $" • Last: {daysSinceReview}d ago" : ""))
                        .FontSize(10)
                        .TextColor(isCompleted ? theme.Success : theme.GetOnBackground().WithAlpha(0.6f))
                )
                .HStart(),

                Label(statusIcon)
                    .FontSize(20)
                    .HEnd()
                    .VCenter()
            )
            .Padding(16)
        )
        .Background(theme.GetSurface())
        .Stroke(statusColor.WithAlpha(0.3f))
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(6))
        .Margin(0, 4);
    }

    VisualNode LearningProgressBar()
    {
        var theme = BootstrapTheme.Current;
        return Grid(rows: "Auto", columns: "Auto,*,Auto",
            // Left bubble shows current turn in round (1-based for display)
            Border(
                Label($"{State.CurrentTurnInRound + 1}")
                    .FontSize(16)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(Colors.White)
                    .TranslationY(-4)
                    .Center()
            )
            .Background(theme.Success)
            .StrokeShape(new RoundRectangle().CornerRadius(15))
            .StrokeThickness(0)
            .HeightRequest(30)
            .Padding(16, 2)
            .GridColumn(0)
            .VCenter(),

            // Center progress bar shows progress through current round
            ProgressBar()
                .Progress((double)State.CurrentTurnInRound / VocabularyQuizPageState.TurnsPerRound)
                .ProgressColor(theme.Success)
                .Background(Colors.LightGray)
                .HeightRequest(6)
                .GridColumn(1)
                .VCenter()
                .Margin(8, 0),

            // Right bubble shows turns per round (always 10)
            Border(
                Label($"{VocabularyQuizPageState.TurnsPerRound}")
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
            .Padding(16, 2)
            .GridColumn(2)
            .VCenter()
        ).Padding(16).GridRow(1);
    }

    VisualNode TermDisplay()
    {
        var theme = BootstrapTheme.Current;
        return VStack(spacing: 16,
            Label(string.Format($"{_localize["WhatIsThisInLanguage"]}", State.TargetLanguage))
                .FontSize(18)
                .FontAttributes(FontAttributes.Bold)
                .Center(),

            // Question term with audio play button
            HStack(spacing: 8,
                Label(State.CurrentTerm)
                    .FontSize(DeviceInfo.Platform == DevicePlatform.WinUI ? 64 : 32)
                    .HeightRequest(64)
                    .Center()
                    .FontAttributes(FontAttributes.Bold),

                // Play vocabulary audio button
                ImageButton()
                    .Source(BootstrapIcons.Create(BootstrapIcons.PlayFill, theme.GetOnBackground(), 24))
                    .HeightRequest(64)
                    .WidthRequest(64)
                    .Aspect(Aspect.Center)
                    .OnClicked(async () => await PlayVocabularyAudioManually())
                    .VCenter()
            ).Center(),

            Label(State.CurrentTargetLanguageTerm)
                .FontSize(24)
                .Center()
                .FontAttributes(FontAttributes.Bold)
                .TextColor(theme.Primary)
                .IsVisible((State.ShowAnswer || State.ShowCorrectAnswer) && State.UserMode != "MultipleChoice"),
            Label(State.RequireCorrectTyping ? "Type the correct answer to continue:" : "")
                .FontSize(14)
                .Center()
                .TextColor(theme.Warning)
                .IsVisible(State.RequireCorrectTyping)
        )
        .WithAnimation(Easing.CubicInOut, 200)
        .Opacity(State.IsCardTransitioning ? 0 : 1)
        .Margin(24)
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
    }

    VisualNode UserInputSection() =>
        Grid(rows: "*, *", columns: "*, Auto, Auto, Auto",
            State.UserMode == InputMode.MultipleChoice.ToString() ?
                RenderMultipleChoice() :
                RenderTextInput()
        )
        .RowSpacing(DeviceInfo.Platform == DevicePlatform.WinUI ? 0 : 5)
        .Padding(DeviceInfo.Platform == DevicePlatform.WinUI ? new Thickness(30) : new Thickness(15, 0))
        .GridRow(1);

    VisualNode RenderTextInput()
    {
        var theme = BootstrapTheme.Current;
        return VStack(
            Label(State.RequireCorrectTyping ? $"{_localize["TypeCorrectAnswerHint"]}" : $"{_localize["TypeYourAnswerHint"]}").Small().Muted(),
            Border(
                Entry(entryRef => _textInputRef = entryRef)
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
            .BackgroundColor(theme.GetSurface())
            .Stroke(theme.GetOutline())
            .StrokeThickness(1)
            .StrokeShape(new RoundRectangle().CornerRadius(8))
            .Padding(4)
        )
        .GridRow(1)
        .GridColumn(0)
        .GridColumnSpan(DeviceInfo.Idiom == DeviceIdiom.Phone ? 4 : 1)
        .Margin(0, 0, 0, 8);
    }

    VisualNode RenderMultipleChoice() =>
        VStack(spacing: 8,
            State.ChoiceOptions.Select(option => RenderChoiceOption(option))
        )
        .GridRow(0);

    VisualNode RenderChoiceOption(string option)
    {
        var theme = BootstrapTheme.Current;
        var isSelected = State.UserGuess == option;
        var showFeedback = State.ShowAnswer;
        var isCorrect = option == State.CurrentTargetLanguageTerm;

        Color backgroundColor = Colors.Transparent;
        Color borderColor = theme.GetOutline();
        Color textColor = theme.GetOnBackground();

        if (showFeedback)
        {
            if (isCorrect)
            {
                backgroundColor = theme.Success;
                borderColor = theme.Success;
                textColor = Colors.White;
            }
            else if (isSelected && !isCorrect)
            {
                backgroundColor = theme.Danger;
                borderColor = theme.Danger;
                textColor = Colors.White;
            }
        }
        else if (isSelected)
        {
            borderColor = theme.Primary;
            backgroundColor = theme.Primary.WithAlpha(0.1f);
        }

        return Border(
            Label(option)
                .FontSize(20)
                .Center()
                .TextColor(textColor)
                .Padding(16, 16)
        )
        .Background(backgroundColor)
        .Stroke(borderColor)
        .StrokeThickness(2)
        .StrokeShape(new RoundRectangle().CornerRadius(8))
        .Margin(0, 4)
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
        var theme = BootstrapTheme.Current;
        if (item.IsCompleted)
            return BootstrapIcons.Create(BootstrapIcons.CheckCircleFill, theme.Success, 20);

        if (item.IsPromoted)
            return BootstrapIcons.Create(BootstrapIcons.PencilSquare, theme.Primary, 20);

        return BootstrapIcons.Create(BootstrapIcons.Circle, theme.GetOutline(), 20);
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
        var theme = BootstrapTheme.Current;
        if (item.IsCompleted)
            return theme.Success.WithAlpha(0.2f);

        if (item.IsPromoted)
            return theme.Warning.WithAlpha(0.2f);

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

        // Card flip animation: fade out before swapping content
        SetState(s => s.IsCardTransitioning = true);
        await Task.Delay(200);

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
            s.IsCardTransitioning = false; // Fade back in with new content
        });

        // Enhanced tracking: Start response timer
        _responseTimer.Restart();

        // Auto-focus text input field if in text mode
        if (State.UserMode == InputMode.Text.ToString() && _textInputRef != null)
        {
            _textInputRef.Dispatcher.Dispatch(() => _textInputRef.Focus());
        }

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

    /// <summary>
    /// Completes the session and shows summary.
    /// Called when pool exhausted + all active words mastered, OR when session naturally ends.
    /// </summary>
    async Task CompleteSession()
    {
        _logger.LogInformation("🏆 Session Complete! Rounds: {Rounds}, Words Mastered: {Mastered}, Total Turns: {Turns}",
            State.RoundsCompleted, State.WordsMasteredThisSession, State.TotalTurnsCompleted);

        // Capture vocabulary items for session summary before any modifications
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

        // Update state for session complete
        SetState(s =>
        {
            s.SessionSummaryItems = sessionItems;
            s.ShowSessionSummary = true;
        });

        await UpdateTermCountsAsync();

        await AppShell.DisplayToastAsync($"{_localize["SessionComplete"]}");

        // Jump to first term (for when they choose to continue)
        var firstTerm = State.VocabularyItems.FirstOrDefault();
        if (firstTerm != null)
        {
            await JumpTo(firstTerm);
        }
    }

    // ========================================================================
    // ROUND-BASED WORD ROTATION
    // ========================================================================

    /// <summary>
    /// Handles mastered words: removes from active list, queues replacements for NEXT round.
    /// This ensures mastery mid-round doesn't add words that appear in the same round.
    /// </summary>
    async Task HandleMasteredWordsForNextRound()
    {
        // Find words that are ready to rotate out (mastered in this quiz)
        var masteredWords = State.VocabularyItems.Where(item => item.ReadyToRotateOut).ToList();

        if (masteredWords.Any())
        {
            _logger.LogInformation("🎊 {Count} words mastered this turn - queueing replacements for next round:", masteredWords.Count);

            foreach (var masteredWord in masteredWords)
            {
                _logger.LogDebug("  🏆 {NativeTerm} (MC: {MCStreak}, Text: {TextStreak})",
                    masteredWord.Word.NativeLanguageTerm,
                    masteredWord.QuizRecognitionStreak,
                    masteredWord.QuizProductionStreak);

                // Remove from active set
                State.VocabularyItems.Remove(masteredWord);

                // Track session stats
                SetState(s => s.WordsMasteredThisSession++);
            }

            // Queue new words as replacements for NEXT round
            await QueueReplacementWordsForNextRound(masteredWords.Count);

            // Update term counts to reflect changes
            await UpdateTermCountsAsync();

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

    /// <summary>
    /// Queues replacement words to be added at the start of the next round.
    /// Does NOT add words mid-round to maintain consistent round structure.
    /// </summary>
    async Task QueueReplacementWordsForNextRound(int neededCount)
    {
        if (neededCount <= 0) return;

        _logger.LogDebug("🏴‍☠️ Queueing {NeededCount} replacement words for next round", neededCount);

        // Get words already in active set or pending
        var currentWords = State.VocabularyItems.Select(item => item.Word.Id).ToHashSet();
        var pendingWords = State.PendingReplacements.Select(item => item.Word.Id).ToHashSet();
        var excludeIds = currentWords.Union(pendingWords).ToHashSet();

        var availableWords = new List<VocabularyWord>();

        if (Props.Resources?.Any() == true)
        {
            foreach (var resourceRef in Props.Resources)
            {
                var resource = await _resourceRepo.GetResourceAsync(resourceRef.Id);
                if (resource?.Vocabulary?.Any() == true)
                {
                    var newWords = resource.Vocabulary
                        .Where(word => !excludeIds.Contains(word.Id))
                        .ToList();
                    availableWords.AddRange(newWords);
                }
            }
        }

        if (!availableWords.Any())
        {
            _logger.LogInformation("🏴‍☠️ No more words available to queue! Pool exhausted.");
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
            .Take(neededCount)
            .ToList();

        _logger.LogDebug("🏴‍☠️ Queuing {Count} new words for next round:", sortedWords.Count);

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
                QuizRecognitionStreak = 0,
                QuizProductionStreak = 0
            };

            State.PendingReplacements.Add(newItem);
            _logger.LogDebug("  📝 Queued: {NativeTerm} (Global mastery: {Mastery:F0}%)",
                word.NativeLanguageTerm, progress.MasteryScore * 100);
        }
    }

    // Legacy method - kept for backwards compatibility but now delegates to round-based system
    Task RotateOutMasteredWordsAndAddNew()
    {
        return HandleMasteredWordsForNextRound();
    }

    /// <summary>
    /// Legacy method to add terms - now queues to PendingReplacements for round-based system.
    /// These words will be added at the start of the next round.
    /// </summary>
    async Task AddNewTermsToMaintainSet()
    {
        // Target set size using new constant
        int targetSetSize = VocabularyQuizPageState.ActiveWordCount;
        int currentCount = State.VocabularyItems.Count + State.PendingReplacements.Count;
        int neededTerms = targetSetSize - currentCount;

        if (neededTerms <= 0) return;

        // Delegate to the new queue method
        await QueueReplacementWordsForNextRound(neededTerms);
    }

    // 🏴‍☠️ INTELLIGENT WORD SELECTION: Prioritize learning and resume progress
    async Task<List<VocabularyWord>> SelectWordsIntelligently(List<VocabularyWord> allVocabulary)
    {
        // From plan: Use explicit target from plan for perfect alignment
        // Manual: Load all available words (ActiveWordCount enforced during round creation)
        var targetSetSize = Props.FromTodaysPlan && Props.TargetWordCount.HasValue
            ? Props.TargetWordCount.Value
            : allVocabulary.Count;

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

    /// <summary>
    /// Gets all learning resources from props (handles both Props.Resources and Props.Resource).
    /// </summary>
    List<LearningResource> GetAllResources()
    {
        if (Props.Resources?.Any() == true)
        {
            return Props.Resources.ToList();
        }

        if (Props.Resource?.Id > 0)
        {
            return new List<LearningResource> { Props.Resource };
        }

        return new List<LearningResource>();
    }

    /// <summary>
    /// Refreshes all smart resources in the provided list.
    /// </summary>
    async Task RefreshSmartResources(List<LearningResource> resources)
    {
        foreach (var resource in resources)
        {
            if (resource?.Id > 0 && resource.IsSmartResource)
            {
                _logger.LogInformation("🔄 Refreshing smart resource: {Title}", resource.Title);
                await _smartResourceService.RefreshSmartResourceAsync(resource.Id);
            }
        }
    }

    /// <summary>
    /// Loads and combines vocabulary from all provided resources.
    /// </summary>
    async Task<List<VocabularyWord>> LoadVocabularyFromResources(List<LearningResource> resources)
    {
        var vocabulary = new List<VocabularyWord>();

        foreach (var resource in resources)
        {
            if (resource?.Vocabulary?.Any() == true)
            {
                vocabulary.AddRange(resource.Vocabulary);
            }
        }

        return vocabulary;
    }

    /// <summary>
    /// Creates quiz items with progress data, falling back to default progress if unavailable.
    /// </summary>
    async Task<List<VocabularyQuizItem>> CreateQuizItems(List<VocabularyWord> words)
    {
        var wordIds = words.Select(w => w.Id).ToList();
        Dictionary<int, SentenceStudio.Shared.Models.VocabularyProgress> progressDict;

        try
        {
            progressDict = await _vocabProgressService.GetProgressForWordsAsync(wordIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading progress, using defaults");
            progressDict = new Dictionary<int, SentenceStudio.Shared.Models.VocabularyProgress>();
        }

        return words.Select(word =>
        {
            var progress = progressDict?.ContainsKey(word.Id) == true
                ? progressDict[word.Id]
                : new SentenceStudio.Shared.Models.VocabularyProgress
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
                Progress = progress,
                QuizRecognitionStreak = 0,
                QuizProductionStreak = 0
            };
        }).ToList();
    }

    async Task LoadVocabulary()
    {
        SetState(s => s.IsBusy = true);

        try
        {
            // Load user preferences first to ensure display direction is applied
            if (State.UserPreferences == null)
            {
                LoadUserPreferences();
            }

            // Get all resources (handles both Props.Resources and Props.Resource)
            var resources = GetAllResources();

            if (!resources.Any())
            {
                _logger.LogWarning("No resources provided");
                await ShowNoVocabularyAlert();
                return;
            }

            // Refresh smart resources before loading
            await RefreshSmartResources(resources);

            // Load vocabulary from all resources
            var vocabulary = await LoadVocabularyFromResources(resources);

            if (!vocabulary.Any())
            {
                _logger.LogWarning("No vocabulary found in resources");
                await ShowNoVocabularyAlert();
                return;
            }

            // Smart word selection: prioritize unmastered words
            var smartSelectedWords = await SelectWordsIntelligently(vocabulary);
            var totalSets = (int)Math.Ceiling(vocabulary.Count / (double)VocabularyQuizPageState.ActiveWordCount);

            _logger.LogInformation("📚 Loaded {Selected} of {Total} words for quiz",
                smartSelectedWords.Count, vocabulary.Count);

            // Create quiz items with progress
            var quizItems = await CreateQuizItems(smartSelectedWords);

            // Filter to incomplete items only
            var incompleteItems = quizItems.Where(item => !item.ReadyToRotateOut).ToList();

            if (!incompleteItems.Any())
            {
                await AppShell.DisplayToastAsync($"{_localize["AllWordsCompletedAddingNew"]}");
            }

            // Set first item as current
            if (incompleteItems.Any())
            {
                incompleteItems[0].IsCurrent = true;
            }

            // Get target language from first resource
            var targetLanguage = resources.FirstOrDefault()?.Language ?? "the target language";

            // Update state
            SetState(s =>
            {
                s.VocabularyItems = new ObservableCollection<VocabularyQuizItem>(incompleteItems);
                s.CurrentRound = 1;
                s.CorrectAnswersInRound = 0;
                s.CurrentSetNumber = 1;
                s.TotalSets = totalSets;
                s.TargetLanguage = targetLanguage;
            });

            // Initialize round-based session (will load first word via StartNewRound)
            if (incompleteItems.Any())
            {
                InitializeSession();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading vocabulary");
        }
        finally
        {
            SetState(s => s.IsBusy = false);
            await UpdateTermCountsAsync();
        }
    }

    async Task ShowNoVocabularyAlert()
    {
        SetState(s => s.IsBusy = false);
        await IPopupService.Current.PushAsync(new SimpleActionPopup
        {
            Title = $"{_localize["NoVocabulary"]}",
            Text = $"{_localize["NoVocabularyMessage"]}",
            ActionButtonText = $"{_localize["OK"]}",
            ShowSecondaryActionButton = false
        });
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

        // Use fuzzy matching for answer evaluation
        var matchResult = FuzzyMatcher.Evaluate(answer, State.CurrentTargetLanguageTerm);
        var isCorrect = matchResult.IsCorrect;

        _logger.LogDebug("🔍 CheckAnswer: isCorrect={IsCorrect}, matchType={MatchType}",
            isCorrect, matchResult.MatchType);

        if (matchResult.MatchType == "Fuzzy")
        {
            _logger.LogInformation("✨ Fuzzy match accepted: user='{User}', complete='{Complete}'",
                answer, matchResult.CompleteForm);
        }

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
            await UpdateUIBasedOnEnhancedProgress(currentItem, isCorrect, matchResult);
            _logger.LogDebug("✅ CheckAnswer: UI feedback updated");

            // 🏴‍☠️ CHECK FOR IMMEDIATE MASTERY: If word is now mastered, prepare for rotation
            if (currentItem.ReadyToRotateOut)
            {
                _logger.LogDebug("🎯 Word '{NativeTerm}' is now ready to rotate out!", currentItem.Word.NativeLanguageTerm);
                // Note: Actual rotation happens in NextItem() to ensure proper flow
            }

            // Update term counts (turn counters incremented in NextItem)
            _logger.LogDebug("🔢 CheckAnswer: Updating term counts...");
            await UpdateTermCountsAsync();

            // Session continues until pool exhausted + all active words mastered (checked in StartNewRound)

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

    private async Task UpdateUIBasedOnEnhancedProgress(VocabularyQuizItem item, bool wasCorrect, FuzzyMatchResult matchResult)
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
                        s.FeedbackMessage = $"{_localize["CorrectWithStreak"].ToString().Replace("{0}", currentStreak.ToString())} Ready to type!";
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
                        // Show fuzzy match feedback if applicable
                        if (matchResult.MatchType == "Fuzzy")
                        {
                            s.FeedbackMessage = $"{_localize["FuzzyMatchCorrect"].ToString().Replace("{0}", matchResult.CompleteForm ?? "")} {_localize["MasteredWithStreak"].ToString().Replace("{0}", currentStreak.ToString())}";
                        }
                        else
                        {
                            s.FeedbackMessage = $"{_localize["MasteredWithStreak"].ToString().Replace("{0}", currentStreak.ToString())}";
                        }
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
                        // Show fuzzy match feedback if applicable
                        if (matchResult.MatchType == "Fuzzy")
                        {
                            s.FeedbackMessage = $"{_localize["FuzzyMatchCorrect"].ToString().Replace("{0}", matchResult.CompleteForm ?? "")}";
                        }
                        else
                        {
                            s.FeedbackMessage = $"{_localize["CorrectWithStreak"].ToString().Replace("{0}", currentStreak.ToString())}";
                        }
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
                    s.FeedbackMessage = $"{_localize["IncorrectStreakReset"]} Type the correct answer:";
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
                    s.FeedbackMessage = $"{_localize["IncorrectStreakReset"]}";
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
        var durationMs = State.UserPreferences?.AutoAdvanceDuration ?? 2000;
        var duration = TimeSpan.FromMilliseconds(durationMs);

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

    async Task UpdateTermCountsAsync()
    {
        // Get total terms and calculate comprehensive progress (async to avoid blocking)
        var totalResourceTerms = await GetTotalTermsInResourceAsync();

        SetState(s =>
        {
            // Count terms in current activity/session
            s.UnknownTermsCount = s.VocabularyItems.Count(item => item.IsUnknown);
            s.LearningTermsCount = s.VocabularyItems.Count(item => item.IsLearning);

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

    async Task<List<VocabularyWord>> GetTotalTermsInResourceAsync()
    {
        // Get all vocabulary terms from the current learning resources
        var allWords = new List<VocabularyWord>();

        if (Props.Resources?.Any() == true)
        {
            foreach (var resourceRef in Props.Resources)
            {
                var resource = await _resourceRepo.GetResourceAsync(resourceRef.Id);
                if (resource?.Vocabulary?.Any() == true)
                {
                    allWords.AddRange(resource.Vocabulary);
                }
            }
        }
        else if (Props.Resource?.Id > 0)
        {
            // Fallback to single resource
            var resource = await _resourceRepo.GetResourceAsync(Props.Resource.Id);
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
            await StartNewRound();
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

    // ========================================================================
    // ROUND-BASED SESSION MANAGEMENT
    // ========================================================================

    /// <summary>
    /// Initializes the first round by setting up session counters, then delegating to StartNewRound.
    /// Called after LoadVocabulary when words are ready.
    /// </summary>
    Task InitializeSession()
    {
        // Initialize session-level counters
        SetState(s =>
        {
            s.CurrentRound = 0; // StartNewRound will increment to 1
            s.RoundsCompleted = 0;
            s.WordsMasteredThisSession = 0;
            s.TotalTurnsCompleted = 0;
        });

        // Let StartNewRound handle the actual round setup
        return StartNewRound();
    }

    /// <summary>
    /// Starts a new round: adds pending replacements, shuffles word order, resets turn counter.
    /// Called by InitializeSession for round 1, or when CurrentTurnInRound reaches RoundWordOrder.Count.
    /// </summary>
    async Task StartNewRound()
    {
        // Capture current round summary BEFORE starting new round (except first round)
        var isFirstRound = State.CurrentRound == 0;
        if (!isFirstRound)
        {
            _logger.LogInformation("📊 Round {Round} complete - showing summary", State.CurrentRound);
            var roundSummaryItems = State.RoundWordOrder.ToList();
            SetState(s =>
            {
                s.SessionSummaryItems = roundSummaryItems;
                s.ShowSessionSummary = true;
            });
            // Exit here - user will click "Continue" to proceed
            return;
        }

        // Continue with normal round setup
        await SetupNewRound();
    }

    /// <summary>
    /// Internal method to actually set up the next round (called by StartNewRound or continue button)
    /// </summary>
    async Task SetupNewRound()
    {
        // Stop any audio that might be playing before starting new round
        StopAllAudio();

        // Hide summary if showing
        SetState(s => s.ShowSessionSummary = false);

        // Add any pending replacement words to the active set
        if (State.PendingReplacements.Any())
        {
            _logger.LogInformation("🔄 Adding {Count} pending replacement words to new round", State.PendingReplacements.Count);
            foreach (var replacement in State.PendingReplacements)
            {
                State.VocabularyItems.Add(replacement);
                _logger.LogDebug("  + {Word} added to active set", replacement.Word.NativeLanguageTerm);
            }
            State.PendingReplacements.Clear();
        }

        // Get available (non-mastered) words from the pool
        var availableWords = State.VocabularyItems.Where(i => !i.ReadyToRotateOut).ToList();

        if (availableWords.Count == 0)
        {
            // All words mastered and no replacements available
            _logger.LogInformation("🏆 Session complete! All words mastered.");
            await CompleteSession();
            return;
        }

        // Take only up to ActiveWordCount (10) words from the pool, shuffle them for the round
        var shuffledItems = availableWords
            .Take(VocabularyQuizPageState.ActiveWordCount)
            .OrderBy(_ => Guid.NewGuid())
            .ToList();

        // Increment round counter
        SetState(s =>
        {
            s.RoundWordOrder = shuffledItems;
            s.CurrentTurnInRound = 0;
            s.CurrentRound++;
            s.RoundsCompleted++;
            s.CorrectAnswersInRound = 0;
            s.IsRoundComplete = false;
        });

        _logger.LogInformation("🎯 Round {Round} started: {Selected} of {Available} available words",
            State.CurrentRound, shuffledItems.Count, availableWords.Count);

        await AppShell.DisplayToastAsync(string.Format($"{_localize["RoundStarted"]}", State.CurrentRound));

        // Jump to the first word of the new round
        var firstWord = GetCurrentRoundWord();
        if (firstWord != null)
        {
            await JumpTo(firstWord);
        }
    }

    /// <summary>
    /// Gets the current word to present based on round order and turn.
    /// Returns the item directly from RoundWordOrder (which stores item references, not indices).
    /// </summary>
    VocabularyQuizItem? GetCurrentRoundWord()
    {
        if (State.RoundWordOrder.Count == 0 || State.CurrentTurnInRound >= State.RoundWordOrder.Count)
        {
            return null;
        }

        var item = State.RoundWordOrder[State.CurrentTurnInRound];
        // Skip items that became mastered mid-round (shouldn't happen with current flow, but be safe)
        if (item?.ReadyToRotateOut == true)
        {
            _logger.LogDebug("⏭️ Skipping mastered item in round order: {Word}", item.Word?.NativeLanguageTerm);
            return null;
        }

        return item;
    }

    /// <summary>
    /// Round-based NextItem: Advances to next word in the current round order.
    /// When all words in round are done, starts a new round.
    /// </summary>
    async Task NextItem()
    {
        // Handle the special case where user must type correct answer
        if (State.RequireCorrectTyping)
        {
            var matchResult = FuzzyMatcher.Evaluate(State.UserInput.Trim(), State.CorrectAnswerToType.Trim());

            if (!matchResult.IsCorrect)
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

        // Handle word mastery - queue replacements for next round (don't add mid-round)
        await HandleMasteredWordsForNextRound();

        // Increment total turns completed
        SetState(s => s.TotalTurnsCompleted++);

        // Advance to next turn in round
        SetState(s => s.CurrentTurnInRound++);

        _logger.LogDebug("🔄 Turn {TurnInRound}/{TotalTurns} in Round {Round}",
            State.CurrentTurnInRound, State.RoundWordOrder.Count, State.CurrentRound);

        // Check if we've completed all turns in this round
        if (State.CurrentTurnInRound >= State.RoundWordOrder.Count)
        {
            _logger.LogInformation("✅ Round {Round} complete! Starting new round...", State.CurrentRound);
            await StartNewRound();

            // If showing summary, exit here - user will continue when ready
            if (State.ShowSessionSummary)
            {
                _logger.LogDebug("📊 Summary displayed - waiting for user to continue");
                return;
            }

            // Check if session is complete (handled in StartNewRound)
            if (State.VocabularyItems.Count == 0)
            {
                _logger.LogInformation("🏁 Session complete - no more vocabulary available");
                await CompleteSession();
                return;
            }
        }

        // Rebuild RoundWordOrder if words were removed (skip mastered word indices)
        await RefreshRoundOrderIfNeeded();

        // Get the next word from the round order
        var nextWord = GetCurrentRoundWord();

        if (nextWord == null)
        {
            // Edge case: no valid next word (shouldn't happen normally)
            _logger.LogWarning("⚠️ No valid next word found in round order");

            // Try to start new round or complete session
            var activeWords = State.VocabularyItems.Where(i => !i.ReadyToRotateOut).ToList();
            if (activeWords.Any() || State.PendingReplacements.Any())
            {
                await StartNewRound();
                nextWord = GetCurrentRoundWord();
            }

            if (nextWord == null)
            {
                await CompleteSession();
                return;
            }
        }

        await JumpTo(nextWord);
    }

    /// <summary>
    /// Refreshes the round order by removing indices for mastered words.
    /// Only rebuilds if we're mid-round and words have been mastered.
    /// </summary>
    async Task RefreshRoundOrderIfNeeded()
    {
        // Check if any items in round order are now mastered (shouldn't happen with current flow)
        var validOrder = State.RoundWordOrder
            .Where(item => item != null && !item.ReadyToRotateOut)
            .ToList();

        if (validOrder.Count != State.RoundWordOrder.Count)
        {
            _logger.LogDebug("🔄 Refreshing round order: {Original} → {Valid} active items",
                State.RoundWordOrder.Count, validOrder.Count);

            SetState(s =>
            {
                // Rebuild order with only non-mastered items
                s.RoundWordOrder = validOrder;
                // Ensure CurrentTurnInRound is still valid
                if (s.CurrentTurnInRound > validOrder.Count)
                {
                    s.CurrentTurnInRound = validOrder.Count; // Will trigger new round
                }
            });
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
        // Word goal: mastered the target number of words
        var wordGoalMet = Props.TargetWordCount.HasValue
            && State.WordsMasteredThisSession >= Props.TargetWordCount.Value;

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
    Task NavigateToNextPlanActivity()
    {
        _logger.LogInformation("🎯 Navigating to next plan activity");

        // Stop the timer for current activity
        if (Props.FromTodaysPlan)
        {
            _timerService.StopSession();
        }

        // Pop back to dashboard - it will automatically show next available item
        return MauiControls.Shell.Current.GoToAsync("..");
    }


    protected override void OnMounted()
    {
        _logger.LogDebug("🚀 VocabularyQuizPage.OnMounted() START");
        _themeService.ThemeChanged += OnThemeChanged;
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
        LoadUserPreferences();

    }

    protected override void OnWillUnmount()
    {
        _logger.LogDebug("🛑 VocabularyQuizPage.OnWillUnmount() START");
        _themeService.ThemeChanged -= OnThemeChanged;
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

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

    // ============================================================================
    // VOCABULARY QUIZ PREFERENCES METHODS
    // ============================================================================

    /// <summary>
    /// Loads user preferences from VocabularyQuizPreferences service.
    /// MUST be called before LoadCurrentItem() to ensure correct display direction.
    /// </summary>
    void LoadUserPreferences()
    {
        _logger.LogInformation("📋 LoadUserPreferences called. _preferences null? {IsNull}", _preferences == null);
        if (_preferences != null)
        {
            _logger.LogInformation("📋 _preferences.AutoPlayVocabAudio={AutoPlay}", _preferences.AutoPlayVocabAudio);
        }
        SetState(s => s.UserPreferences = _preferences);
        _logger.LogInformation("📋 Loaded vocabulary quiz preferences: DisplayDirection={Direction}, AutoPlayVocabAudio={AutoPlay}",
            _preferences?.DisplayDirection, _preferences?.AutoPlayVocabAudio);
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

    // ============================================================================
    // AUDIO PLAYBACK METHODS
    // ============================================================================

    /// <summary>
    /// Manually plays vocabulary audio when user clicks play button.
    /// Ignores AutoPlayVocabAudio preference.
    /// </summary>
    async Task PlayVocabularyAudioManually()
    {
        var currentItem = State.VocabularyItems.FirstOrDefault(i => i.IsCurrent);
        if (currentItem == null) return;

        _logger.LogInformation("🎧 Manual play vocabulary audio for: {Term}", currentItem.Word.TargetLanguageTerm);

        // Stop any currently playing audio
        StopAllAudio();

        // Play audio (bypass preference check)
        await PlayVocabularyAudioInternal(currentItem.Word);
    }

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
        var prefsLoaded = State.UserPreferences != null;
        var autoPlayEnabled = State.UserPreferences?.AutoPlayVocabAudio ?? false;
        _logger.LogDebug("🎧 PlayVocabularyAudioAsync: prefsLoaded={PrefsLoaded}, autoPlayEnabled={AutoPlay}, _preferences.AutoPlayVocabAudio={DirectPref}",
            prefsLoaded, autoPlayEnabled, _preferences?.AutoPlayVocabAudio);
        
        if (!autoPlayEnabled)
        {
            _logger.LogDebug("🎧 Auto-play vocabulary audio is disabled (prefsLoaded={PrefsLoaded})", prefsLoaded);
            return;
        }

        await PlayVocabularyAudioInternal(word);
    }

    /// <summary>
    /// Internal method that actually plays the audio.
    /// Uses the CORRECT pattern from EditVocabularyWordPage.cs
    /// </summary>
    async Task PlayVocabularyAudioInternal(VocabularyWord word)
    {
        try
        {
            // Get the target language term (Korean word)
            var targetTerm = word.TargetLanguageTerm;

            if (string.IsNullOrWhiteSpace(targetTerm))
            {
                _logger.LogWarning("⚠️ PlayVocabularyAudioInternal: TargetLanguageTerm is null/empty for word ID {WordId}", word.Id);
                return;
            }

            _logger.LogInformation("🎧 Playing vocabulary audio for: {Term}", targetTerm);

            Stream audioStream;
            bool fromCache = false;

            // Get the selected voice ID for the resource's language
            var voiceId = _speechVoicePreferences.GetVoiceForLanguage(State.TargetLanguage);

            // Check if we have cached audio for this word with this voice
            var cachedAudio = await _historyRepo.GetStreamHistoryByPhraseAndVoiceAsync(targetTerm, voiceId);

            if (cachedAudio != null && !string.IsNullOrEmpty(cachedAudio.AudioFilePath) && File.Exists(cachedAudio.AudioFilePath))
            {
                // Use cached audio file
                _logger.LogInformation("🎧 Using cached audio for word: {Word} with voice: {VoiceId}", targetTerm, voiceId);
                audioStream = File.OpenRead(cachedAudio.AudioFilePath);
                fromCache = true;
            }
            else
            {
                // Generate audio using ElevenLabs with selected voice
                _logger.LogInformation("🎧 Generating audio from API for word: {Word} with voice: {VoiceId}", targetTerm, voiceId);
                audioStream = await _speechService.TextToSpeechAsync(
                    text: targetTerm,
                    voiceId: voiceId, // Use selected voice from preferences
                    stability: 0.5f,
                    similarityBoost: 0.75f
                );

                // Save to cache for future use
                var audioCacheDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "AudioCache");
                Directory.CreateDirectory(audioCacheDir);

                var fileName = $"word_{Guid.NewGuid()}.mp3";
                var filePath = System.IO.Path.Combine(audioCacheDir, fileName);

                // Save to file
                using (var fileStream = File.Create(filePath))
                {
                    await audioStream.CopyToAsync(fileStream);
                }

                // Create stream history entry for caching
                var streamHistory = new StreamHistory
                {
                    Phrase = targetTerm,
                    VoiceId = voiceId, // Save with the voice ID used
                    AudioFilePath = filePath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _historyRepo.SaveStreamHistoryAsync(streamHistory);

                _logger.LogInformation("✅ Audio generated and cached for word");

                // Open the file again for playback
                audioStream = File.OpenRead(filePath);
            }

            // Reset stream position to beginning
            audioStream.Position = 0;

            // Create audio player from stream and play immediately
            var player = AudioManager.Current.CreatePlayer(audioStream);
            player.PlaybackEnded += OnVocabularyAudioEnded;
            player.Play();

            SetState(s => s.VocabularyAudioPlayer = player);

            _logger.LogInformation("✅ Successfully playing audio for: {Word} (from {Source})",
                targetTerm, fromCache ? "cache" : "API");
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
        _logger.LogDebug("🎵 Audio playback ended");

        if (State.VocabularyAudioPlayer != null)
        {
            State.VocabularyAudioPlayer.PlaybackEnded -= OnVocabularyAudioEnded;
            // Don't dispose immediately - can cause crashes on some platforms
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
                if (State.VocabularyAudioPlayer.IsPlaying)
                {
                    State.VocabularyAudioPlayer.Stop();
                }
                _logger.LogDebug("✅ Audio player stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exception stopping audio player");
            }
        }
    }
}
