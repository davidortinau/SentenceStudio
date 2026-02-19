using MauiReactor.Parameters;
using ReactorCustomLayouts;
using MauiReactor.Shapes;
using Microsoft.Maui.Layouts;
using SentenceStudio.Pages.Clozure;
using Button = MauiReactor.Button;
using SentenceStudio.Pages.MinimalPairs;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.VocabularyMatching;
using SentenceStudio.Pages.VocabularyProgress;
using SentenceStudio.Pages.VocabularyQuiz;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Repositories;
using SentenceStudio.Services.Progress;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

namespace SentenceStudio.Pages.Dashboard;

class DashboardParameters
{
    public List<LearningResource> SelectedResources { get; set; } = new();
    public SkillProfile SelectedSkillProfile { get; set; }
}

class DashboardPageState
{
    public List<LearningResource> Resources { get; set; } = [];
    public List<SkillProfile> SkillProfiles { get; set; } = [];

    public List<LearningResource> SelectedResources { get; set; } = [];
    public int SelectedSkillProfileIndex { get; set; } = -1;
    public int SelectedResourceIndex { get; set; } = -1;

    public DisplayOrientation Orientation { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Orientation;
    public double Width { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Width;
    public double Height { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Height;
    public double Density { get; set; } = DeviceDisplay.Current.MainDisplayInfo.Density;

    public VocabProgressSummary? VocabSummary { get; set; }
    public List<ResourceProgress> ResourceProgress { get; set; } = [];
    public SkillProgress? SelectedSkillProgress { get; set; }
    public List<PracticeHeatPoint> PracticeHeat { get; set; } = [];

    // PHASE 2 OPTIMIZATION: Loading states for lazy loading
    public bool IsLoadingProgress { get; set; } = false;
    public bool HasLoadedProgressOnce { get; set; } = false;

    // Today's Plan state
    public bool IsTodaysPlanMode { get; set; } = true; // Default to guided mode for habit formation
    public TodaysPlan? TodaysPlan { get; set; }
    public StreakInfo? StreakInfo { get; set; }
    public bool IsLoadingTodaysPlan { get; set; } = false;
}

partial class DashboardPage : Component<DashboardPageState>
{
    private const string PREF_SELECTED_RESOURCE_IDS = "SelectedResourceIds";
    private const string PREF_SELECTED_SKILL_PROFILE_ID = "SelectedSkillProfileId";
    private const string PREF_DASHBOARD_MODE = "DashboardMode"; // "TodaysPlan" or "ChooseOwn"

    [Inject] LearningResourceRepository _resourceRepository;
    [Inject] SkillProfileRepository _skillService;
    [Inject] IProgressService _progressService;
    [Inject] ILogger<DashboardPage> _logger;
    [Inject] MinimalPairRepository _minimalPairRepo;
    [Inject] NativeThemeService _themeService;

    [Param] IParameter<DashboardParameters> _parameters;

    LocalizationManager _localize => LocalizationManager.Instance;
    private int _progressFetchVersion = 0;

    // PHASE 2 OPTIMIZATION: Debounce timer for preference saves
    private System.Timers.Timer? _preferenceSaveTimer;
    private CancellationTokenSource? _progressLoadCts;

    protected override void OnMounted()
    {
        var info = DeviceDisplay.Current.MainDisplayInfo;

        // Load saved dashboard mode preference
        var savedMode = Preferences.Default.Get(PREF_DASHBOARD_MODE, "TodaysPlan");

        SetState(s =>
        {
            s.Orientation = info.Orientation;
            s.Width = info.Width;
            s.Height = info.Height;
            s.Density = info.Density;
            s.IsTodaysPlanMode = savedMode == "TodaysPlan";
        });
        DeviceDisplay.Current.MainDisplayInfoChanged += OnMainDisplayInfoChanged;
        _themeService.ThemeChanged += OnThemeChanged;
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        DeviceDisplay.Current.MainDisplayInfoChanged -= OnMainDisplayInfoChanged;
        _themeService.ThemeChanged -= OnThemeChanged;

        // PHASE 2 OPTIMIZATION: Clean up debounce timer and cancel any in-flight operations
        _preferenceSaveTimer?.Stop();
        _preferenceSaveTimer?.Dispose();
        _progressLoadCts?.Cancel();
        _progressLoadCts?.Dispose();

        base.OnWillUnmount();
    }

    private void OnMainDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e)
    {
        // var info = e.DisplayInfo;
        var info = DeviceDisplay.Current.MainDisplayInfo;

        // SetState triggers a rerender in MauiReactor
        SetState(s =>
        {
            s.Orientation = info.Orientation;
            s.Width = info.Width;
            s.Height = info.Height;
            s.Density = info.Density;
        });
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Invalidate();

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        SafeAreaEdges safeEdges = DeviceDisplay.Current.MainDisplayInfo.Rotation switch
        {
            DisplayRotation.Rotation0 => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None),
            DisplayRotation.Rotation90 => new(SafeAreaRegions.All, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None),
            DisplayRotation.Rotation180 => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None),
            DisplayRotation.Rotation270 => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.All, SafeAreaRegions.None),
            _ => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None)
        };

        return ContentPage($"{_localize["DashboardTitle"]}",
            State.IsTodaysPlanMode ? ToolbarItem($"{_localize["PlanRegenerateButton"] ?? "Regenerate"}")
                .Order(ToolbarItemOrder.Secondary)
                .OnClicked(() => _ = RegeneratePlanAsync()) : null,

            VScrollView(
                VStack(spacing: 16,
                    RenderWelcomeMessage(),
                    RenderModeToggle(),
                    State.IsTodaysPlanMode ? RenderTodaysPlanMode() : RenderChooseOwnMode()
                )
                .Padding(16)
            )
            .Set(Layout.SafeAreaEdgesProperty, safeEdges)
        )
        .Set(Layout.SafeAreaEdgesProperty, safeEdges)
        .BackgroundColor(BootstrapTheme.Current.GetBackground())
        .OnAppearing(LoadOrRefreshDataAsync);
    }

    VisualNode RenderWelcomeMessage()
    {
        // Desktop only - show welcome message on wider screens
        if (DeviceInfo.Idiom == DeviceIdiom.Phone || (State.Width / State.Density) < 600)
            return ContentView().HeightRequest(0);

        var userName = Preferences.Default.Get("UserProfile_Name", string.Empty);
        if (string.IsNullOrEmpty(userName))
            return ContentView().HeightRequest(0);

        return Label($"Welcome, {userName}!")
            .H1()
            .Margin(new Thickness(0, 0, 0, 8));
    }

    VisualNode RenderModeToggle()
    {
        var theme = BootstrapTheme.Current;
        return new SegmentedButtonGroup()
            .Left(RenderToggleButton(
                $"{_localize["ModeTodaysPlan"]}",
                BootstrapIcons.CalendarCheck,
                State.IsTodaysPlanMode,
                theme,
                () =>
                {
                    SetState(st => st.IsTodaysPlanMode = true);
                    Preferences.Default.Set(PREF_DASHBOARD_MODE, "TodaysPlan");
                    _ = LoadTodaysPlanAsync();
                }))
            .Right(RenderToggleButton(
                $"{_localize["ModeChooseOwn"]}",
                BootstrapIcons.Sliders,
                !State.IsTodaysPlanMode,
                theme,
                () =>
                {
                    SetState(st => st.IsTodaysPlanMode = false);
                    Preferences.Default.Set(PREF_DASHBOARD_MODE, "ChooseOwn");
                }))
            .CornerRadius(6)
            .Margin(new Thickness(0, 0, 0, 16));
    }

    Button RenderToggleButton(string text, string icon, bool isActive, BootstrapTheme theme, Action onClicked)
    {
        var iconColor = isActive ? theme.OnPrimary : theme.GetOnBackground();
        var btn = Button()
            .Text(text)
            .ImageSource(BootstrapIcons.Create(icon, iconColor, 16))
            .HeightRequest(44)
            .HFill()
            .OnClicked(onClicked);

        btn = isActive
            ? btn.Primary()
            : btn.Background(new SolidColorBrush(Colors.Transparent))
                 .TextColor(theme.GetOnBackground());

        return btn.CornerRadius(0).BorderWidth(0);
    }

    VisualNode RenderTodaysPlanMode()
    {
        if (State.IsLoadingTodaysPlan)
        {
            return VStack(spacing: 8,
                ActivityIndicator()
                    .IsRunning(true)
                    .Primary()
                    .HeightRequest(50)
                    .HCenter(),
                Label("Loading today's plan...")
                    .Muted()
                    .HCenter()
            )
            .PaddingLevel(4)
            .Margin(new Thickness(0, 0, 0, 16));
        }

        if (State.TodaysPlan == null)
        {
            return Border(
                VStack(spacing: 16,
                    Image()
                        .Source(BootstrapIcons.Create(BootstrapIcons.CalendarPlus, BootstrapTheme.Current.GetMuted(), 48))
                        .HeightRequest(48)
                        .HCenter(),
                    Label("No plan for today yet.")
                        .H5()
                        .HCenter(),
                    Label("Generate your personalized learning plan to get started.")
                        .Muted()
                        .HCenter()
                        .HorizontalTextAlignment(TextAlignment.Center),
                    Button("Generate Plan")
                        .Primary()
                        .HeightRequest(44)
                        .HCenter()
                        .OnClicked(LoadTodaysPlanAsync)
                )
                .PaddingLevel(4)
                .HCenter()
            )
            .Class("card")
            .Margin(new Thickness(0, 0, 0, 16));
        }

        return VStack(spacing: 16,
            RenderStreakBadge(),
            RenderProgressCard(),
            RenderPlanItems(),
            RenderVocabularyStats()
        );
    }

    VisualNode RenderStreakBadge()
    {
        var streak = State.TodaysPlan?.Streak;
        if (streak == null || streak.CurrentStreak <= 0)
            return ContentView().HeightRequest(0);

        return HStack(spacing: 8,
            Label($"{BootstrapIcons.Fire} {streak.CurrentStreak} day streak")
                .Badge(BootstrapVariant.Warning)
                .FontSize(16),
            streak.LongestStreak > streak.CurrentStreak 
                ? Label($"Best: {streak.LongestStreak} days")
                    .Muted()
                    .FontSize(14)
                : null
        )
        .Margin(new Thickness(0, 0, 0, 8));
    }

    VisualNode RenderProgressCard()
    {
        var plan = State.TodaysPlan;
        if (plan == null)
            return ContentView().HeightRequest(0);

        return Border(
            VStack(spacing: 8,
                Grid("Auto", "*,Auto",
                    Label("Today's Progress")
                        .FontSize(14)
                        .FontAttributes(FontAttributes.Bold)
                        .GridColumn(0),
                    Label($"{plan.CompletedCount} / {plan.TotalCount}")
                        .Muted()
                        .Small()
                        .GridColumn(1)
                ),
                ProgressBar()
                    .Progress(plan.CompletionPercentage / 100.0)
                    .BootstrapHeight()
                    .HeightRequest(8)
                    .Success(),
                !string.IsNullOrEmpty(plan.Rationale)
                    ? Label(plan.Rationale)
                        .Muted()
                        .FontSize(14)
                    : null
            )
            .PaddingLevel(3)
        )
        .Class("card")
        .Margin(new Thickness(0, 0, 0, 8));
    }

    VisualNode RenderPlanItems()
    {
        var plan = State.TodaysPlan;
        if (plan?.Items == null || !plan.Items.Any())
            return ContentView().HeightRequest(0);

        return VStack(spacing: 8,
            plan.Items.Select(item => RenderPlanItem(item)).ToArray()
        );
    }

    VisualNode RenderPlanItem(DailyPlanItem item)
    {
        var icon = item.IsCompleted
            ? BootstrapIcons.CheckCircleFill
            : GetActivityIcon(item.ActivityType);

        var iconColor = item.IsCompleted
            ? BootstrapTheme.Current.Success
            : BootstrapTheme.Current.Primary;

        return Border(
            HStack(spacing: 12,
                Image()
                    .Source(BootstrapIcons.Create(icon, iconColor, 24))
                    .HeightRequest(24)
                    .WidthRequest(24)
                    .VCenter(),
                VStack(spacing: 4,
                    Label(GetActivityLabel(item.ActivityType))
                        .FontSize(14)
                        .FontAttributes(FontAttributes.Bold)
                        .When(item.IsCompleted, l => l.TextDecorations(TextDecorations.Strikethrough)),
                    HStack(spacing: 4,
                        !string.IsNullOrEmpty(item.ResourceTitle)
                            ? Label(item.ResourceTitle)
                                .Muted()
                                .Small()
                            : null,
                        !string.IsNullOrEmpty(item.ResourceTitle)
                            ? Label("·")
                                .Muted()
                                .Small()
                            : null,
                        Label($"~{item.EstimatedMinutes} min")
                            .Muted()
                            .Small(),
                        item.VocabDueCount.HasValue && item.VocabDueCount > 0
                            ? Label("·")
                                .Muted()
                                .Small()
                            : null,
                        item.VocabDueCount.HasValue && item.VocabDueCount > 0
                            ? Label($"{item.VocabDueCount} words")
                                .Muted()
                                .Small()
                            : null
                    )
                )
                .VCenter()
                .HFill(),
                Image()
                    .Source(BootstrapIcons.Create(BootstrapIcons.ChevronRight, BootstrapTheme.Current.GetMuted(), 20))
                    .HeightRequest(20)
                    .WidthRequest(20)
                    .VCenter()
            )
            .PaddingLevel(3)
        )
        .Class("card")
        .When(item.IsCompleted, b => b.Opacity(0.75))
        .OnTapped(() => _ = OnPlanItemTapped(item));
    }

    string GetActivityIcon(PlanActivityType activityType)
    {
        return activityType switch
        {
            PlanActivityType.VocabularyReview => BootstrapIcons.CardChecklist,
            PlanActivityType.Reading => BootstrapIcons.Book,
            PlanActivityType.Listening => BootstrapIcons.Soundwave,
            PlanActivityType.VideoWatching => BootstrapIcons.PlayCircle,
            PlanActivityType.Shadowing => BootstrapIcons.Soundwave,
            PlanActivityType.Cloze => BootstrapIcons.Puzzle,
            PlanActivityType.Translation => BootstrapIcons.Translate,
            PlanActivityType.Conversation => BootstrapIcons.ChatDots,
            PlanActivityType.VocabularyGame => BootstrapIcons.Grid3X3Gap,
            _ => BootstrapIcons.Circle
        };
    }

    string GetActivityLabel(PlanActivityType activityType)
    {
        return activityType switch
        {
            PlanActivityType.VocabularyReview => $"{_localize["VocabularyQuiz"]}",
            PlanActivityType.Reading => $"{_localize["Reading"]}",
            PlanActivityType.Listening => $"{_localize["Listening"]}",
            PlanActivityType.VideoWatching => "Watch Video",
            PlanActivityType.Shadowing => $"{_localize["Shadowing"]}",
            PlanActivityType.Cloze => $"{_localize["Clozures"]}",
            PlanActivityType.Translation => $"{_localize["Translate"]}",
            PlanActivityType.Conversation => $"{_localize["Conversation"]}",
            PlanActivityType.VocabularyGame => $"{_localize["VocabularyMatchingTitle"]}",
            _ => "Activity"
        };
    }

    VisualNode RenderChooseOwnMode()
    {
        return VStack(spacing: 16,
            RenderSelectors(),
            Label($"{_localize["Activities"]}")
                .H5()
                .FontAttributes(FontAttributes.Bold)
                .Margin(new Thickness(0, 8, 0, 8)),
            RenderActivityCards(),
            RenderVocabularyStats()
        );
    }

    VisualNode RenderSelectors()
    {
        var theme = BootstrapTheme.Current;

        var resourcePicker = Picker()
            .Title("Select resource")
            .ItemsSource(State.Resources?.Select(r => r.Title ?? $"Resource {r.Id}").ToList() ?? new List<string>())
            .SelectedIndex(State.SelectedResourceIndex >= 0 && State.SelectedResourceIndex < (State.Resources?.Count ?? 0) ? State.SelectedResourceIndex : -1)
            .OnSelectedIndexChanged(OnResourcePickerChanged)
            .FormSelect()
            .HFill()
            .HeightRequest(44);

        var skillPicker = Picker()
            .Title("Select skill")
            .ItemsSource(State.SkillProfiles?.Select(s => s.Title ?? $"Skill {s.Id}").ToList() ?? new List<string>())
            .SelectedIndex(State.SelectedSkillProfileIndex >= 0 && State.SelectedSkillProfileIndex < (State.SkillProfiles?.Count ?? 0) ? State.SelectedSkillProfileIndex : -1)
            .OnSelectedIndexChanged(OnSkillPickerChanged)
            .FormSelect()
            .HFill()
            .HeightRequest(44);

        return Border(
            VStack(spacing: 16,
                VStack(spacing: 4,
                    Label($"{_localize["LearningResources"]}")
                        .Class("form-label")
                        .FontAttributes(FontAttributes.Bold),
                    resourcePicker
                ),
                VStack(spacing: 4,
                    Label($"{_localize["SkillProfiles"]}")
                        .Class("form-label")
                        .FontAttributes(FontAttributes.Bold),
                    skillPicker
                )
            )
            .PaddingLevel(3)
        )
        .Class("card")
        .Margin(new Thickness(0, 0, 0, 16));
    }

    VisualNode RenderActivityCards()
    {
        var activities = new[]
        {
            (Label: $"{_localize["Conversation"]}", Icon: BootstrapIcons.ChatDots, Route: "conversation", IsSpecial: true),
            (Label: $"{_localize["DescribeAScene"]}", Icon: BootstrapIcons.Image, Route: nameof(DescribeAScenePage), IsSpecial: false),
            (Label: $"{_localize["Translate"]}", Icon: BootstrapIcons.Translate, Route: nameof(TranslationPage), IsSpecial: false),
            (Label: $"{_localize["Write"]}", Icon: BootstrapIcons.PencilSquare, Route: nameof(WritingPage), IsSpecial: false),
            (Label: $"{_localize["Clozures"]}", Icon: BootstrapIcons.Puzzle, Route: nameof(ClozurePage), IsSpecial: false),
            (Label: $"{_localize["Reading"]}", Icon: BootstrapIcons.Book, Route: "reading", IsSpecial: false),
            (Label: $"{_localize["VocabularyQuiz"]}", Icon: BootstrapIcons.CardChecklist, Route: nameof(VocabularyQuizPage), IsSpecial: false),
            (Label: $"{_localize["VocabularyMatchingTitle"]}", Icon: BootstrapIcons.Grid3X3Gap, Route: nameof(VocabularyMatchingPage), IsSpecial: false),
            (Label: $"{_localize["Shadowing"]}", Icon: BootstrapIcons.Soundwave, Route: "shadowing", IsSpecial: false),
            (Label: $"{_localize["HowDoYouSay"]}", Icon: BootstrapIcons.ChatLeftDots, Route: "howdoyousay", IsSpecial: true),
            (Label: $"{_localize["MinimalPairsTitle"]}", Icon: BootstrapIcons.Ear, Route: "minimalpairs", IsSpecial: true)
        };

        double screenWidth = State.Width / State.Density;
        int columns = screenWidth >= 900 ? 4 : (screenWidth >= 600 ? 3 : 2);
        int rows = (int)Math.Ceiling((double)activities.Length / columns);
        string rowDefs = string.Join(",", Enumerable.Repeat("Auto", rows));
        string colDefs = string.Join(",", Enumerable.Repeat("*", columns));

        return Grid(rowDefs, colDefs,
            activities.Select((activity, index) => 
                RenderActivityCard(activity.Label, activity.Icon, activity.Route, activity.IsSpecial)
                    .GridRow(index / columns)
                    .GridColumn(index % columns)
            ).ToArray()
        )
        .ColumnSpacing(12)
        .RowSpacing(12);
    }

    VisualNode RenderActivityCard(string label, string icon, string route, bool isSpecial)
    {
        var theme = BootstrapTheme.Current;
        return Border(
            VStack(spacing: 8,
                Image()
                    .Source(BootstrapIcons.Create(icon, theme.GetOnBackground(), 28))
                    .HeightRequest(28)
                    .HCenter(),
                Label(label)
                    .FontSize(14)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(theme.GetOnBackground())
                    .HCenter()
                    .HorizontalTextAlignment(TextAlignment.Center)
            )
            .Padding(16)
            .HCenter()
            .VCenter()
        )
        .Class("card")
        .HeightRequest(120)
        .OnTapped(async () =>
        {
            if (isSpecial)
            {
                await HandleSpecialActivity(route);
            }
            else
            {
                await HandleStandardActivity(route);
            }
        });
    }

    async Task HandleSpecialActivity(string route)
    {
        // HowDoYouSay doesn't require resources/skills
        if (route == "howdoyousay")
        {
            await MauiControls.Shell.Current.GoToAsync(route);
            return;
        }

        // Minimal Pairs launches session directly
        if (route == "minimalpairs")
        {
            try
            {
                var pairs = await _minimalPairRepo.GetUserPairsAsync(1);
                if (pairs.Count == 0)
                {
                    await IPopupService.Current.PushAsync(new SimpleActionPopup
                    {
                        Title = $"{_localize["MinimalPairsTitle"]}",
                        Text = $"{_localize["MinimalPairsEmptyState"]}",
                        ActionButtonText = $"{_localize["OK"]}",
                        ShowSecondaryActionButton = false
                    });
                    return;
                }

                await MauiControls.Shell.Current.GoToAsync<MinimalPairSessionPageProps>(
                    nameof(MinimalPairSessionPage),
                    props =>
                    {
                        props.PairIds = pairs.Select(p => p.Id).ToArray();
                        props.Mode = "Mixed";
                        props.PlannedTrialCount = 20;
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch minimal pairs session");
            }
            return;
        }

        // Conversation just navigates
        await MauiControls.Shell.Current.GoToAsync(route);
    }

    async Task HandleStandardActivity(string route)
    {
        if (_parameters.Value.SelectedResources?.Any() != true)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Ahoy!",
                Text = "Ye need to select at least one learning resource first, matey!",
                ActionButtonText = "Aye!",
                ShowSecondaryActionButton = false
            });
            return;
        }

        if (_parameters.Value.SelectedSkillProfile == null)
        {
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Avast!",
                Text = "Choose yer skill profile first, ye scallywag!",
                ActionButtonText = "Aye!",
                ShowSecondaryActionButton = false
            });
            return;
        }

        await MauiControls.Shell.Current.GoToAsync<ActivityProps>(
            route,
            props =>
            {
                props.Resources = _parameters.Value.SelectedResources?.ToList() ?? new List<LearningResource>();
                props.Skill = _parameters.Value.SelectedSkillProfile;
            }
        );
    }

    VisualNode RenderVocabularyStats()
    {
        return VStack(spacing: 12,
            Label($"{_localize["VocabProgress"]}")
                .H5()
                .FontAttributes(FontAttributes.Bold)
                .Margin(new Thickness(0, 16, 0, 4)),
            State.IsLoadingProgress && !State.HasLoadedProgressOnce
                ? VStack(spacing: 8,
                    ActivityIndicator()
                        .IsRunning(true)
                        .Primary()
                        .HeightRequest(50)
                        .HCenter(),
                    Label("Loading progress data...")
                        .Muted()
                        .HCenter()
                )
                .PaddingLevel(4)
                : (State.VocabSummary != null
                    ? RenderVocabCards()
                    : Border(
                        Label("No vocabulary data yet. Start practicing!")
                            .Muted()
                            .HCenter()
                            .PaddingLevel(4)
                    )
                    .Class("card")
                )
        );
    }

    VisualNode RenderVocabCards()
    {
        var summary = State.VocabSummary;
        if (summary == null)
            return ContentView().HeightRequest(0);

        var total = summary.New + summary.Learning + summary.Review + summary.Known;

        double screenWidth = State.Width / State.Density;
        int columns = screenWidth >= 600 ? 4 : 2;

        return VStack(spacing: 12,
            Grid("Auto,Auto", Enumerable.Repeat("*", columns).Aggregate((a, b) => $"{a},{b}"),
                RenderVocabStatCard("New", summary.New, BootstrapVariant.Primary)
                    .GridRow(0)
                    .GridColumn(0),
                RenderVocabStatCard("Learning", summary.Learning, BootstrapVariant.Warning)
                    .GridRow(0)
                    .GridColumn(1),
                RenderVocabStatCard("Review", summary.Review, BootstrapVariant.Warning)
                    .GridRow(columns >= 4 ? 0 : 1)
                    .GridColumn(columns >= 4 ? 2 : 0),
                RenderVocabStatCard("Known", summary.Known, BootstrapVariant.Success)
                    .GridRow(columns >= 4 ? 0 : 1)
                    .GridColumn(columns >= 4 ? 3 : 1)
            )
            .ColumnSpacing(12)
            .RowSpacing(12),
            total > 0
                ? Border(
                    Grid("Auto", "*,Auto",
                        Label($"Total words: {total}")
                            .FontSize(14)
                            .FontAttributes(FontAttributes.Bold)
                            .GridColumn(0),
                        Label($"7-day accuracy: {Math.Round(summary.SuccessRate7d * 100)}%")
                            .Muted()
                            .Small()
                            .GridColumn(1)
                    )
                    .PaddingLevel(3)
                )
                .Class("card")
                : null
        );
    }

    VisualNode RenderVocabStatCard(string label, int count, BootstrapVariant variant)
    {
        var theme = BootstrapTheme.Current;
        var numberColor = theme.GetVariantColor(variant);
        return Border(
            VStack(spacing: 4,
                Label(count.ToString())
                    .FontSize(28)
                    .FontAttributes(FontAttributes.Bold)
                    .TextColor(numberColor)
                    .HCenter(),
                Label(label)
                    .Muted()
                    .Small()
                    .HCenter()
            )
            .PaddingLevel(3)
            .HCenter()
            .VCenter()
        )
        .Class("card")
        .OnTapped(() =>
        {
            var filterType = label switch
            {
                "New" => VocabularyFilterType.Unknown,
                "Learning" => VocabularyFilterType.Learning,
                "Review" => VocabularyFilterType.Learning,
                "Known" => VocabularyFilterType.Known,
                _ => VocabularyFilterType.All
            };
            NavigateToVocabularyProgress(filterType);
        });
    }

    async Task LoadOrRefreshDataAsync()
    {
        //Console.Writeline(">> DashboardPage OnAppearing <<");
        // PHASE 1 OPTIMIZATION: Use lightweight query for resources (no vocabulary loaded) FOR DROPDOWN ONLY
        var resourcesLightweight = await _resourceRepository.GetAllResourcesLightweightAsync();
        var skills = await _skillService.ListAsync();

        // Check if we have existing parameter values (from navigation) or load from preferences
        var existingSelectedResources = _parameters.Value?.SelectedResources;
        var existingSelectedSkill = _parameters.Value?.SelectedSkillProfile;

        List<LearningResource> selectedResources;
        SkillProfile selectedSkill;

        if (existingSelectedResources?.Any() == true && existingSelectedSkill != null)
        {
            // Use existing parameter values (e.g., from navigation)
            selectedResources = existingSelectedResources;
            selectedSkill = existingSelectedSkill;
            _logger.LogDebug("🏴‍☠️ Using existing parameter values");
        }
        else
        {
            // Load from preferences or use defaults (this gives us lightweight resources)
            (selectedResources, selectedSkill) = await LoadUserSelectionsFromPreferences(resourcesLightweight, skills);

            // CRITICAL FIX: Reload selected resources WITH vocabulary for activities
            if (selectedResources?.Any() == true)
            {
                var selectedIds = selectedResources.Select(r => r.Id).ToList();
                var fullResources = new List<LearningResource>();
                foreach (var id in selectedIds)
                {
                    var fullResource = await _resourceRepository.GetResourceAsync(id);
                    if (fullResource != null)
                        fullResources.Add(fullResource);
                }
                selectedResources = fullResources;
                _logger.LogDebug("🏴‍☠️ Reloaded {Count} resources WITH vocabulary for activities", fullResources.Count);
            }
        }

        // Set the parameter values with FULL resources (including vocabulary)
        _parameters.Set(p =>
        {
            p.SelectedResources = selectedResources;
            p.SelectedSkillProfile = selectedSkill;
        });

        // Calculate indices for the selected items (using lightweight resources for dropdown)
        var selectedResourceIndex = -1;
        var selectedSkillIndex = -1;

        if (selectedResources?.Any() == true)
        {
            var firstSelected = selectedResources.First();
            for (int i = 0; i < resourcesLightweight.Count; i++)
            {
                if (resourcesLightweight[i].Id == firstSelected.Id)
                {
                    selectedResourceIndex = i;
                    break;
                }
            }
        }

        if (selectedSkill != null)
        {
            for (int i = 0; i < skills.Count; i++)
            {
                if (skills[i].Id == selectedSkill.Id)
                {
                    selectedSkillIndex = i;
                    break;
                }
            }
        }

        // PHASE 2 OPTIMIZATION: Update UI immediately with resources/skills, then load progress asynchronously
        // Use lightweight resources for dropdown, but full resources are in parameters for navigation
        SetState(s =>
        {
            s.Resources = resourcesLightweight;
            s.SkillProfiles = skills;
            s.SelectedResources = selectedResources ?? new List<LearningResource>();
            s.SelectedSkillProfileIndex = selectedSkillIndex >= 0 ? selectedSkillIndex : (skills.Any() ? 0 : -1);
            s.SelectedResourceIndex = selectedResourceIndex >= 0 ? selectedResourceIndex : (resourcesLightweight.Any() ? 0 : -1);
        });

        // Debug logging to verify state
        _logger.LogDebug("🏴‍☠️ State set - Selected Resources Count: {Count}", State.SelectedResources.Count);
        _logger.LogDebug("🏴‍☠️ State set - Selected Resource Index: {Index}", State.SelectedResourceIndex);
        _logger.LogDebug("🏴‍☠️ State set - Selected Skill Index: {Index}", State.SelectedSkillProfileIndex);
        if (State.SelectedResources.Any())
        {
            _logger.LogDebug("🏴‍☠️ Selected resource titles: {Titles}", string.Join(", ", State.SelectedResources.Select(r => r.Title)));
        }

        // Load progress data asynchronously without blocking UI
        _ = RefreshProgressDataAsync(selectedSkill?.Id);

        // Load today's plan if in that mode
        if (State.IsTodaysPlanMode)
        {
            _logger.LogDebug("📅 Dashboard OnAppearing - scheduling plan reload with delay");
            // CRITICAL: Delay plan reload to allow previous activity page to complete unmount and save progress
            // This prevents race condition where we load plan before the activity saves its progress
            _ = Task.Run(async () =>
            {
                await Task.Delay(300); // Wait for activity unmount to complete
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    _logger.LogDebug("📅 Dashboard - delayed plan reload executing NOW");
                    await LoadTodaysPlanAsync();
                });
            });
        }
        else
        {
            _logger.LogDebug("📅 Dashboard OnAppearing - in Choose Own mode, not loading plan");
        }
    }

    private async Task RefreshProgressDataAsync(int? skillId)
    {
        _logger.LogDebug("🏴‍☠️ RefreshProgressDataAsync called");
        // PHASE 2 OPTIMIZATION: Cancel any previous in-flight progress loads
        await (_progressLoadCts?.CancelAsync() ?? Task.CompletedTask);
        _progressLoadCts?.Dispose();
        _progressLoadCts = new CancellationTokenSource();
        var ct = _progressLoadCts.Token;

        int myVersion = Interlocked.Increment(ref _progressFetchVersion);

        // Set loading state
        SetState(s => s.IsLoadingProgress = true);

        try
        {
            var vocabFromUtc = DateTime.UtcNow.AddDays(-30);
            var heatFromUtc = DateTime.UtcNow.AddDays(-364);
            var heatToUtc = DateTime.UtcNow;

            var vocabTask = _progressService.GetVocabSummaryAsync(vocabFromUtc, ct);
            var resourceTask = _progressService.GetRecentResourceProgressAsync(vocabFromUtc, 3, ct);
            var heatTask = _progressService.GetPracticeHeatAsync(heatFromUtc, heatToUtc, ct); // full year for heat map
            Task<SentenceStudio.Services.Progress.SkillProgress?> skillTask = Task.FromResult<SentenceStudio.Services.Progress.SkillProgress?>(null);
            if (skillId.HasValue)
            {
                skillTask = _progressService.GetSkillProgressAsync(skillId.Value, ct);
            }

            await Task.WhenAll(vocabTask, resourceTask, heatTask, skillTask);

            // If a newer request started meanwhile, abandon these results
            if (myVersion != _progressFetchVersion || ct.IsCancellationRequested) return;

            _logger.LogDebug("🏴‍☠️ Setting progress data in state - VocabSummary: New={New}, Learning={Learning}, Review={Review}, Known={Known}", vocabTask.Result.New, vocabTask.Result.Learning, vocabTask.Result.Review, vocabTask.Result.Known);
            _logger.LogDebug("🏴‍☠️ PracticeHeat has {Count} data points", heatTask.Result.Count());

            SetState(st =>
            {
                st.VocabSummary = vocabTask.Result;
                st.ResourceProgress = resourceTask.Result;
                st.SelectedSkillProgress = skillTask.Result;
                st.PracticeHeat = heatTask.Result.ToList();
                st.IsLoadingProgress = false;
                st.HasLoadedProgressOnce = true;
            });

            _logger.LogDebug("🏴‍☠️ State updated - VocabSummary is {Status}", State.VocabSummary != null ? "NOT NULL" : "NULL");
            _logger.LogDebug("🏴‍☠️ State updated - PracticeHeat count: {Count}", State.PracticeHeat?.Count ?? 0);
            _logger.LogDebug("🏴‍☠️ State updated - HasLoadedProgressOnce: {HasLoaded}", State.HasLoadedProgressOnce);
            _logger.LogDebug("🏴‍☠️ Progress data loaded - VocabSummary is {Status}, PracticeHeat count: {Count}", State.VocabSummary != null ? "not null" : "null", State.PracticeHeat.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Progress data load cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Progress data load error");
            SetState(s => s.IsLoadingProgress = false);
        }
    }

    async Task LoadTodaysPlanAsync()
    {
        _logger.LogInformation("🚀 LoadTodaysPlanAsync - START");

        SetState(s => s.IsLoadingTodaysPlan = true);

        try
        {
            _logger.LogDebug("📊 Calling GenerateTodaysPlanAsync...");
            var plan = await _progressService.GenerateTodaysPlanAsync();

            _logger.LogInformation("✅ Plan loaded - Items: {ItemCount}", plan?.Items?.Count ?? 0);
            if (plan != null)
            {
                _logger.LogDebug("📊 Plan completion: {Percentage:F1}%", plan.CompletionPercentage);
                _logger.LogDebug("⏱️ Total minutes: {Spent} / {Total}", plan.Items.Sum(i => i.MinutesSpent), plan.EstimatedTotalMinutes);

                foreach (var item in plan.Items)
                {
                    _logger.LogDebug("  • {TitleKey}: {MinutesSpent}/{EstimatedMinutes} min, Completed={IsCompleted}", item.TitleKey, item.MinutesSpent, item.EstimatedMinutes, item.IsCompleted);
                }
            }

            SetState(s =>
            {
                s.TodaysPlan = plan;
                s.StreakInfo = plan?.Streak; // Streak is part of TodaysPlan
                s.IsLoadingTodaysPlan = false;
            });

            _logger.LogInformation("✅ LoadTodaysPlanAsync - COMPLETE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error loading today's plan");
            SetState(s => s.IsLoadingTodaysPlan = false);

            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Arrr!",
                Text = "Failed to load today's plan. Try again, ye scallywag!",
                ActionButtonText = "Aye",
                ShowSecondaryActionButton = false
            });
        }
    }

    async Task RegeneratePlanAsync()
    {
        _logger.LogInformation("🔄 RegeneratePlanAsync - clearing cache and generating fresh plan");

        // Clear the cached plan to force LLM regeneration
        await _progressService.ClearCachedPlanAsync(DateTime.UtcNow.Date);

        // Now load/generate a fresh plan
        await LoadTodaysPlanAsync();
    }

    async Task OnPlanItemTapped(DailyPlanItem item)
    {
        try
        {
            _logger.LogDebug("🎯 OnPlanItemTapped called with item: {TitleKey}", item?.TitleKey ?? "NULL");
            _logger.LogDebug("🎯 ActivityType: {ActivityType}", item?.ActivityType);

            // Map activity type to route
            var route = item.ActivityType switch
            {
                PlanActivityType.VocabularyReview => nameof(VocabularyQuizPage),
                PlanActivityType.Reading => "reading",
                PlanActivityType.Listening => "reading", // ReadingPage handles audio playback for listening
                PlanActivityType.VideoWatching => await HandleVideoActivity(item),
                PlanActivityType.Shadowing => "shadowing",
                PlanActivityType.Cloze => nameof(ClozurePage),
                PlanActivityType.Translation => nameof(TranslationPage),
                PlanActivityType.Conversation => null, // TODO: Implement conversation page
                PlanActivityType.VocabularyGame => nameof(VocabularyMatchingPage),
                _ => null
            };

            _logger.LogDebug("🎯 OnPlanItemTapped: Mapped route = '{Route}'", route);

            if (!string.IsNullOrEmpty(route))
            {
                _logger.LogDebug("✅ Route is not empty, proceeding with resource loading...");

                // Load resource and skill from plan item's RouteParameters
                List<LearningResource>? resourcesToUse = null;
                SkillProfile? skillToUse = null;

                _logger.LogDebug("🔍 Checking for ResourceId in plan item...");
                _logger.LogDebug("🔍 ActivityType: {ActivityType}", item.ActivityType);
                _logger.LogDebug("🔍 RouteParameters null? {IsNull}", item.RouteParameters == null);
                _logger.LogDebug("🔍 RouteParameters count: {Count}", item.RouteParameters?.Count ?? 0);

                // Check for ResourceId in route parameters for ANY activity type
                if (item.RouteParameters?.ContainsKey("ResourceId") == true)
                {
                    _logger.LogDebug("✅ Plan item with ResourceId detected for {ActivityType}", item.ActivityType);

                    try
                    {
                        _logger.LogDebug("🔍 RouteParameters['ResourceId'] value: {Value}", item.RouteParameters["ResourceId"]);
                        _logger.LogDebug("🔍 RouteParameters['ResourceId'] type: {Type}", item.RouteParameters["ResourceId"]?.GetType().Name ?? "NULL");

                        var resourceId = Convert.ToInt32(item.RouteParameters["ResourceId"]);
                        _logger.LogDebug("📝 ResourceId = {ResourceId}", resourceId);

                        // Load resource from database (don't depend on selected resources)
                        var dbResource = await _resourceRepository.GetResourceAsync(resourceId);
                        if (dbResource != null)
                        {
                            resourcesToUse = new List<LearningResource> { dbResource };
                            _logger.LogInformation("✅ Loaded resource from DB for plan: {Title} (ID: {Id})", dbResource.Title, dbResource.Id);
                        }
                        else
                        {
                            _logger.LogError("❌ ResourceId {ResourceId} not found in database", resourceId);
                            await IPopupService.Current.PushAsync(new SimpleActionPopup
                            {
                                Title = "Arrr!",
                                Text = $"The resource for this activity be missin' from the database (ID: {resourceId}). Try regeneratin' yer plan!",
                                ActionButtonText = "Aye!",
                                ShowSecondaryActionButton = false
                            });
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ CRITICAL ERROR loading resource from plan");
                        await IPopupService.Current.PushAsync(new SimpleActionPopup
                        {
                            Title = "Shiver me timbers!",
                            Text = "Failed to load the resource for this activity. Try again!",
                            ActionButtonText = "Aye!",
                            ShowSecondaryActionButton = false
                        });
                        return;
                    }
                }
                else
                {
                    // No ResourceId in plan - fallback to selected resources (Choose My Own mode)
                    if (_parameters.Value?.SelectedResources?.Any() != true)
                    {
                        _logger.LogError("❌ No ResourceId in plan and no selected resources");
                        await IPopupService.Current.PushAsync(new SimpleActionPopup
                        {
                            Title = "Ahoy!",
                            Text = "Ye need to select at least one learning resource first, matey!",
                            ActionButtonText = "Aye!",
                            ShowSecondaryActionButton = false
                        });
                        return;
                    }
                    resourcesToUse = _parameters.Value.SelectedResources?.ToList() ?? new List<LearningResource>();
                    _logger.LogDebug("📚 Using selected resources (count: {Count})", resourcesToUse.Count);
                }

                // Load skill from plan or fallback to selected skill or first available skill
                if (item.RouteParameters?.ContainsKey("SkillId") == true)
                {
                    try
                    {
                        var skillId = Convert.ToInt32(item.RouteParameters["SkillId"]);
                        skillToUse = await _skillService.GetAsync(skillId);
                        if (skillToUse != null)
                        {
                            _logger.LogDebug("✅ Loaded skill from plan: {Title} (ID: {Id})", skillToUse.Title, skillToUse.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ Failed to load skill from plan, will use fallback");
                    }
                }

                // Fallback to selected skill or first available skill
                if (skillToUse == null)
                {
                    // Try selected skill first
                    skillToUse = _parameters.Value?.SelectedSkillProfile;

                    if (skillToUse != null)
                    {
                        _logger.LogDebug("📚 Using selected skill: {Title}", skillToUse.Title);
                    }
                    else
                    {
                        // No selected skill - load first available skill from database
                        _logger.LogWarning("⚠️ No skill in plan and no selected skill - loading default skill");
                        var availableSkills = await _skillService.ListAsync();
                        skillToUse = availableSkills.FirstOrDefault();

                        if (skillToUse != null)
                        {
                            _logger.LogInformation("✅ Using default skill: {Title} (ID: {Id})", skillToUse.Title, skillToUse.Id);
                        }
                        else
                        {
                            _logger.LogError("❌ No skills found in database");
                            await IPopupService.Current.PushAsync(new SimpleActionPopup
                            {
                                Title = "Avast!",
                                Text = "No skill profiles found in the database. Create one first!",
                                ActionButtonText = "Aye!",
                                ShowSecondaryActionButton = false
                            });
                            return;
                        }
                    }
                }

                _logger.LogInformation("🚀 OnPlanItemTapped: Navigating to {Route}...", route);
                _logger.LogDebug("🚀 Resources to use: {Count} resources", resourcesToUse?.Count ?? 0);
                _logger.LogDebug("🚀 Skill to use: {Skill}", skillToUse?.Title ?? "NULL");
                await MauiControls.Shell.Current.GoToAsync<ActivityProps>(
                    route,
                    props =>
                    {
                        _logger.LogDebug("🔧 OnPlanItemTapped: Configuring ActivityProps...");
                        props.Resources = resourcesToUse;
                        props.Skill = skillToUse;
                        props.FromTodaysPlan = true;  // Enable timer for Today's Plan activities
                        props.PlanItemId = item.Id;   // Track which plan item this is
                    }
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ FATAL ERROR in OnPlanItemTapped");
            await IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Error",
                Text = $"Failed to start activity: {ex.Message}",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
        }
    }

    Task<string> HandleVideoActivity(DailyPlanItem item)
    {
        // Navigate to VideoWatchingPage which handles loading the resource and displaying the video
        // The page will use the ResourceId from RouteParameters to load the video URL
        _logger.LogDebug("🎬 HandleVideoActivity: Returning VideoWatchingPage route");
        return Task.FromResult(nameof(SentenceStudio.Pages.VideoWatching.VideoWatchingPage));
    }

    private void OnResourcePickerChanged(int selectedIndex)
    {
        try
        {
            SetState(s =>
            {
                s.SelectedResourceIndex = selectedIndex;
                if (selectedIndex >= 0 && selectedIndex < s.Resources.Count)
                {
                    s.SelectedResources = new List<LearningResource> { s.Resources[selectedIndex] };
                }
                else
                {
                    s.SelectedResources = new List<LearningResource>();
                }
            });

            // Reload selected resources WITH vocabulary for activities
            var selected = State.SelectedResources;
            Task.Run(async () =>
            {
                if (selected?.Any() == true)
                {
                    var selectedIds = selected.Select(r => r.Id).ToList();
                    var fullResources = new List<LearningResource>();
                    foreach (var id in selectedIds)
                    {
                        var fullResource = await _resourceRepository.GetResourceAsync(id);
                        if (fullResource != null)
                            fullResources.Add(fullResource);
                    }

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        _parameters.Set(p => p.SelectedResources = fullResources);
                        _logger.LogDebug("Reloaded {Count} resources WITH vocabulary after selection change", fullResources.Count);
                    });
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        _parameters.Set(p => p.SelectedResources = new List<LearningResource>());
                    });
                }
            });

            DebouncedSaveUserSelectionsToPreferences();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnResourcePickerChanged error");
        }
    }

    private void OnSkillPickerChanged(int selectedIndex)
    {
        try
        {
            SetState(s => s.SelectedSkillProfileIndex = selectedIndex);

            var selectedProfile = selectedIndex >= 0 && selectedIndex < State.SkillProfiles.Count
                ? State.SkillProfiles[selectedIndex]
                : null;
            _parameters.Set(p => p.SelectedSkillProfile = selectedProfile);
            DebouncedSaveUserSelectionsToPreferences();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnSkillPickerChanged error");
        }
    }

    /// <summary>
    /// PHASE 2 OPTIMIZATION: Debounced save to prevent excessive writes during rapid selection changes
    /// </summary>
    private void DebouncedSaveUserSelectionsToPreferences()
    {
        // Reset the timer - if user makes another selection within 500ms, we'll wait again
        _preferenceSaveTimer?.Stop();
        _preferenceSaveTimer?.Dispose();

        _preferenceSaveTimer = new System.Timers.Timer(500); // 500ms debounce
        _preferenceSaveTimer.AutoReset = false;
        _preferenceSaveTimer.Elapsed += (s, e) =>
        {
            // Execute on main thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
                SaveUserSelectionsToPreferences();
            });
        };
        _preferenceSaveTimer.Start();
    }

    /// <summary>
    /// Save the user's current selections to preferences for persistence across app sessions
    /// </summary>
    private void SaveUserSelectionsToPreferences()
    {
        try
        {
            // Save selected resource IDs as a comma-separated string
            if (_parameters.Value?.SelectedResources?.Any() == true)
            {
                var resourceIds = string.Join(",", _parameters.Value.SelectedResources.Select(r => r.Id.ToString()));
                Preferences.Default.Set(PREF_SELECTED_RESOURCE_IDS, resourceIds);
                _logger.LogDebug("🏴‍☠️ Saved selected resource IDs to preferences: {ResourceIds}", resourceIds);
            }
            else
            {
                Preferences.Default.Remove(PREF_SELECTED_RESOURCE_IDS);
            }

            // Save selected skill profile ID
            if (_parameters.Value?.SelectedSkillProfile != null)
            {
                Preferences.Default.Set(PREF_SELECTED_SKILL_PROFILE_ID, _parameters.Value.SelectedSkillProfile.Id);
                _logger.LogDebug("🏴‍☠️ Saved selected skill profile ID to preferences: {SkillId}", _parameters.Value.SelectedSkillProfile.Id);
            }
            else
            {
                Preferences.Default.Remove(PREF_SELECTED_SKILL_PROFILE_ID);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error saving preferences");
        }
    }

    /// <summary>
    /// Load the user's saved selections from preferences
    /// </summary>
    private async Task<(List<LearningResource> selectedResources, SkillProfile selectedSkill)> LoadUserSelectionsFromPreferences(
        List<LearningResource> availableResources,
        List<SkillProfile> availableSkills)
    {
        var selectedResources = new List<LearningResource>();
        SkillProfile selectedSkill = null;

        try
        {
            // Load selected resource IDs
            var savedResourceIds = Preferences.Default.Get(PREF_SELECTED_RESOURCE_IDS, string.Empty);
            if (!string.IsNullOrEmpty(savedResourceIds))
            {
                var resourceIds = savedResourceIds.Split(',')
                    .Where(s => int.TryParse(s.Trim(), out _))
                    .Select(s => int.Parse(s.Trim()))
                    .ToList();

                selectedResources = availableResources
                    .Where(r => resourceIds.Contains(r.Id))
                    .ToList();

                _logger.LogDebug("🏴‍☠️ Loaded {Count} selected resources from preferences", selectedResources.Count);
            }

            // Load selected skill profile ID
            var savedSkillId = Preferences.Default.Get(PREF_SELECTED_SKILL_PROFILE_ID, -1);
            if (savedSkillId >= 0)
            {
                selectedSkill = availableSkills.FirstOrDefault(s => s.Id == savedSkillId);
                _logger.LogDebug("🏴‍☠️ Loaded selected skill profile from preferences: {Title}", selectedSkill?.Title ?? "Not found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error loading preferences");
        }

        // Fallback to defaults if no valid saved selections
        if (!selectedResources.Any())
        {
            selectedResources = availableResources.Take(1).ToList();
            _logger.LogDebug("🏴‍☠️ No saved resources found, using default (first resource)");
        }

        if (selectedSkill == null)
        {
            selectedSkill = availableSkills.FirstOrDefault();
            _logger.LogDebug("🏴‍☠️ No saved skill profile found, using default (first skill)");
        }

        return (selectedResources, selectedSkill);
    }

    private void NavigateToVocabularyProgress(VocabularyFilterType filterType)
    {
        // Invoke on main thread (selection event should already be on UI thread, this is extra safety)
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _ = MauiControls.Shell.Current.GoToAsync<VocabularyProgressProps>(
                    nameof(VocabularyLearningProgressPage),
                    props =>
                    {
                        props.InitialFilter = filterType;
                        props.Title = $"Vocabulary Progress - {filterType}";
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Navigation error");
            }
        });
    }
}

class ActivityProps
{
    public List<LearningResource> Resources { get; set; } = new();
    public SkillProfile Skill { get; set; }

    /// <summary>
    /// Indicates if this activity was launched from Today's Plan.
    /// When true, activity timer will be enabled.
    /// </summary>
    public bool FromTodaysPlan { get; set; }

    /// <summary>
    /// The ID of the plan item being tracked (if from Today's Plan)
    /// </summary>
    public string? PlanItemId { get; set; }

    /// <summary>
    /// Target number of words to review (from plan).
    /// When null, uses activity's default session size.
    /// Ensures quiz loads exactly the number of words the plan promised.
    /// </summary>
    public int? TargetWordCount { get; set; }

    // Backward compatibility - returns first resource or null
    public LearningResource Resource => Resources?.FirstOrDefault();
}
