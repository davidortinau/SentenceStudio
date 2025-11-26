using MauiReactor.Parameters;
using ReactorCustomLayouts;
using MauiReactor.Shapes;
using Microsoft.Maui.Layouts;
using SentenceStudio.Pages.Clozure;
using SentenceStudio.Pages.Scene;
using SentenceStudio.Pages.Translation;
using SentenceStudio.Pages.VocabularyMatching;
using SentenceStudio.Pages.VocabularyProgress;
using SentenceStudio.Pages.VocabularyQuiz;
using SentenceStudio.Pages.Writing;
using SentenceStudio.Services.Progress;
using SentenceStudio.Pages.Controls;
using Microsoft.Extensions.Logging;

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
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        DeviceDisplay.Current.MainDisplayInfoChanged -= OnMainDisplayInfoChanged;

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

    public override VisualNode Render()
    {
        SafeAreaEdges safeEdges = DeviceDisplay.Current.MainDisplayInfo.Rotation switch
        {
            DisplayRotation.Rotation0 => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None),
            DisplayRotation.Rotation90 => new(SafeAreaRegions.All, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None),
            DisplayRotation.Rotation180 => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None),
            DisplayRotation.Rotation270 => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.All, SafeAreaRegions.None),
            _ => new(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None)
        };

        return ContentPage($"{_localize["DashboardTitle"]}",
            ToolbarItem($"{_localize["PlanRegenerateButton"] ?? "Regenerate"}")
                .Order(ToolbarItemOrder.Secondary)
                .OnClicked(() => _ = RegeneratePlanAsync()),

            VScrollView(
                VStack(
                    RenderModeToggle(),
                    State.IsTodaysPlanMode ? RenderTodaysPlanMode() : RenderChooseOwnMode()
                )
                .Padding(MyTheme.LayoutPadding)
                .Spacing(MyTheme.LayoutSpacing)
            )
            .Set(Layout.SafeAreaEdgesProperty, safeEdges)
        )
        .Set(Layout.SafeAreaEdgesProperty, safeEdges)
        .OnAppearing(LoadOrRefreshDataAsync);
    }

    VisualNode RenderModeToggle()
    {
        return new SfSegmentedControl(
                new SfSegmentItem()
                    .Text($"{_localize["ModeTodaysPlan"]}")
                    .SelectedSegmentTextColor(MyTheme.LightOnDarkBackground),
                new SfSegmentItem()
                    .Text($"{_localize["ModeChooseOwn"]}")
                    .SelectedSegmentTextColor(MyTheme.LightOnDarkBackground)
            )
            .TextStyle(new Syncfusion.Maui.Toolkit.SegmentedControl.SegmentTextStyle()
            {
                TextColor = MyTheme.Gray600
            })
            .SelectionIndicatorSettings(new Syncfusion.Maui.Toolkit.SegmentedControl.SelectionIndicatorSettings()
            {
                Background = MyTheme.PrimaryButtonBackground,
                TextColor = Colors.White
            })
            .SelectedIndex(State.IsTodaysPlanMode ? 0 : 1)
            .SegmentWidth(180)
            .BackgroundColor(MyTheme.SecondaryButtonBackground)
            .CornerRadius((float)MyTheme.Size80)
            .HeightRequest(44)
            .OnSelectionChanged((s, e) =>
            {
                var selectedIndex = e.NewIndex;
                SetState(st => st.IsTodaysPlanMode = selectedIndex == 0);
                Preferences.Default.Set(PREF_DASHBOARD_MODE, selectedIndex == 0 ? "TodaysPlan" : "ChooseOwn");

                if (selectedIndex == 0)
                {
                    _ = LoadTodaysPlanAsync();
                }
            }).HCenter();

        // return HStack(spacing: MyTheme.ComponentSpacing,
        //     // Today's Plan button
        //     Border(
        //         Label(_localize["ModeTodaysPlan"])
        //             .ThemeKey(State.IsTodaysPlanMode ? MyTheme.Body1Strong : MyTheme.Body1)
        //             .TextColor(State.IsTodaysPlanMode ? MyTheme.DarkOnLightBackground : MyTheme.SecondaryText)
        //             .Center()
        //             .Padding(MyTheme.Size120, MyTheme.Size80)
        //     )
        //     .BackgroundColor(State.IsTodaysPlanMode ? MyTheme.HighlightLightest : MyTheme.SecondaryButtonBackground)
        //     .StrokeShape(new RoundRectangle().CornerRadius(MyTheme.Size80))
        //     .StrokeThickness(0)
        //     .HStart()
        //     .HorizontalOptions(LayoutOptions.FillAndExpand)
        //     .OnTapped(() =>
        //     {
        //         SetState(s => s.IsTodaysPlanMode = true);
        //         Preferences.Default.Set(PREF_DASHBOARD_MODE, "TodaysPlan");
        //         _ = LoadTodaysPlanAsync();
        //     }),

        //     // Choose My Own button
        //     Border(
        //         Label(_localize["ModeChooseOwn"])
        //             .ThemeKey(!State.IsTodaysPlanMode ? MyTheme.Body1Strong : MyTheme.Body1)
        //             .TextColor(!State.IsTodaysPlanMode ? MyTheme.DarkOnLightBackground : MyTheme.SecondaryText)
        //             .Center()
        //             .Padding(MyTheme.Size120, MyTheme.Size80)
        //     )
        //     .BackgroundColor(!State.IsTodaysPlanMode ? MyTheme.HighlightLightest : MyTheme.SecondaryButtonBackground)
        //     .StrokeShape(new RoundRectangle().CornerRadius(MyTheme.Size80))
        //     .StrokeThickness(0)
        //     .HEnd()
        //     .HorizontalOptions(LayoutOptions.FillAndExpand)
        //     .OnTapped(() =>
        //     {
        //         SetState(s => s.IsTodaysPlanMode = false);
        //         Preferences.Default.Set(PREF_DASHBOARD_MODE, "ChooseOwn");
        //     })
        // )
        // .Padding(0, 0, 0, MyTheme.SectionSpacing);
    }

    VisualNode RenderTodaysPlanMode()
    {
        return VStack(spacing: MyTheme.LayoutSpacing,
            // Today's Plan Card
            State.IsLoadingTodaysPlan
                ? VStack(
                    ActivityIndicator()
                        .IsRunning(true)
                        .Color(MyTheme.HighlightDarkest)
                        .HeightRequest(50),
                    Label("Loading today's plan...")
                        .TextColor(MyTheme.SecondaryText)
                        .FontSize(14)
                        .HorizontalOptions(LayoutOptions.Center)
                ).Padding(MyTheme.SectionSpacing).Spacing(MyTheme.ComponentSpacing)
                : (State.TodaysPlan != null
                    ? new TodaysPlanCard()
                        .Plan(State.TodaysPlan)
                        .StreakInfo(State.StreakInfo)
                        .OnItemTapped(item => _ = OnPlanItemTapped(item))
                    :
                    VStack(spacing: MyTheme.ComponentSpacing,
                        Label("Ready to start learning?")
                            .TextColor(MyTheme.PrimaryText)
                            .FontSize(18)
                            .FontAttributes(MauiControls.FontAttributes.Bold)
                            .HorizontalOptions(LayoutOptions.Center),
                        Label("Select a resource and skill to generate your personalized learning plan.")
                            .TextColor(MyTheme.SecondaryText)
                            .FontSize(14)
                            .HorizontalOptions(LayoutOptions.Center)
                            .HorizontalTextAlignment(TextAlignment.Center),
                        Border(
                            Label("Generate Today's Plan")
                                .TextColor(MyTheme.PrimaryButtonText)
                                .FontSize(16)
                                .FontAttributes(MauiControls.FontAttributes.Bold)
                                .HorizontalOptions(LayoutOptions.Center)
                                .VerticalOptions(LayoutOptions.Center)
                                .Padding(MyTheme.Size160, MyTheme.Size120)
                        )
                        .BackgroundColor(MyTheme.PrimaryButtonBackground)
                        .StrokeShape(new RoundRectangle().CornerRadius(MyTheme.Size80))
                        .StrokeThickness(0)
                        .HorizontalOptions(LayoutOptions.Center)
                        .Margin(0, MyTheme.Size160, 0, 0)
                        .OnTapped(LoadTodaysPlanAsync)
                    )
                    .Padding(MyTheme.SectionSpacing)
                ),

            // Progress section - same as before
            RenderProgressSection()
        );
    }

    VisualNode RenderChooseOwnMode()
    {
        return VStack(spacing: MyTheme.LayoutSpacing,
                        DeviceInfo.Idiom == DeviceIdiom.Phone || (DeviceDisplay.MainDisplayInfo.Width / DeviceDisplay.MainDisplayInfo.Density) < 600 ?
                            VStack(spacing: MyTheme.LayoutSpacing,
                                Border(
                                    new SfTextInputLayout{

                                        new SfComboBox()
                                            .MinimumHeightRequest(44)
                                            .BackgroundColor(Colors.Transparent)
                                            .TextColor(MyTheme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground)
                                            .TokenItemStyle(MyTheme.ChipStyle)
                                            .ItemPadding(MyTheme.LayoutPadding)
                                            .PlaceholderText("Select resource(s)")
                                            .DropDownBackground(MyTheme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground)
                                            .ItemsSource(State.Resources)
                                            .DisplayMemberPath("Title")
                                            .SelectedItems(State.SelectedResources?.Cast<object>().ToList() ?? new List<object>())
                                            .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Multiple)
                                            .OnSelectionChanged(OnResourcesSelectionChanged)
                                    }.Hint("Learning Resource(s)")
                                    .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Outlined)
                                ).Padding(MyTheme.Size160, MyTheme.Size80),
                                Border(
                                    VStack(
                                        Label("Skill(s)"),
                                        new SfComboBox()
                                            .HeightRequest(44)
                                            .BackgroundColor(Colors.Transparent)
                                            .TextColor(MyTheme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground)
                                            .ItemPadding(MyTheme.LayoutPadding)
                                            .PlaceholderText("Select skill(s)")
                                            .DropDownBackground(MyTheme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground)
                                            .ItemsSource(State.SkillProfiles)
                                            .DisplayMemberPath("Title")
                                            .SelectedIndex(State.SkillProfiles?.Count > 0 && State.SelectedSkillProfileIndex >= 0 && State.SelectedSkillProfileIndex < State.SkillProfiles.Count ? State.SelectedSkillProfileIndex : -1)
                                            .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Single)
                                            .OnSelectionChanged(OnSkillSelectionChanged)
                                    ).Spacing(MyTheme.LayoutSpacing)
                                ).Padding(MyTheme.Size160, MyTheme.Size80)
                            ) : // Desktop
                            Grid(
                                Border(
                                    VStack(
                                        Label("Resource"),
                                        new SfComboBox()
                                            // .HeightRequest(60)
                                            .BackgroundColor(Colors.Transparent)
                                            .ShowBorder(false)
                                            // .MultiSelectionDisplayMode(Syncfusion.Maui.Inputs.ComboBoxMultiSelectionDisplayMode.Token)
                                            .TextColor(MyTheme.IsLightTheme ? MyTheme.DarkOnLightBackground : MyTheme.LightOnDarkBackground)
                                            .TokenItemStyle(MyTheme.ChipStyle)
                                            .TokensWrapMode(Syncfusion.Maui.Inputs.ComboBoxTokensWrapMode.Wrap)
                                            .EnableAutoSize(true)
                                            // .ItemPadding(MyTheme.LayoutPadding)
                                            .PlaceholderText("Select resource(s)")
                                            // .DropDownBackground(MyTheme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground)
                                            .ItemsSource(State.Resources)
                                            .DisplayMemberPath("Title")
                                            .SelectedItems(State.SelectedResources?.Cast<object>().ToList() ?? new List<object>())
                                            .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Multiple)
                                            .OnSelectionChanged(OnResourcesSelectionChanged)
                                    ).Spacing(MyTheme.LayoutSpacing)
                                ).Padding(MyTheme.Size160, MyTheme.Size80),
                                Border(
                                    VStack(
                                        Label("Resource"),
                                        new SfComboBox()
                                            .BackgroundColor(Colors.Transparent)
                                            .ShowBorder(false)
                                            .PlaceholderText("Select skill(s)")
                                            //.DropDownBackground(MyTheme.IsLightTheme ? MyTheme.LightSecondaryBackground : MyTheme.DarkSecondaryBackground)
                                            .ItemsSource(State.SkillProfiles)
                                            .DisplayMemberPath("Title")
                                            .SelectedIndex(State.SkillProfiles?.Count > 0 && State.SelectedSkillProfileIndex >= 0 && State.SelectedSkillProfileIndex < State.SkillProfiles.Count ? State.SelectedSkillProfileIndex : -1)
                                            .SelectionMode(Syncfusion.Maui.Inputs.ComboBoxSelectionMode.Single)
                                            .OnSelectionChanged(OnSkillSelectionChanged)
                                    ).Spacing(MyTheme.LayoutSpacing)
                                ).Padding(MyTheme.Size160, MyTheme.Size80).GridColumn(1)
                            ).Columns("*,*").ColumnSpacing(MyTheme.LayoutSpacing),

            // Progress section
            RenderProgressSection(),

                        // Activities
                        Label().ThemeKey(MyTheme.Title1).HStart().Text($"{_localize["Activities"]}"),
                        new HWrap(){
                            new ActivityBorder().LabelText($"{_localize["Warmup"]}").Route("warmup"),
                            new ActivityBorder().LabelText($"{_localize["DescribeAScene"]}").Route(nameof(DescribeAScenePage)),
                            new ActivityBorder().LabelText($"{_localize["Translate"]}").Route(nameof(TranslationPage)),
                            new ActivityBorder().LabelText($"{_localize["Write"]}").Route(nameof(WritingPage)),
                            new ActivityBorder().LabelText($"{_localize["Clozures"]}").Route(nameof(ClozurePage)),
                            new ActivityBorder().LabelText($"{_localize["Reading"]}").Route("reading"),
                            new ActivityBorder().LabelText("Watch").Route(nameof(SentenceStudio.Pages.VideoWatching.VideoWatchingPage)),
                            new ActivityBorder().LabelText($"{_localize["VocabularyQuiz"]}").Route(nameof(VocabularyQuizPage)),
                            new ActivityBorder().LabelText($"{_localize["VocabularyMatchingTitle"]}").Route(nameof(VocabularyMatchingPage)),
                            new ActivityBorder().LabelText($"{_localize["Shadowing"]}").Route("shadowing"),
                            new ActivityBorder().LabelText($"{_localize["HowDoYouSay"]}").Route("howdoyousay")
                        }.Spacing(MyTheme.LayoutSpacing)
        );
    }

    VisualNode RenderProgressSection()
    {
        return VStack(spacing: MyTheme.LayoutSpacing,
                        // Progress Section
                        Label($"{_localize["VocabProgress"]}")
                            .ThemeKey(MyTheme.Title1).HStart().Margin(0, MyTheme.SectionSpacing, 0, MyTheme.ComponentSpacing),
                        (State.IsLoadingProgress && !State.HasLoadedProgressOnce
                            ? VStack(
                                ActivityIndicator()
                                    .IsRunning(true)
                                    .Color(MyTheme.HighlightDarkest)
                                    .HeightRequest(50),
                                Label("Loading progress data...")
                                    .TextColor(Colors.Gray)
                                    .FontSize(14)
                                    .HorizontalOptions(LayoutOptions.Center)
                            ).Padding(MyTheme.SectionSpacing).Spacing(MyTheme.ComponentSpacing)
                            : (State.HasLoadedProgressOnce
                                ? FlexLayout(
                                    // Vocab Progress Card - always render if we have data
                                    (State.VocabSummary != null
                                        ? Border(
                                            new VocabProgressCard()
                                                .Summary(State.VocabSummary)
                                                .IsVisible(true)
                                                .OnSegmentTapped(NavigateToVocabularyProgress)
                                        )
                                        .StrokeThickness(0)
                                        .Set(Microsoft.Maui.Controls.FlexLayout.GrowProperty, 1f)
                                        .Set(Microsoft.Maui.Controls.FlexLayout.BasisProperty, new FlexBasis(0, true))
                                        .Set(Microsoft.Maui.Controls.FlexLayout.AlignSelfProperty, FlexAlignSelf.Stretch)
                                        .Set(View.MinimumWidthRequestProperty, 340d)
                                        .Margin(0, 0, MyTheme.CardMargin, MyTheme.CardMargin)
                                        : Label("No vocabulary progress data available yet. Start practicing!")
                                            .TextColor(Colors.Gray)
                                            .FontSize(14)
                                            .Padding(MyTheme.SectionSpacing)
                                            .Set(Microsoft.Maui.Controls.FlexLayout.GrowProperty, 1f)
                                            .Set(Microsoft.Maui.Controls.FlexLayout.BasisProperty, new FlexBasis(0, true))
                                            .Margin(0, 0, MyTheme.CardMargin, MyTheme.CardMargin)
                                    ),
                                    // Practice Heat Card - always render if we have data
                                    (State.PracticeHeat?.Any() == true
                                        ? Border(
                                            new PracticeStreakCard()
                                                .HeatData(State.PracticeHeat)
                                                .IsVisible(true)
                                        )
                                        .StrokeThickness(0)
                                        .Set(Microsoft.Maui.Controls.FlexLayout.GrowProperty, 1f)
                                        .Set(Microsoft.Maui.Controls.FlexLayout.BasisProperty, new FlexBasis(0, true))
                                        .Set(Microsoft.Maui.Controls.FlexLayout.AlignSelfProperty, FlexAlignSelf.Stretch)
                                        .Set(View.MinimumWidthRequestProperty, 340d)
                                        .Margin(0, 0, MyTheme.CardMargin, MyTheme.CardMargin)
                                        : Label("No practice activity data yet. Start practicing!")
                                            .TextColor(Colors.Gray)
                                            .FontSize(14)
                                            .Padding(MyTheme.SectionSpacing)
                                            .Set(Microsoft.Maui.Controls.FlexLayout.GrowProperty, 1f)
                                            .Set(Microsoft.Maui.Controls.FlexLayout.BasisProperty, new FlexBasis(0, true))
                                            .Margin(0, 0, MyTheme.CardMargin, MyTheme.CardMargin)
                                    )
                                )
                                // Always row + wrap; wrapping handles narrow widths. If you prefer vertical stacking earlier than wrap, change threshold below.
                                .Direction((State.Width / State.Density) > 600 ? FlexDirection.Row : FlexDirection.Column)
                                .Wrap(FlexWrap.Wrap)
                                .AlignItems(FlexAlignItems.Stretch)
                                .JustifyContent(FlexJustify.Start)
                                .Padding(0)
                                : ContentView().HeightRequest(0) // While loading for the first time, don't show cards
                            )
                        )
        );
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

            await Application.Current.MainPage.DisplayAlert(
                "Arrr!",
                "Failed to load today's plan. Try again, ye scallywag!",
                "Aye");
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
                            await Application.Current.MainPage.DisplayAlert(
                                "Arrr!",
                                $"The resource for this activity be missin' from the database (ID: {resourceId}). Try regeneratin' yer plan!",
                                "Aye!");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ CRITICAL ERROR loading resource from plan");
                        await Application.Current.MainPage.DisplayAlert(
                            "Shiver me timbers!",
                            "Failed to load the resource for this activity. Try again!",
                            "Aye!");
                        return;
                    }
                }
                else
                {
                    // No ResourceId in plan - fallback to selected resources (Choose My Own mode)
                    if (_parameters.Value?.SelectedResources?.Any() != true)
                    {
                        _logger.LogError("❌ No ResourceId in plan and no selected resources");
                        await Application.Current.MainPage.DisplayAlert(
                            "Ahoy!",
                            "Ye need to select at least one learning resource first, matey!",
                            "Aye!");
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
                            await Application.Current.MainPage.DisplayAlert(
                                "Avast!",
                                "No skill profiles found in the database. Create one first!",
                                "Aye!");
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
            await Application.Current.MainPage.DisplayAlert(
                "Error",
                $"Failed to start activity: {ex.Message}",
                "OK");
        }
    }

    async Task<string> HandleVideoActivity(DailyPlanItem item)
    {
        // For video activities, check if we have route parameters with URL
        if (item.RouteParameters != null && item.RouteParameters.ContainsKey("url"))
        {
            var url = item.RouteParameters["url"]?.ToString();
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    await Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred);

                    // Videos count as completed immediately when opened (no time tracking for external content)
                    await _progressService.MarkPlanItemCompleteAsync(item.Id, item.EstimatedMinutes);
                    await LoadTodaysPlanAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error opening video URL");
                    await Application.Current.MainPage.DisplayAlert(
                        "Arrr!",
                        "Failed to open the video. Check your internet connection!",
                        "Aye");
                }
            }
        }
        return null; // Don't navigate to a page
    }

    // Selection handlers that are resilient to chip removals (e.AddedItems can be null)
    private void OnResourcesSelectionChanged(object sender, Syncfusion.Maui.Inputs.SelectionChangedEventArgs e)
    {
        try
        {
            var combo = sender as Syncfusion.Maui.Inputs.SfComboBox;
            var selected = combo?.SelectedItems?.OfType<LearningResource>().ToList() ?? new List<LearningResource>();

            SetState(s =>
            {
                s.SelectedResources = selected;
                s.SelectedResourceIndex = selected.Any() ? State.Resources.IndexOf(selected.First()) : -1;
            });

            // CRITICAL FIX: Reload selected resources WITH vocabulary for activities
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
                        _logger.LogDebug("🏴‍☠️ Reloaded {Count} resources WITH vocabulary after selection change", fullResources.Count);
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
            _logger.LogError(ex, "OnResourcesSelectionChanged error");
        }
    }

    private void OnSkillSelectionChanged(object sender, Syncfusion.Maui.Inputs.SelectionChangedEventArgs e)
    {
        try
        {
            var combo = sender as Syncfusion.Maui.Inputs.SfComboBox;
            // Single selection mode: use SelectedItem
            var selectedProfile = combo?.SelectedItem as SkillProfile;
            var index = selectedProfile != null ? State.SkillProfiles.IndexOf(selectedProfile) : -1;

            SetState(s => s.SelectedSkillProfileIndex = index);
            _parameters.Set(p => p.SelectedSkillProfile = selectedProfile);
            DebouncedSaveUserSelectionsToPreferences();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnSkillSelectionChanged error");
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

public partial class ActivityBorder : MauiReactor.Component
{
    [Inject]
    ILogger<ActivityBorder> _logger;

    [Prop]
    string _labelText;

    [Prop]
    string _route;

    [Param] IParameter<DashboardParameters> _parameters;

    public override VisualNode Render() =>
        Border(
            Grid(
                Label()
                    .VerticalOptions(LayoutOptions.Center)
                    .HorizontalOptions(LayoutOptions.Center)
                    .Text($"{_labelText}")
            )
            .WidthRequest(DeviceInfo.Idiom == DeviceIdiom.Phone ? 140 : 200)
            .HeightRequest(DeviceInfo.Idiom == DeviceIdiom.Phone ? 60 : 80)
        )
        .StrokeShape(Rectangle())
        .StrokeThickness(1)
        .HorizontalOptions(LayoutOptions.Start)
        .OnTapped(async () =>
        {
            // 🏴‍☠️ Validate that we have the required selections before navigating
            if (_parameters.Value.SelectedResources?.Any() != true)
            {
                await Application.Current.MainPage.DisplayAlert(
                    "Ahoy!",
                    "Ye need to select at least one learning resource before startin' this activity, matey!",
                    "Aye, Captain!");
                return;
            }

            if (_parameters.Value.SelectedSkillProfile == null)
            {
                await Application.Current.MainPage.DisplayAlert(
                    "Avast!",
                    "Choose yer skill profile first, ye scallywag!",
                    "Aye, Captain!");
                return;
            }

            _logger.LogDebug("🏴‍☠️ ActivityBorder: Navigating to {Route} with {ResourceCount} resources and skill {SkillTitle}", _route, _parameters.Value.SelectedResources.Count, _parameters.Value.SelectedSkillProfile.Title);

            await MauiControls.Shell.Current.GoToAsync<ActivityProps>(
                _route,
                props =>
                {
                    props.Resources = _parameters.Value.SelectedResources?.ToList() ?? new List<LearningResource>();
                    props.Skill = _parameters.Value.SelectedSkillProfile;
                }
            );
        });
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